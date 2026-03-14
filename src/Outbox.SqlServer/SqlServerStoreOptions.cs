namespace Outbox.SqlServer;

public sealed class SqlServerStoreOptions
{
    public int CommandTimeoutSeconds { get; set; } = 30;
    public string SchemaName { get; set; } = "dbo";
    public int TransientRetryMaxAttempts { get; set; } = 3;
    public int TransientRetryBackoffMs { get; set; } = 200;
}
