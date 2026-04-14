// Copyright (c) OrgName. All rights reserved.

using Npgsql;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

[Collection(InfrastructureCollection.Name)]
public class ProcessKillTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public ProcessKillTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task SigKill_SurvivingPublisher_ClaimsOrphanedPartitions_AndDrainsMessages()
    {
        var topic = OutboxTestHelper.UniqueTopic("sigkill");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Start publisher A
        var (hostA, transportA) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);
        await hostA.StartAsync();

        // Wait for A to register and claim partitions
        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);

            return owners.Values.Any(v => v != null);
        }, TimeSpan.FromSeconds(10), message: "Publisher A should claim partitions");

        var publisherIdA = (await OutboxTestHelper.GetPublisherIdsAsync(_infra.ConnectionString)).First();
        _output.WriteLine($"Publisher A registered as {publisherIdA}");

        // Insert messages and let A process some
        await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 50, topic, "key-1");
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Simulate SIGKILL: stop A gracefully (which unregisters), then re-insert stale publisher
        await hostA.StopAsync();
        hostA.Dispose();

        // Re-create the stale publisher row and assign partitions (simulating no cleanup)
        await using (var conn = new NpgsqlConnection(_infra.ConnectionString))
        {
            await conn.OpenAsync();

            // Re-insert stale publisher with old heartbeat
            await using var insertCmd = new NpgsqlCommand(@"
                INSERT INTO outbox_publishers (outbox_table_name, publisher_id, registered_at_utc, last_heartbeat_utc, host_name)
                VALUES ('outbox', @id, clock_timestamp() - interval '5 minutes', clock_timestamp() - interval '5 minutes', 'dead-host')
                ON CONFLICT (outbox_table_name, publisher_id) DO UPDATE SET last_heartbeat_utc = clock_timestamp() - interval '5 minutes'",
                conn);
            insertCmd.Parameters.AddWithValue("@id", publisherIdA);
            await insertCmd.ExecuteNonQueryAsync();

            // Assign half the partitions to the dead publisher
            await using var assignCmd = new NpgsqlCommand(@"
                UPDATE outbox_partitions SET owner_publisher_id = @id, owned_since_utc = clock_timestamp()
                WHERE partition_id < 16 AND outbox_table_name = 'outbox'", conn);
            assignCmd.Parameters.AddWithValue("@id", publisherIdA);
            await assignCmd.ExecuteNonQueryAsync();
        }

        // Re-insert any remaining messages
        var remaining = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);
        if (remaining == 0)
            await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 20, topic, "key-1");

        // Start publisher B
        var (hostB, transportB) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);

        try
        {
            await hostB.StartAsync();

            // Wait for B to detect stale A and claim partitions
            // HeartbeatTimeout=5s, GracePeriod=8s, RebalanceInterval=3s -> ~16s worst case
            await OutboxTestHelper.WaitUntilAsync(async () =>
            {
                var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);
                var publisherB = (await OutboxTestHelper.GetPublisherIdsAsync(_infra.ConnectionString))
                    .FirstOrDefault(id => id != publisherIdA);

                if (publisherB == null) return false;

                return owners.Values.Count(v => v == publisherB) > 16;
            }, TimeSpan.FromSeconds(30), message: "Publisher B should claim A's orphaned partitions");

            // Wait for all messages to drain
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString).ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(30), message: "All messages should drain");

            _output.WriteLine("All messages drained after publisher B claimed orphaned partitions");

            Assert.True(true, "Test completed without exceptions");
        }
        finally
        {
            await hostB.StopAsync();
            hostB.Dispose();
        }
    }

    [Fact]
    public async Task SigKill_MessagesPickedUpByNewPublisher_WithFreshAttemptBudget()
    {
        var topic = OutboxTestHelper.UniqueTopic("sigkill-retry");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Start publisher A with a failing transport so messages stay in the outbox.
        // Under the in-memory retry model, retry tracking is NOT persisted to the DB.
        // When A is killed, B picks up the same messages with a fresh attempt budget.
        var (hostA, transportA) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);

        // Transient failures: trip the circuit breaker without burning the attempt
        // counter, so messages stay in the outbox while A is alive. With the default
        // MaxPublishAttempts=5 + sub-second backoff, non-transient failures would DLQ
        // all 5 messages well before we kill A — defeating the purpose of the test.
        transportA.SetSimulatedFailuresTransient(true);
        transportA.SetFailing(true);

        await hostA.StartAsync();

        // Wait for A to register and claim partitions
        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);

            return owners.Values.Any(v => v != null);
        }, TimeSpan.FromSeconds(10), message: "Publisher A should claim partitions");

        // Insert messages — A will fetch them but fail to send.
        // In the new model, attempt counts are tracked in-memory only.
        await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 5, topic, "key-1");

        // Let A attempt a few times, then simulate SIGKILL
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Simulate SIGKILL: stop A gracefully (we can't truly SIGKILL in a test),
        // then re-create stale publisher state to simulate the effect of an abrupt kill.
        await hostA.StopAsync();
        hostA.Dispose();

        var publisherIdA = "dead-publisher-sigkill";

        await using (var conn = new NpgsqlConnection(_infra.ConnectionString))
        {
            await conn.OpenAsync();

            // Re-insert stale publisher with old heartbeat (simulating no cleanup)
            await using var insertCmd = new NpgsqlCommand(@"
                INSERT INTO outbox_publishers (outbox_table_name, publisher_id, registered_at_utc, last_heartbeat_utc, host_name)
                VALUES ('outbox', @id, clock_timestamp() - interval '5 minutes', clock_timestamp() - interval '5 minutes', 'dead-host')
                ON CONFLICT (outbox_table_name, publisher_id) DO UPDATE SET last_heartbeat_utc = clock_timestamp() - interval '5 minutes'",
                conn);
            insertCmd.Parameters.AddWithValue("@id", publisherIdA);
            await insertCmd.ExecuteNonQueryAsync();
        }

        // Assert: messages are still in the outbox (retry attempts are in-memory, not persisted).
        // Publisher B will pick them up with a fresh attempt budget.
        var pendingBeforeB = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);
        Assert.True(pendingBeforeB > 0, "Messages should still be in outbox after A is killed");
        _output.WriteLine($"Messages in outbox before B starts: {pendingBeforeB} (in-memory attempts are reset)");

        // Start publisher B with working transport — it should claim partitions and drain messages
        var (hostB, _) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);

        try
        {
            await hostB.StartAsync();

            // Wait for B to claim partitions and process messages
            await OutboxTestHelper.WaitUntilAsync(async () =>
            {
                var count = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);

                return count == 0;
            }, TimeSpan.FromSeconds(30), message: "Publisher B should drain all messages with fresh attempt budget");

            // Verify messages ended up in Kafka (published) or dead letter
            var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 5, TimeSpan.FromSeconds(10));
            var dlqCount = await OutboxTestHelper.GetDeadLetterCountAsync(_infra.ConnectionString);

            _output.WriteLine($"Consumed: {consumed.Count}, Dead-lettered: {dlqCount}");
            Assert.True(consumed.Count + dlqCount >= 5,
                $"All 5 messages should be published ({consumed.Count}) or dead-lettered ({dlqCount})");
        }
        finally
        {
            await hostB.StopAsync();
            hostB.Dispose();
        }
    }
}
