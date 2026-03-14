using Npgsql;
using Outbox.Shared;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__OutboxDb")
    ?? throw new InvalidOperationException("ConnectionStrings__OutboxDb not set");

var initSql = await File.ReadAllTextAsync("/init.sql");

Console.WriteLine("[DbInit] Waiting for PostgreSQL...");
await using var conn = await DbHelpers.OpenConnectionAsync(connectionString, CancellationToken.None);

// PostgreSQL doesn't use GO batch separators — execute the entire script as one statement
Console.WriteLine("[DbInit] Running schema...");
await using var cmd = new NpgsqlCommand(initSql, conn);
cmd.CommandTimeout = 30;
await cmd.ExecuteNonQueryAsync();

Console.WriteLine("[DbInit] Done.");
