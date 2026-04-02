// Copyright (c) OrgName. All rights reserved.

using System.Text;
using System.Text.Json;
using Npgsql;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios;

/// <summary>
///     Verifies that per-(topic, partitionKey) message ordering is preserved during
///     publisher rebalancing, crash recovery, and continuous insertion scenarios.
///     These tests combine multiple publishers with ordering verification — a gap
///     in the existing test suite where rebalancing and ordering are tested separately.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public class RebalanceOrderingTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    public RebalanceOrderingTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task ScaleUp_OrderingPreservedDuringRebalance()
    {
        var topic = OutboxTestHelper.UniqueTopic("rebal-order");
        const string partitionKey = "rebal-order-key";
        const int messageCount = 200;
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Start publisher A — owns all 64 partitions
        var (hostA, _) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);
        await hostA.StartAsync();

        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);

            return owners.Values.Count(v => v != null) == 64;
        }, TimeSpan.FromSeconds(15), message: "A should own all 64 partitions");

        _output.WriteLine("Publisher A owns all 64 partitions");

        // Insert 200 ordered messages
        await OutboxTestHelper.InsertMessagesAsync(
            _infra.ConnectionString, messageCount, topic, partitionKey, startOrdinal: 0);
        _output.WriteLine($"Inserted {messageCount} messages for partition key '{partitionKey}'");

        // Wait briefly for A to start processing, then start publisher B mid-flight
        await Task.Delay(TimeSpan.FromSeconds(1));

        var (hostB, _) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);

        try
        {
            await hostB.StartAsync();
            _output.WriteLine("Publisher B started — rebalance triggered");

            // Wait for all messages to drain
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString)
                    .ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(60), message: "All messages should drain with 2 publishers");

            _output.WriteLine("All messages drained");

            // Consume and verify ordering
            var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, messageCount, TimeSpan.FromSeconds(15));

            var indices = ExtractIndices(consumed);
            var deduped = Deduplicate(indices);

            _output.WriteLine($"Consumed {consumed.Count} messages, {deduped.Count} unique");
            _output.WriteLine($"First 20 indices: {string.Join(", ", deduped.Take(20))}");

            Assert.Equal(messageCount, deduped.Count);
            AssertStrictOrdering(deduped, partitionKey);

            _output.WriteLine("Ordering verified: all messages in order through rebalance");
        }
        finally
        {
            await hostB.StopAsync();
            hostB.Dispose();
            await hostA.StopAsync();
            hostA.Dispose();
        }
    }

    [Fact]
    public async Task MultiplePartitionKeys_OrderingPreservedPerKeyWithConcurrentPublishers()
    {
        var topic = OutboxTestHelper.UniqueTopic("multi-key-order");
        const int keysCount = 5;
        const int messagesPerKey = 50;
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Start publishers A and B
        var (hostA, _) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);
        await hostA.StartAsync();

        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);

            return owners.Values.Count(v => v != null) == 64;
        }, TimeSpan.FromSeconds(15), message: "A should own all 64 partitions");

        var (hostB, _) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);
        await hostB.StartAsync();

        // Wait for rebalance to stabilize (~32/32)
        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);
            var grouped = owners.Values.Where(v => v != null).GroupBy(v => v).ToList();

            return grouped.Count == 2 && grouped.All(g => g.Count() >= 24);
        }, TimeSpan.FromSeconds(30), message: "Partitions should be split roughly 32/32");

        _output.WriteLine("Two publishers running with ~32/32 partition split");

        try
        {
            // Insert messages for each partition key with sequential indices
            var partitionKeys = Enumerable.Range(0, keysCount)
                .Select(i => $"multi-key-{i}")
                .ToList();

            foreach (var key in partitionKeys)
            {
                await OutboxTestHelper.InsertMessagesAsync(
                    _infra.ConnectionString, messagesPerKey, topic, key, startOrdinal: 0);
            }

            var totalMessages = keysCount * messagesPerKey;
            _output.WriteLine($"Inserted {totalMessages} messages across {keysCount} partition keys");

            // Wait for all messages to drain
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString)
                    .ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(60), message: "All messages should drain");

            // Consume all messages
            var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, totalMessages, TimeSpan.FromSeconds(15));

            _output.WriteLine($"Consumed {consumed.Count} total messages");

            // Verify ordering per partition key
            foreach (var key in partitionKeys)
            {
                var keyMessages = consumed.Where(m => m.Key == key).ToList();
                var indices = ExtractIndices(keyMessages);
                var deduped = Deduplicate(indices);

                _output.WriteLine($"  Key '{key}': {keyMessages.Count} consumed, {deduped.Count} unique");

                Assert.Equal(messagesPerKey, deduped.Count);
                AssertStrictOrdering(deduped, key);
            }

            _output.WriteLine("Ordering verified for all partition keys independently");
        }
        finally
        {
            await hostB.StopAsync();
            hostB.Dispose();
            await hostA.StopAsync();
            hostA.Dispose();
        }
    }

    [Fact]
    public async Task CrashRecovery_OrderingPreservedAfterPublisherTakeover()
    {
        var topic = OutboxTestHelper.UniqueTopic("crash-order");
        const string partitionKey = "crash-order-key";
        const int messageCount = 100;
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Start publisher A
        var (hostA, _) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);
        await hostA.StartAsync();

        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);

            return owners.Values.Count(v => v != null) == 64;
        }, TimeSpan.FromSeconds(15), message: "A should own all 64 partitions");

        var publisherIdA = (await OutboxTestHelper.GetPublisherIdsAsync(_infra.ConnectionString)).First();
        _output.WriteLine($"Publisher A registered as {publisherIdA}");

        // Insert 100 ordered messages
        await OutboxTestHelper.InsertMessagesAsync(
            _infra.ConnectionString, messageCount, topic, partitionKey, startOrdinal: 0);
        _output.WriteLine($"Inserted {messageCount} messages");

        // Let A process some messages
        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var count = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);

            return count <= 60;
        }, TimeSpan.FromSeconds(30), message: "A should process at least 40 messages");

        var remainingBeforeKill = await OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString);
        _output.WriteLine($"Remaining before kill: {remainingBeforeKill}");

        // Simulate SIGKILL: stop A gracefully, then re-create stale publisher state
        await hostA.StopAsync();
        hostA.Dispose();

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

            // Assign partitions to the dead publisher
            await using var assignCmd = new NpgsqlCommand(@"
                UPDATE outbox_partitions SET owner_publisher_id = @id, owned_since_utc = clock_timestamp()
                WHERE outbox_table_name = 'outbox'", conn);
            assignCmd.Parameters.AddWithValue("@id", publisherIdA);
            await assignCmd.ExecuteNonQueryAsync();
        }

        _output.WriteLine("Simulated SIGKILL — stale publisher state restored");

        // Start publisher B
        var (hostB, _) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);

        try
        {
            await hostB.StartAsync();
            _output.WriteLine("Publisher B started");

            // Wait for B to claim orphaned partitions and drain remaining messages
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString)
                    .ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(60), message: "B should drain all remaining messages");

            _output.WriteLine("All messages drained");

            // Consume ALL messages from Kafka and verify ordering.
            // Request more than messageCount because at-least-once delivery during crash
            // recovery can produce duplicates (A published some, B re-publishes overlapping ones).
            var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, messageCount * 2, TimeSpan.FromSeconds(15));

            var indices = ExtractIndices(consumed);
            var deduped = Deduplicate(indices);

            _output.WriteLine($"Consumed {consumed.Count} messages, {deduped.Count} unique");
            _output.WriteLine($"First 20 indices: {string.Join(", ", deduped.Take(20))}");

            Assert.Equal(messageCount, deduped.Count);
            AssertStrictOrdering(deduped, partitionKey);

            _output.WriteLine("Ordering verified: all messages in order through crash recovery");
        }
        finally
        {
            await hostB.StopAsync();
            hostB.Dispose();
        }
    }

    [Fact]
    public async Task ContinuousInsertion_OrderingPreservedDuringRebalance()
    {
        var topic = OutboxTestHelper.UniqueTopic("continuous-order");
        const string partitionKey = "continuous-key";
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Start publisher A
        var (hostA, _) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);
        await hostA.StartAsync();

        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);

            return owners.Values.Count(v => v != null) == 64;
        }, TimeSpan.FromSeconds(15), message: "A should own all 64 partitions");

        _output.WriteLine("Publisher A owns all 64 partitions");

        // Start background insertion task
        using var insertionCts = new CancellationTokenSource();
        var insertedCount = 0;

        var insertionTask = Task.Run(async () =>
        {
            await using var conn = new NpgsqlConnection(_infra.ConnectionString);
            await conn.OpenAsync();

            while (!insertionCts.Token.IsCancellationRequested)
            {
                var ordinal = Interlocked.Increment(ref insertedCount) - 1;

                try
                {
                    var sql = @"
                        INSERT INTO outbox (topic_name, partition_key, event_type, payload,
                                            event_datetime_utc, event_ordinal)
                        VALUES (@topic, @key, 'TestEvent', @payload, clock_timestamp(), @ordinal)";

                    await using var cmd = new NpgsqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@topic", topic);
                    cmd.Parameters.AddWithValue("@key", partitionKey);
                    cmd.Parameters.Add(new NpgsqlParameter("@payload", NpgsqlTypes.NpgsqlDbType.Bytea)
                    {
                        Value = Encoding.UTF8.GetBytes($"{{\"index\":{ordinal}}}")
                    });
                    cmd.Parameters.AddWithValue("@ordinal", (short)(ordinal % short.MaxValue));
                    await cmd.ExecuteNonQueryAsync(insertionCts.Token);

                    await Task.Delay(TimeSpan.FromMilliseconds(50), insertionCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, insertionCts.Token);

        try
        {
            // Let A process for 2 seconds
            await Task.Delay(TimeSpan.FromSeconds(2));
            _output.WriteLine($"After 2s: {insertedCount} messages inserted");

            // Start publisher B — triggers rebalance
            var (hostB, _) = OutboxTestHelper.BuildPublisherHost(
                _infra.ConnectionString, _infra.BootstrapServers);

            try
            {
                await hostB.StartAsync();
                _output.WriteLine("Publisher B started — rebalance triggered");

                // Continue inserting for 4 more seconds during rebalance
                await Task.Delay(TimeSpan.FromSeconds(4));

                // Stop insertion
                await insertionCts.CancelAsync();
                await insertionTask;

                var totalInserted = insertedCount;
                _output.WriteLine($"Stopped insertion. Total inserted: {totalInserted}");

                // Wait for all messages to drain
                await OutboxTestHelper.WaitUntilAsync(
                    () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString)
                        .ContinueWith(t => t.Result == 0),
                    TimeSpan.FromSeconds(60), message: "All messages should drain");

                _output.WriteLine("All messages drained");

                // Consume and verify ordering
                var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                    _infra.BootstrapServers, topic, totalInserted, TimeSpan.FromSeconds(15));

                var indices = ExtractIndices(consumed);
                var deduped = Deduplicate(indices);

                _output.WriteLine($"Consumed {consumed.Count} messages, {deduped.Count} unique");
                _output.WriteLine($"First 20 indices: {string.Join(", ", deduped.Take(20))}");

                Assert.Equal(totalInserted, deduped.Count);
                AssertStrictOrdering(deduped, partitionKey);

                _output.WriteLine("Ordering verified: all messages in order through continuous insertion + rebalance");
            }
            finally
            {
                await hostB.StopAsync();
                hostB.Dispose();
            }
        }
        finally
        {
            if (!insertionCts.IsCancellationRequested)
            {
                await insertionCts.CancelAsync();
                await insertionTask;
            }

            await hostA.StopAsync();
            hostA.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentTransactions_OrderingPreservedThroughPublisherPipeline()
    {
        // Scenario:
        //   1. Publisher is running and processing messages
        //   2. Txn #1 (slow) inserts message A with earlier timestamp — stays uncommitted
        //   3. Txn #2 (fast) inserts messages B, C with later timestamps — commits immediately
        //   4. The version ceiling (xmin < pg_snapshot_xmin) must prevent B, C from being
        //      published before A's transaction commits
        //   5. After Txn #1 commits, all 3 messages should be published in order: A, B, C

        var topic = OutboxTestHelper.UniqueTopic("concurrent-txn");
        const string partitionKey = "concurrent-txn-key";
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        // Fixed timestamps: A is definitively earlier than B, C
        var timestampA = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var timestampB = new DateTimeOffset(2025, 6, 1, 12, 0, 1, TimeSpan.Zero);
        var timestampC = new DateTimeOffset(2025, 6, 1, 12, 0, 2, TimeSpan.Zero);

        // Synchronization gates
        var txn1Inserted = new SemaphoreSlim(0, 1);
        var txn2Committed = new SemaphoreSlim(0, 1);
        var proceedToCommitTxn1 = new SemaphoreSlim(0, 1);

        // Start publisher — it will be polling for messages
        var (host, _) = OutboxTestHelper.BuildPublisherHost(
            _infra.ConnectionString, _infra.BootstrapServers);
        await host.StartAsync();

        await OutboxTestHelper.WaitUntilAsync(async () =>
        {
            var owners = await OutboxTestHelper.GetPartitionOwnersAsync(_infra.ConnectionString);

            return owners.Values.Count(v => v != null) == 64;
        }, TimeSpan.FromSeconds(15), message: "Publisher should own all 64 partitions");

        _output.WriteLine("Publisher running and owns all partitions");

        try
        {
            // Txn #1: insert message A (earlier timestamp) — leave uncommitted
            var txn1Task = Task.Run(async () =>
            {
                await using var conn = new NpgsqlConnection(_infra.ConnectionString);
                await conn.OpenAsync();
                await using var tx = await conn.BeginTransactionAsync();

                await InsertInTransactionAsync(conn, tx, topic, partitionKey,
                    eventIndex: 0, ordinal: 0, timestamp: timestampA);

                txn1Inserted.Release();

                // Wait until test tells us to commit
                await proceedToCommitTxn1.WaitAsync();
                await tx.CommitAsync();
            });

            // Wait for Txn #1 to insert (but not commit)
            await txn1Inserted.WaitAsync();
            _output.WriteLine("Txn #1: message A inserted (uncommitted, ts=12:00:00)");

            // Txn #2: insert messages B, C (later timestamps) — commit immediately
            var txn2Task = Task.Run(async () =>
            {
                await using var conn = new NpgsqlConnection(_infra.ConnectionString);
                await conn.OpenAsync();
                await using var tx = await conn.BeginTransactionAsync();

                await InsertInTransactionAsync(conn, tx, topic, partitionKey,
                    eventIndex: 1, ordinal: 1, timestamp: timestampB);
                await InsertInTransactionAsync(conn, tx, topic, partitionKey,
                    eventIndex: 2, ordinal: 2, timestamp: timestampC);

                await tx.CommitAsync();
                txn2Committed.Release();
            });

            await txn2Committed.WaitAsync();
            _output.WriteLine("Txn #2: messages B, C committed (ts=12:00:01, 12:00:02)");

            // Give the publisher several poll cycles to potentially pick up B, C
            // (the version ceiling should prevent this)
            await Task.Delay(TimeSpan.FromSeconds(3));

            // Check: nothing should have been published yet (B, C withheld by version ceiling)
            var prematureMessages = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 1, TimeSpan.FromSeconds(2));
            _output.WriteLine($"Messages published while Txn #1 open: {prematureMessages.Count}");
            Assert.Empty(prematureMessages);

            // Now commit Txn #1 — all 3 messages become visible
            proceedToCommitTxn1.Release();
            await Task.WhenAll(txn1Task, txn2Task);
            _output.WriteLine("Txn #1: committed — all messages now visible");

            // Wait for all messages to drain from outbox
            await OutboxTestHelper.WaitUntilAsync(
                () => OutboxTestHelper.GetOutboxCountAsync(_infra.ConnectionString)
                    .ContinueWith(t => t.Result == 0),
                TimeSpan.FromSeconds(30), message: "All 3 messages should drain");

            _output.WriteLine("All messages drained from outbox");

            // Consume from Kafka and verify ordering
            var consumed = await OutboxTestHelper.ConsumeMessagesAsync(
                _infra.BootstrapServers, topic, 3, TimeSpan.FromSeconds(10));

            var indices = ExtractIndices(consumed);
            var deduped = Deduplicate(indices);

            _output.WriteLine($"Consumed {consumed.Count} messages, {deduped.Count} unique");
            _output.WriteLine($"Indices: {string.Join(", ", deduped)}");

            Assert.Equal(3, deduped.Count);
            AssertStrictOrdering(deduped, partitionKey);

            _output.WriteLine("Ordering verified: A (index 0) before B (index 1) before C (index 2)");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static async Task InsertInTransactionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        string topic, string partitionKey, int eventIndex, int ordinal,
        DateTimeOffset timestamp)
    {
        const string sql = @"
            INSERT INTO outbox (topic_name, partition_key, event_type, payload,
                                event_datetime_utc, event_ordinal)
            VALUES (@topic, @key, 'TestEvent', @payload, @ts, @ordinal)";

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@topic", topic);
        cmd.Parameters.AddWithValue("@key", partitionKey);
        cmd.Parameters.Add(new NpgsqlParameter("@payload", NpgsqlTypes.NpgsqlDbType.Bytea)
        {
            Value = Encoding.UTF8.GetBytes($"{{\"index\":{eventIndex}}}")
        });
        cmd.Parameters.AddWithValue("@ts", timestamp);
        cmd.Parameters.AddWithValue("@ordinal", (short)ordinal);
        await cmd.ExecuteNonQueryAsync();
    }

    private static List<int> ExtractIndices(IEnumerable<ConsumedMessage> messages)
    {
        return messages
            .Select(m =>
            {
                var doc = JsonDocument.Parse(m.Value);

                return doc.RootElement.GetProperty("index").GetInt32();
            })
            .ToList();
    }

    private static List<int> Deduplicate(List<int> indices)
    {
        var deduped = new List<int>();
        var seen = new HashSet<int>();

        foreach (var idx in indices)
        {
            if (seen.Add(idx))
                deduped.Add(idx);
        }

        return deduped;
    }

    private static void AssertStrictOrdering(List<int> deduped, string partitionKey)
    {
        for (var i = 1; i < deduped.Count; i++)
        {
            Assert.True(deduped[i] > deduped[i - 1],
                $"Ordering violation for partition key '{partitionKey}': " +
                $"index {deduped[i]} came after {deduped[i - 1]} at position {i}");
        }
    }
}
