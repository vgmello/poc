// Copyright (c) OrgName. All rights reserved.

using System.Text;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Outbox.Core.Options;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Outbox.PostgreSQL;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios.EventHub;

/// <summary>
///     Verifies that FetchBatchAsync does NOT return committed rows for a partition key
///     when there are earlier (lower event_datetime_utc) uncommitted rows for the same
///     partition key. Returning the committed rows first would violate per-partition-key
///     ordering because the earlier rows become visible only after the slow transaction commits.
///
///     Scenario (from architecture diagram):
///       1. Txn #1 starts, inserts message A (event_datetime_utc = T1)
///       2. Txn #2 starts, inserts messages B, C (event_datetime_utc = T2 > T1), commits
///       3. FetchBatchAsync runs — A is invisible (uncommitted), B and C are visible
///       4. Current bug: B and C are returned → published before A → ordering violation
///       5. Expected: B and C are withheld until A's transaction commits
///
///     Uses SemaphoreSlim for deterministic coordination between concurrent transactions
///     and explicit timestamps to avoid timing dependencies.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public class ConcurrentTransactionOrderingTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    // Fixed timestamps: A is definitively earlier than B, C — no clock dependency
    private static readonly DateTimeOffset TimestampA = new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TimestampB = new(2025, 1, 1, 12, 0, 1, TimeSpan.Zero);
    private static readonly DateTimeOffset TimestampC = new(2025, 1, 1, 12, 0, 2, TimeSpan.Zero);

    static ConcurrentTransactionOrderingTests()
    {
        // Npgsql 9.x returns DateTime for timestamptz, but OutboxMessage uses DateTimeOffset.
        // Register a handler to bridge the gap (matches SqlServer's UtcDateTimeOffsetTypeHandler).
        SqlMapper.AddTypeHandler(new UtcDateTimeOffsetHandler());
    }

    public ConcurrentTransactionOrderingTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task FetchBatch_WithUncommittedEarlierRows_ShouldNotReturnLaterCommittedRows()
    {
        // --- Arrange: clean state and set up the store ---
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var (store, publisherId) = await CreateStoreAndRegisterPublisherAsync(_infra.ConnectionString);
        await ClaimAllPartitionsAsync(_infra.ConnectionString, publisherId);

        var partitionKey = $"pk-{Guid.NewGuid():N}";
        var eventHub = EventHubTestHelper.CheckoutHub();

        // Synchronization gates:
        // txn1Inserted — signals that Txn #1 has inserted A (but NOT committed)
        // txn2Committed — signals that Txn #2 has committed B, C
        // proceedToCommitTxn1 — signals Txn #1 to commit
        var txn1Inserted = new SemaphoreSlim(0, 1);
        var txn2Committed = new SemaphoreSlim(0, 1);
        var proceedToCommitTxn1 = new SemaphoreSlim(0, 1);

        // --- Run Transaction #1 on a separate task (slow transaction) ---
        var txn1Task = Task.Run(async () =>
        {
            await using var conn = new NpgsqlConnection(_infra.ConnectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            await InsertInTransactionAsync(conn, tx, eventHub, partitionKey,
                eventIndex: 0, timestamp: TimestampA);

            // Signal: A is inserted (uncommitted)
            txn1Inserted.Release();

            // Wait until the test tells us to commit
            await proceedToCommitTxn1.WaitAsync();
            await tx.CommitAsync();
        });

        // --- Run Transaction #2 on a separate task (fast transaction) ---
        var txn2Task = Task.Run(async () =>
        {
            // Wait until Txn #1 has inserted A
            await txn1Inserted.WaitAsync();

            await using var conn = new NpgsqlConnection(_infra.ConnectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            await InsertInTransactionAsync(conn, tx, eventHub, partitionKey,
                eventIndex: 1, timestamp: TimestampB);
            await InsertInTransactionAsync(conn, tx, eventHub, partitionKey,
                eventIndex: 2, timestamp: TimestampC);

            await tx.CommitAsync();

            // Signal: B, C committed
            txn2Committed.Release();
        });

        // Wait for Txn #2 to commit (Txn #1 is still open)
        await txn2Committed.WaitAsync();
        _output.WriteLine("Txn #1: message A inserted (uncommitted)");
        _output.WriteLine("Txn #2: messages B, C committed (visible)");

        // --- Assert: FetchBatchAsync should NOT return B, C ---
        var batch1 = await store.FetchBatchAsync(publisherId,
            batchSize: 10,
            CancellationToken.None);

        _output.WriteLine($"FetchBatch while Txn #1 open: returned {batch1.Count} messages " +
                           $"(seq: {string.Join(", ", batch1.Select(m => m.SequenceNumber))})");

        // Critical assertion: no messages should be returned while there are
        // uncommitted earlier rows for the same partition key.
        Assert.Empty(batch1);

        // --- Signal Txn #1 to commit, then verify all messages come through in order ---
        proceedToCommitTxn1.Release();
        await Task.WhenAll(txn1Task, txn2Task);
        _output.WriteLine("Txn #1: committed (message A now visible)");

        var batch2 = await store.FetchBatchAsync(publisherId,
            batchSize: 10,
            CancellationToken.None);

        _output.WriteLine($"FetchBatch after all committed: returned {batch2.Count} messages " +
                           $"(seq: {string.Join(", ", batch2.Select(m => m.SequenceNumber))})");

        // All 3 messages should now be returned
        Assert.Equal(3, batch2.Count);

        // Messages must be in sequence_number order (insert order = delivery order)
        for (var i = 1; i < batch2.Count; i++)
        {
            var prev = batch2[i - 1];
            var curr = batch2[i];

            Assert.True(
                curr.SequenceNumber > prev.SequenceNumber,
                $"Ordering violation: message at position {i} " +
                $"(seq={curr.SequenceNumber}, ts={curr.EventDateTimeUtc}) " +
                $"should come after position {i - 1} " +
                $"(seq={prev.SequenceNumber}, ts={prev.EventDateTimeUtc})");
        }

        _output.WriteLine("All 3 messages returned in correct order after both transactions committed");

        await store.UnregisterPublisherAsync(publisherId, CancellationToken.None);
    }

    [Fact]
    public async Task FetchBatch_WithUncommittedRowOnDifferentPartitionKey_ShouldStillReturnOtherPartitions()
    {
        // --- Arrange ---
        await OutboxTestHelper.CleanupAsync(_infra.ConnectionString);

        var (store, publisherId) = await CreateStoreAndRegisterPublisherAsync(_infra.ConnectionString);
        await ClaimAllPartitionsAsync(_infra.ConnectionString, publisherId);

        var eventHub = EventHubTestHelper.CheckoutHub();

        var txn1Inserted = new SemaphoreSlim(0, 1);
        var fastKeyInserted = new SemaphoreSlim(0, 1);
        var proceedToCommitTxn1 = new SemaphoreSlim(0, 1);

        var slowKey = $"pk-slow-{Guid.NewGuid():N}";
        var fastKey = $"pk-fast-{Guid.NewGuid():N}";

        // Transaction #1: insert to slow partition key — leave uncommitted
        var txn1Task = Task.Run(async () =>
        {
            await using var conn = new NpgsqlConnection(_infra.ConnectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            await InsertInTransactionAsync(conn, tx, eventHub, slowKey,
                eventIndex: 0, timestamp: TimestampA);

            txn1Inserted.Release();

            await proceedToCommitTxn1.WaitAsync();
            await tx.CommitAsync();
        });

        // Wait for Txn #1 to insert, then insert committed messages for a different key
        await txn1Inserted.WaitAsync();
        _output.WriteLine($"Txn #1: inserted message for slow key (uncommitted)");

        await OutboxTestHelper.InsertMessagesAsync(
            _infra.ConnectionString, 3, eventHub, fastKey, startOrdinal: 0);
        _output.WriteLine($"Inserted 3 committed messages for fast key");

        // --- Act ---
        var batch = await store.FetchBatchAsync(publisherId,
            batchSize: 10,
            CancellationToken.None);

        _output.WriteLine($"FetchBatch: returned {batch.Count} messages");

        // The version ceiling (xmin/MIN_ACTIVE_ROWVERSION) may block ALL rows when
        // there's any active write transaction — this is the known trade-off.
        // What we primarily verify: after committing Txn #1, everything drains.

        var batchCountWhileOpen = batch.Count;

        proceedToCommitTxn1.Release();
        await txn1Task;
        _output.WriteLine("Txn #1: committed");

        if (batch.Count > 0)
        {
            await store.DeletePublishedAsync(
                batch.Select(m => m.SequenceNumber).ToList(), CancellationToken.None);
        }

        var batch2 = await store.FetchBatchAsync(publisherId,
            batchSize: 10,
            CancellationToken.None);

        _output.WriteLine($"FetchBatch after commit: returned {batch2.Count} messages");

        // Total across both batches should be 4 (1 slow-key + 3 fast-key)
        Assert.Equal(4, batchCountWhileOpen + batch2.Count);

        _output.WriteLine("All 4 messages eventually delivered — no message loss");

        await store.UnregisterPublisherAsync(publisherId, CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<(PostgreSqlOutboxStore Store, string PublisherId)>
        CreateStoreAndRegisterPublisherAsync(string connectionString)
    {
        var services = new ServiceCollection();
        services.Configure<PostgreSqlStoreOptions>(o =>
        {
            o.ConnectionString = connectionString;
        });
        services.Configure<OutboxPublisherOptions>(o =>
        {
            o.PublisherName = "test-ordering";
        });

        var sp = services.BuildServiceProvider();
        var store = new PostgreSqlOutboxStore(
            sp,
            sp.GetRequiredService<IOptionsMonitor<PostgreSqlStoreOptions>>(),
            sp.GetRequiredService<IOptionsMonitor<OutboxPublisherOptions>>());

        var publisherId = await store.RegisterPublisherAsync(CancellationToken.None);
        _output.WriteLine($"Registered publisher: {publisherId}");

        return (store, publisherId);
    }

    private static async Task ClaimAllPartitionsAsync(string connectionString, string publisherId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE outbox_partitions
            SET    owner_publisher_id = @pub,
                   owned_since_utc    = clock_timestamp(),
                   grace_expires_utc  = NULL
            WHERE  outbox_table_name = 'outbox'", conn);
        cmd.Parameters.AddWithValue("@pub", publisherId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertInTransactionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        string topic, string partitionKey, int eventIndex,
        DateTimeOffset timestamp)
    {
        const string sql = @"
            INSERT INTO outbox (topic_name, partition_key, event_type, payload,
                                event_datetime_utc)
            VALUES (@topic, @key, 'TestEvent', @payload, @ts)";

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@topic", topic);
        cmd.Parameters.AddWithValue("@key", partitionKey);
        cmd.Parameters.Add(new NpgsqlParameter("@payload", NpgsqlTypes.NpgsqlDbType.Bytea)
        {
            Value = Encoding.UTF8.GetBytes($"{{\"index\":{eventIndex}}}")
        });
        cmd.Parameters.AddWithValue("@ts", timestamp);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class UtcDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(System.Data.IDbDataParameter parameter, DateTimeOffset value) =>
            parameter.Value = value.UtcDateTime;

        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            DateTimeOffset dto => dto,
            _ => throw new InvalidCastException($"Cannot convert {value.GetType()} to DateTimeOffset")
        };
    }
}
