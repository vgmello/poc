// Copyright (c) OrgName. All rights reserved.

using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Text.Json.Serialization;

namespace Outbox.SqlServer;

public sealed class SqlServerStoreOptions : IValidatableObject
{
    [Required]
    [RegularExpression(@"^[a-zA-Z_][a-zA-Z0-9_]*$",
        ErrorMessage = "SchemaName must match pattern [a-zA-Z_][a-zA-Z0-9_]*.")]
    public string SchemaName { get; set; } = "dbo";

    [Required(AllowEmptyStrings = true)]
    [RegularExpression(@"^([a-zA-Z_][a-zA-Z0-9_]*)?$",
        ErrorMessage = "TablePrefix must be empty or match pattern [a-zA-Z_][a-zA-Z0-9_]*.")]
    public string TablePrefix { get; set; } = "";

    [Range(1, int.MaxValue)]
    public int CommandTimeoutSeconds { get; set; } = 30;

    [Range(1, int.MaxValue)]
    public int TransientRetryMaxAttempts { get; set; } = 6;

    [Range(1, int.MaxValue)]
    public int TransientRetryBackoffMs { get; set; } = 1000;

    public string? GroupName { get; set; }

    [RegularExpression(@"^[a-zA-Z_][a-zA-Z0-9_]*$",
        ErrorMessage = "SharedSchemaName must match pattern [a-zA-Z_][a-zA-Z0-9_]*.")]
    public string? SharedSchemaName { get; set; }

    public string? ConnectionString { get; set; }

    [JsonIgnore]
    public Func<IServiceProvider, CancellationToken, Task<DbConnection>>? ConnectionFactory { get; set; }

    public string GetOutboxTableName() => $"{TablePrefix}Outbox";

    public string GetSharedSchemaName() => SharedSchemaName ?? SchemaName;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ConnectionFactory is null && string.IsNullOrEmpty(ConnectionString))
        {
            yield return new ValidationResult(
                "Either ConnectionString or ConnectionFactory must be set.",
                new[] { nameof(ConnectionString), nameof(ConnectionFactory) });
        }
    }
}
