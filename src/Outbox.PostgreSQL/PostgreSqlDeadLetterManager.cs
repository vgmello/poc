using System.Data.Common;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;

namespace Outbox.PostgreSQL;

public sealed class PostgreSqlDeadLetterManager : IDeadLetterManager
{
    private readonly PostgreSqlDbHelper _db;
    private readonly PostgreSqlStoreOptions _options;
    private readonly string _schema;

    public PostgreSqlDeadLetterManager(
        Func<IServiceProvider, CancellationToken, Task<DbConnection>> connectionFactory,
        IServiceProvider serviceProvider,
        IOptions<PostgreSqlStoreOptions> options)
    {
        _options = options.Value;
        _schema = _options.SchemaName;
        _db = new PostgreSqlDbHelper(connectionFactory, serviceProvider, _options);
    }

    public async Task<IReadOnlyList<DeadLetteredMessage>> GetAsync(
        int limit, int offset, CancellationToken ct)
    {
        var sql = $@"
SELECT sequence_number, topic_name, partition_key, event_type, headers, payload,
       event_datetime_utc, event_ordinal, retry_count, created_at_utc,
       dead_lettered_at_utc, last_error
FROM   {_schema}.outbox_dead_letter
ORDER  BY dead_letter_seq
LIMIT  @limit OFFSET @offset;";

        var messages = new List<DeadLetteredMessage>();

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            messages.Clear();
            await using var cmd = _db.CreateCommand(sql, conn);
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);

            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                messages.Add(new DeadLetteredMessage(
                    SequenceNumber: reader.GetInt64(0),
                    TopicName: reader.GetString(1),
                    PartitionKey: reader.GetString(2),
                    EventType: reader.GetString(3),
                    Headers: reader.IsDBNull(4) ? null : reader.GetString(4),
                    Payload: reader.GetString(5),
                    EventDateTimeUtc: reader.GetFieldValue<DateTimeOffset>(6),
                    EventOrdinal: reader.GetInt16(7),
                    RetryCount: reader.GetInt32(8),
                    CreatedAtUtc: reader.GetFieldValue<DateTimeOffset>(9),
                    DeadLetteredAtUtc: reader.GetFieldValue<DateTimeOffset>(10),
                    LastError: reader.IsDBNull(11) ? null : reader.GetString(11)));
            }
        }, ct).ConfigureAwait(false);

        return messages;
    }

    public async Task ReplayAsync(IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        var sql = $@"
WITH replayed AS (
    DELETE FROM {_schema}.outbox_dead_letter
    WHERE  sequence_number = ANY(@ids)
    RETURNING sequence_number, topic_name, partition_key, event_type, headers, payload,
              created_at_utc, event_datetime_utc, event_ordinal
)
INSERT INTO {_schema}.outbox
    (topic_name, partition_key, event_type, headers, payload,
     created_at_utc, event_datetime_utc, event_ordinal,
     leased_until_utc, lease_owner, retry_count)
SELECT topic_name, partition_key, event_type, headers, payload,
       created_at_utc, event_datetime_utc, event_ordinal,
       NULL, NULL, 0
FROM replayed;";

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var cmd = _db.CreateCommand(sql, conn);
            cmd.Parameters.Add(new NpgsqlParameter("@ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = sequenceNumbers.ToArray()
            });
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task PurgeAsync(IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        var sql = $@"
DELETE FROM {_schema}.outbox_dead_letter
WHERE  sequence_number = ANY(@ids);";

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var cmd = _db.CreateCommand(sql, conn);
            cmd.Parameters.Add(new NpgsqlParameter("@ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = sequenceNumbers.ToArray()
            });
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task PurgeAllAsync(CancellationToken ct)
    {
        var sql = $"DELETE FROM {_schema}.outbox_dead_letter;";

        await _db.ExecuteWithRetryAsync(async (conn, token) =>
        {
            await using var cmd = _db.CreateCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

}
