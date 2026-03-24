// Copyright (c) OrgName. All rights reserved.

using System.ComponentModel.DataAnnotations;

namespace Outbox.PostgreSQL;

public sealed class PostgreSqlStoreOptions
{
    [Required]
    [RegularExpression(@"^[a-zA-Z_][a-zA-Z0-9_]*$",
        ErrorMessage = "SchemaName must match pattern [a-zA-Z_][a-zA-Z0-9_]*.")]
    public string SchemaName { get; set; } = "public";

    [Required(AllowEmptyStrings = true)]
    [RegularExpression(@"^[a-zA-Z_][a-zA-Z0-9_]*$",
        ErrorMessage = "TablePrefix must be empty or match pattern [a-zA-Z_][a-zA-Z0-9_]*.")]
    public string TablePrefix { get; set; } = "";

    [Range(1, int.MaxValue)]
    public int CommandTimeoutSeconds { get; set; } = 30;

    [Range(1, int.MaxValue)]
    public int TransientRetryMaxAttempts { get; set; } = 6;

    [Range(1, int.MaxValue)]
    public int TransientRetryBackoffMs { get; set; } = 1000;
}
