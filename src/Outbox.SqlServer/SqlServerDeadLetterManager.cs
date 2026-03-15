using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;

namespace Outbox.SqlServer;

/// <summary>
/// SQL Server implementation of <see cref="IDeadLetterManager"/>.
/// </summary>
public sealed class SqlServerDeadLetterManager : IDeadLetterManager
{
    private readonly SqlServerDbHelper _db;
    private readonly SqlServerStoreOptions _options;

    public SqlServerDeadLetterManager(
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        IServiceProvider serviceProvider,
        IOptions<SqlServerStoreOptions> options)
    {
        _options = options.Value;
        _db = new SqlServerDbHelper(connectionFactory, serviceProvider, _options);
    }

    // -------------------------------------------------------------------------
    // Get
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<DeadLetteredMessage>> GetAsync(
        int limit, int offset, CancellationToken ct)
    {
        var schema = _options.SchemaName;

        var sql = $"""
            SELECT
                SequenceNumber,
                TopicName,
                PartitionKey,
                EventType,
                Headers,
                Payload,
                EventDateTimeUtc,
                EventOrdinal,
                RetryCount,
                CreatedAtUtc,
                DeadLetteredAtUtc,
                LastError
            FROM {schema}.OutboxDeadLetter
            ORDER BY SequenceNumber
            OFFSET @Offset ROWS
            FETCH NEXT @Limit ROWS ONLY;
            """;

        var messages = new List<DeadLetteredMessage>();

        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            messages.Clear();
            await using var cmd = (SqlCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@Offset", offset);
            cmd.Parameters.AddWithValue("@Limit", limit);

            await using var reader = await cmd.ExecuteReaderAsync(cancel).ConfigureAwait(false);
            while (await reader.ReadAsync(cancel).ConfigureAwait(false))
            {
                messages.Add(new DeadLetteredMessage(
                    SequenceNumber: reader.GetInt64(0),
                    TopicName: reader.GetString(1),
                    PartitionKey: reader.GetString(2),
                    EventType: reader.GetString(3),
                    Headers: reader.IsDBNull(4) ? null : reader.GetString(4),
                    Payload: reader.GetString(5),
                    EventDateTimeUtc: reader.GetDateTime(6),
                    EventOrdinal: reader.GetInt16(7),
                    RetryCount: reader.GetInt32(8),
                    CreatedAtUtc: reader.GetDateTime(9),
                    DeadLetteredAtUtc: reader.GetDateTime(10),
                    LastError: reader.IsDBNull(11) ? null : reader.GetString(11)));
            }
        }, ct).ConfigureAwait(false);

        return messages;
    }

    // -------------------------------------------------------------------------
    // Replay — move rows back to Outbox for reprocessing
    // -------------------------------------------------------------------------

    public async Task ReplayAsync(IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        if (sequenceNumbers.Count == 0) return;
        var schema = _options.SchemaName;

        // Atomic DELETE...OUTPUT INTO: rows removed from DeadLetter are inserted
        // into Outbox in a single statement, eliminating INSERT/DELETE races.
        var sql = $"""
            DELETE dl
            OUTPUT deleted.TopicName, deleted.PartitionKey, deleted.EventType,
                   deleted.Headers, deleted.Payload, deleted.CreatedAtUtc,
                   deleted.EventDateTimeUtc, deleted.EventOrdinal,
                   0, NULL, NULL
            INTO {schema}.Outbox(TopicName, PartitionKey, EventType,
                 Headers, Payload, CreatedAtUtc,
                 EventDateTimeUtc, EventOrdinal,
                 RetryCount, LeasedUntilUtc, LeaseOwner)
            FROM {schema}.OutboxDeadLetter dl
            INNER JOIN @Ids p ON dl.SequenceNumber = p.SequenceNumber;
            """;

        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var cmd = (SqlCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            SqlServerDbHelper.AddSequenceNumberTvp(cmd, "@Ids", sequenceNumbers, schema);
            await cmd.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Purge
    // -------------------------------------------------------------------------

    public async Task PurgeAsync(IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        if (sequenceNumbers.Count == 0) return;
        var schema = _options.SchemaName;

        var sql = $"""
            DELETE dl
            FROM {schema}.OutboxDeadLetter dl
            INNER JOIN @Ids p ON dl.SequenceNumber = p.SequenceNumber;
            """;

        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var cmd = (SqlCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            SqlServerDbHelper.AddSequenceNumberTvp(cmd, "@Ids", sequenceNumbers, schema);
            await cmd.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task PurgeAllAsync(CancellationToken ct)
    {
        var schema = _options.SchemaName;
        var sql = $"DELETE FROM {schema}.OutboxDeadLetter;";

        await _db.ExecuteWithRetryAsync(async (conn, cancel) =>
        {
            await using var cmd = (SqlCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            await cmd.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

}
