namespace Outbox.PostgreSQL;

public sealed class PostgreSqlStoreOptions
{
    public int CommandTimeoutSeconds { get; set; } = 30;
    public string SchemaName { get; set; } = "public";
    public int TransientRetryMaxAttempts { get; set; } = 3;
    public int TransientRetryBackoffMs { get; set; } = 200;
}
