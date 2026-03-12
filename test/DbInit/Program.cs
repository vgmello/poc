using Microsoft.Data.SqlClient;
using Outbox.Shared;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__OutboxDb")
    ?? throw new InvalidOperationException("ConnectionStrings__OutboxDb not set");

// master connection string for CREATE DATABASE
var masterConnStr = new SqlConnectionStringBuilder(connectionString)
{
    InitialCatalog = "master"
}.ConnectionString;

var initSql = await File.ReadAllTextAsync("/init.sql");

// Split on GO batch separators (sqlcmd directive, not T-SQL)
var batches = System.Text.RegularExpressions.Regex.Split(initSql, @"^\s*GO\s*$",
    System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
    .Where(b => !string.IsNullOrWhiteSpace(b))
    .ToList();

Console.WriteLine("[DbInit] Waiting for SQL Server...");
await using var masterConn = await DbHelpers.OpenConnectionAsync(masterConnStr, CancellationToken.None);

// Run the first batch (CREATE DATABASE) against master
Console.WriteLine("[DbInit] Creating database...");
await using (var cmd = new SqlCommand(batches[0], masterConn))
{
    cmd.CommandTimeout = 30;
    await cmd.ExecuteNonQueryAsync();
}
await masterConn.CloseAsync();

// Run remaining batches against OutboxTest
Console.WriteLine("[DbInit] Running schema...");
await using var conn = await DbHelpers.OpenConnectionAsync(connectionString, CancellationToken.None);
for (int i = 1; i < batches.Count; i++)
{
    await using var cmd = new SqlCommand(batches[i], conn);
    cmd.CommandTimeout = 30;
    await cmd.ExecuteNonQueryAsync();
}

Console.WriteLine("[DbInit] Done.");
