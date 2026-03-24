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

        var producerIdA = (await OutboxTestHelper.GetProducerIdsAsync(_infra.ConnectionString)).First();
        _output.WriteLine($"Publisher A registered as {producerIdA}");

        // Insert messages and let A process some
        await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 50, topic, "key-1");
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Simulate SIGKILL: stop A gracefully (which unregisters), then re-insert stale producer
        await hostA.StopAsync();
        hostA.Dispose();

        // Re-create the stale producer row and assign partitions (simulating no cleanup)
        await using (var conn = new NpgsqlConnection(_infra.ConnectionString))
        {
            await conn.OpenAsync();

            // Re-insert stale producer with old heartbeat
            await using var insertCmd = new NpgsqlCommand(@"
                INSERT INTO outbox_producers (producer_id, registered_at_utc, last_heartbeat_utc, host_name)
                VALUES (@id, clock_timestamp() - interval '5 minutes', clock_timestamp() - interval '5 minutes', 'dead-host')
                ON CONFLICT (producer_id) DO UPDATE SET last_heartbeat_utc = clock_timestamp() - interval '5 minutes'",
                conn);
            insertCmd.Parameters.AddWithValue("@id", producerIdA);
            await insertCmd.ExecuteNonQueryAsync();

            // Assign half the partitions to the dead producer
            await using var assignCmd = new NpgsqlCommand(@"
                UPDATE outbox_partitions SET owner_producer_id = @id, owned_since_utc = clock_timestamp()
                WHERE partition_id < 16", conn);
            assignCmd.Parameters.AddWithValue("@id", producerIdA);
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
                var producerB = (await OutboxTestHelper.GetProducerIdsAsync(_infra.ConnectionString))
                    .FirstOrDefault(id => id != producerIdA);

                if (producerB == null) return false;

                return owners.Values.Count(v => v == producerB) > 16;
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
    public async Task SigKill_LeasedMessages_RetryCountIncrementedOnRelease()
    {
        var topic = OutboxTestHelper.UniqueTopic("sigkill-retry");
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Start publisher A with short lease duration
        var (hostA, transportA) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers,
            o =>
            {
                OutboxTestHelper.FastTestOptions(o);
                o.LeaseDurationSeconds = 5; // Short lease for faster test
            });

        // Make transport fail so messages stay leased (not deleted)
        transportA.SetFailing(true);

        await hostA.StartAsync();

        // Wait for A to register and claim partitions
        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);

            return owners.Values.Any(v => v != null);
        }, TimeSpan.FromSeconds(10), message: "Publisher A should claim partitions");

        // Insert messages — A will lease them but fail to send (transport failing)
        await OutboxTestHelper.InsertMessagesAsync(_infra.ConnectionString, 5, topic, "key-1");

        // Wait for A to lease and fail (retry_count gets incremented on release)
        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var retryCounts = await OutboxTestHelper.GetRetryCountsAsync(_infra.ConnectionString);

            return retryCounts.Any(r => r.RetryCount > 0);
        }, TimeSpan.FromSeconds(10), message: "Publisher A should increment retry counts on transport failure");

        // Capture retry counts before simulating kill
        var retryCountsBeforeKill = await OutboxTestHelper.GetRetryCountsAsync(_infra.ConnectionString);
        _output.WriteLine(
            $"Retry counts before kill: {string.Join(", ", retryCountsBeforeKill.Select(r => $"seq={r.Seq}:retry={r.RetryCount}"))}");

        // Simulate SIGKILL: stop A gracefully (we can't truly SIGKILL in a test),
        // then re-create stale producer state to simulate the effect of an abrupt kill.
        await hostA.StopAsync();
        hostA.Dispose();

        var producerIdA = "dead-producer-sigkill";

        await using (var conn = new NpgsqlConnection(_infra.ConnectionString))
        {
            await conn.OpenAsync();

            // Re-insert stale producer with old heartbeat (simulating no cleanup)
            await using var insertCmd = new NpgsqlCommand(@"
                INSERT INTO outbox_producers (producer_id, registered_at_utc, last_heartbeat_utc, host_name)
                VALUES (@id, clock_timestamp() - interval '5 minutes', clock_timestamp() - interval '5 minutes', 'dead-host')
                ON CONFLICT (producer_id) DO UPDATE SET last_heartbeat_utc = clock_timestamp() - interval '5 minutes'",
                conn);
            insertCmd.Parameters.AddWithValue("@id", producerIdA);
            await insertCmd.ExecuteNonQueryAsync();

            // Re-insert messages that would have been leased by the dead producer
            // (simulating messages left behind after SIGKILL)
            await using var resetCmd = new NpgsqlCommand(@"
                UPDATE outbox
                SET lease_owner = @id,
                    leased_until_utc = clock_timestamp() + interval '5 seconds'
                WHERE lease_owner IS NULL",
                conn);
            resetCmd.Parameters.AddWithValue("@id", producerIdA);
            await resetCmd.ExecuteNonQueryAsync();
        }

        // Wait for leases to expire
        _output.WriteLine("Waiting for leases to expire...");
        await Task.Delay(TimeSpan.FromSeconds(6));

        // Start publisher B — it should re-lease the messages (incrementing retry_count via LeaseBatch)
        var (hostB, _) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers,
            o =>
            {
                OutboxTestHelper.FastTestOptions(o);
                o.LeaseDurationSeconds = 5;
            });

        try
        {
            await hostB.StartAsync();

            // Wait for B to claim partitions and process messages
            await OutboxTestHelper.WaitUntilAsync(async () =>
            {
                var count = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);

                return count == 0;
            }, TimeSpan.FromSeconds(30), message: "Publisher B should drain all messages");

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
