using System.Text.RegularExpressions;

namespace Outbox.SqlServer;

public sealed class SqlServerStoreOptions
{
    private static readonly Regex ValidSchemaPattern = new(
        @"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

    private string _schemaName = "dbo";

    public int CommandTimeoutSeconds { get; set; } = 30;

    public string SchemaName
    {
        get => _schemaName;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !ValidSchemaPattern.IsMatch(value))
                throw new ArgumentException(
                    $"SchemaName '{value}' is invalid. Must match pattern [a-zA-Z_][a-zA-Z0-9_]*.",
                    nameof(value));
            _schemaName = value;
        }
    }

    public int TransientRetryMaxAttempts { get; set; } = 6;
    public int TransientRetryBackoffMs { get; set; } = 1000;
}
