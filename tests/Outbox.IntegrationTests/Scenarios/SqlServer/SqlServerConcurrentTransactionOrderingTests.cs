// Copyright (c) OrgName. All rights reserved.

using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Outbox.Core.Options;
using Outbox.IntegrationTests.Fixtures;
using Outbox.IntegrationTests.Helpers;
using Outbox.SqlServer;
using Xunit;
using Xunit.Abstractions;

namespace Outbox.IntegrationTests.Scenarios.SqlServer;

/// <summary>
///     SQL Server variant of <see cref="ConcurrentTransactionOrderingTests" />.
///     Verifies that FetchBatchAsync does NOT return committed rows for a partition key
///     when there are earlier (lower EventDateTimeUtc) uncommitted rows for the same
///     partition key.
///
///     Uses SemaphoreSlim for deterministic coordination between concurrent transactions
///     and explicit timestamps to avoid timing dependencies.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public class SqlServerConcurrentTransactionOrderingTests
{
    private readonly InfrastructureFixture _infra;
    private readonly ITestOutputHelper _output;

    // Fixed timestamps: A is definitively earlier than B, C — no clock dependency
    private static readonly DateTime TimestampA = new(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TimestampB = new(2025, 1, 1, 12, 0, 1, DateTimeKind.Utc);
    private static readonly DateTime TimestampC = new(2025, 1, 1, 12, 0, 2, DateTimeKind.Utc);

    public SqlServerConcurrentTransactionOrderingTests(InfrastructureFixture infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    [Fact]
    public async Task FetchBatch_WithUncommittedEarlierRows_ShouldNotReturnLaterCommittedRows()
    {
        // --- Arrange ---
        await SqlServerTestHelper.CleanupAsync(_infra.SqlServerConnectionString);

        var (store, publisherId) = await CreateStoreAndRegisterPublisherAsync(
            _infra.SqlServerConnectionString);
        await ClaimAllPartitionsAsync(_infra.SqlServerConnectionString, publisherId);

        const string partitionKey = "concurrent-order-test";
        const string topic = "concurrent-ordering-topic";

        var txn1Inserted = new SemaphoreSlim(0, 1);
        var txn2Committed = new SemaphoreSlim(0, 1);
        var proceedToCommitTxn1 = new SemaphoreSlim(0, 1);

        // --- Run Transaction #1 on a separate task (slow transaction) ---
        var txn1Task = Task.Run(async () =>
        {
            await using var conn = new SqlConnection(_infra.SqlServerConnectionString);
            await conn.OpenAsync();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

            await InsertInTransactionAsync(conn, tx, topic, partitionKey,
                eventIndex: 0, ordinal: 0, timestamp: TimestampA);

            txn1Inserted.Release();

            await proceedToCommitTxn1.WaitAsync();
            await tx.CommitAsync();
        });

        // --- Run Transaction #2 on a separate task (fast transaction) ---
        var txn2Task = Task.Run(async () =>
        {
            await txn1Inserted.WaitAsync();

            await using var conn = new SqlConnection(_infra.SqlServerConnectionString);
            await conn.OpenAsync();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

            await InsertInTransactionAsync(conn, tx, topic, partitionKey,
                eventIndex: 1, ordinal: 1, timestamp: TimestampB);
            await InsertInTransactionAsync(conn, tx, topic, partitionKey,
                eventIndex: 2, ordinal: 2, timestamp: TimestampC);

            await tx.CommitAsync();

            txn2Committed.Release();
        });

        await txn2Committed.WaitAsync();
        _output.WriteLine("Txn #1: message A inserted (uncommitted)");
        _output.WriteLine("Txn #2: messages B, C committed (visible)");

        // --- Assert: FetchBatchAsync should NOT return B, C ---
        var batch1 = await store.FetchBatchAsync(publisherId,
            batchSize: 10, maxRetryCount: 5,
            CancellationToken.None);

        _output.WriteLine($"FetchBatch while Txn #1 open: returned {batch1.Count} messages " +
                           $"(seq: {string.Join(", ", batch1.Select(m => m.SequenceNumber))})");

        Assert.Empty(batch1);

        // --- Signal Txn #1 to commit, then verify all messages come through in order ---
        proceedToCommitTxn1.Release();
        await Task.WhenAll(txn1Task, txn2Task);
        _output.WriteLine("Txn #1: committed (message A now visible)");

        var batch2 = await store.FetchBatchAsync(publisherId,
            batchSize: 10, maxRetryCount: 5,
            CancellationToken.None);

        _output.WriteLine($"FetchBatch after all committed: returned {batch2.Count} messages " +
                           $"(seq: {string.Join(", ", batch2.Select(m => m.SequenceNumber))})");

        Assert.Equal(3, batch2.Count);

        for (var i = 1; i < batch2.Count; i++)
        {
            var prev = batch2[i - 1];
            var curr = batch2[i];

            Assert.True(
                curr.EventDateTimeUtc > prev.EventDateTimeUtc ||
                (curr.EventDateTimeUtc == prev.EventDateTimeUtc &&
                 curr.EventOrdinal > prev.EventOrdinal),
                $"Ordering violation: message at position {i} " +
                $"(seq={curr.SequenceNumber}, ts={curr.EventDateTimeUtc}, ord={curr.EventOrdinal}) " +
                $"should come after position {i - 1} " +
                $"(seq={prev.SequenceNumber}, ts={prev.EventDateTimeUtc}, ord={prev.EventOrdinal})");
        }

        _output.WriteLine("All 3 messages returned in correct order after both transactions committed");

        await store.UnregisterPublisherAsync(publisherId, CancellationToken.None);
    }

    [Fact]
    public async Task FetchBatch_WithUncommittedRowOnDifferentPartitionKey_ShouldStillReturnOtherPartitions()
    {
        // --- Arrange ---
        await SqlServerTestHelper.CleanupAsync(_infra.SqlServerConnectionString);

        var (store, publisherId) = await CreateStoreAndRegisterPublisherAsync(
            _infra.SqlServerConnectionString);
        await ClaimAllPartitionsAsync(_infra.SqlServerConnectionString, publisherId);

        const string topic = "concurrent-cross-partition-topic";

        var txn1Inserted = new SemaphoreSlim(0, 1);
        var proceedToCommitTxn1 = new SemaphoreSlim(0, 1);

        var txn1Task = Task.Run(async () =>
        {
            await using var conn = new SqlConnection(_infra.SqlServerConnectionString);
            await conn.OpenAsync();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

            await InsertInTransactionAsync(conn, tx, topic, "slow-key",
                eventIndex: 0, ordinal: 0, timestamp: TimestampA);

            txn1Inserted.Release();

            await proceedToCommitTxn1.WaitAsync();
            await tx.CommitAsync();
        });

        await txn1Inserted.WaitAsync();
        _output.WriteLine("Txn #1: inserted message for 'slow-key' (uncommitted)");

        await SqlServerTestHelper.InsertMessagesAsync(
            _infra.SqlServerConnectionString, 3, topic, "fast-key", startOrdinal: 0);
        _output.WriteLine("Inserted 3 committed messages for 'fast-key'");

        // --- Act ---
        var batch = await store.FetchBatchAsync(publisherId,
            batchSize: 10, maxRetryCount: 5,
            CancellationToken.None);

        _output.WriteLine($"FetchBatch: returned {batch.Count} messages");

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
            batchSize: 10, maxRetryCount: 5,
            CancellationToken.None);

        _output.WriteLine($"FetchBatch after commit: returned {batch2.Count} messages");

        Assert.Equal(4, batchCountWhileOpen + batch2.Count);

        _output.WriteLine("All 4 messages eventually delivered — no message loss");

        await store.UnregisterPublisherAsync(publisherId, CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<(SqlServerOutboxStore Store, string PublisherId)>
        CreateStoreAndRegisterPublisherAsync(string connectionString)
    {
        var services = new ServiceCollection();
        services.Configure<SqlServerStoreOptions>(o =>
        {
            o.ConnectionString = connectionString;
        });
        services.Configure<OutboxPublisherOptions>(o =>
        {
            o.PublisherName = "test-ordering";
        });

        var sp = services.BuildServiceProvider();
        var store = new SqlServerOutboxStore(
            sp,
            sp.GetRequiredService<IOptionsMonitor<SqlServerStoreOptions>>(),
            sp.GetRequiredService<IOptionsMonitor<OutboxPublisherOptions>>());

        var publisherId = await store.RegisterPublisherAsync(CancellationToken.None);
        _output.WriteLine($"Registered publisher: {publisherId}");

        return (store, publisherId);
    }

    private static async Task ClaimAllPartitionsAsync(string connectionString, string publisherId)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            UPDATE dbo.OutboxPartitions
            SET    OwnerPublisherId = @pub,
                   OwnedSinceUtc    = SYSUTCDATETIME(),
                   GraceExpiresUtc  = NULL
            WHERE  OutboxTableName = N'Outbox'", conn);
        cmd.Parameters.AddWithValue("@pub", publisherId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertInTransactionAsync(
        SqlConnection conn, SqlTransaction tx,
        string topic, string partitionKey, int eventIndex, int ordinal,
        DateTime timestamp)
    {
        const string sql = @"
            INSERT INTO dbo.Outbox (TopicName, PartitionKey, EventType, Payload,
                                    EventDateTimeUtc, EventOrdinal)
            VALUES (@topic, @key, 'TestEvent', @payload, @ts, @ordinal)";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@topic", topic);
        cmd.Parameters.AddWithValue("@key", partitionKey);
        cmd.Parameters.Add(new SqlParameter("@payload", System.Data.SqlDbType.VarBinary)
        {
            Value = Encoding.UTF8.GetBytes($"{{\"index\":{eventIndex}}}")
        });
        cmd.Parameters.AddWithValue("@ts", timestamp);
        cmd.Parameters.AddWithValue("@ordinal", (short)ordinal);
        await cmd.ExecuteNonQueryAsync();
    }
}
