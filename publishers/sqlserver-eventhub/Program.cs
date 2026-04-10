using Outbox.Core.Abstractions;
using Outbox.Core.Builder;
using Outbox.EventHub;
using Outbox.SqlServer;

var builder = WebApplication.CreateSlimBuilder(args);

builder.AddOutbox(outbox =>
{
    outbox.UseSqlServer();
    outbox.UseEventHub();
});

var app = builder.Build();
app.MapHealthChecks("/health/internal");

// ---------------------------------------------------------------------------
// Dead letter management — sample endpoints.
// In production, protect these behind authentication/authorization (e.g., an
// internal admin API, operations portal, or CLI tool).
// ---------------------------------------------------------------------------
app.MapGet("/dead-letters", async (IDeadLetterManager dlm, int? limit, int? offset) =>
    Results.Ok(await dlm.GetAsync(limit ?? 50, offset ?? 0, CancellationToken.None)));

app.MapPost("/dead-letters/replay", async (IDeadLetterManager dlm, long[] ids) =>
{
    await dlm.ReplayAsync(ids, CancellationToken.None);
    return Results.Ok();
});

app.MapPost("/dead-letters/purge", async (IDeadLetterManager dlm, long[] ids) =>
{
    await dlm.PurgeAsync(ids, CancellationToken.None);
    return Results.Ok();
});

app.MapDelete("/dead-letters", async (IDeadLetterManager dlm) =>
{
    await dlm.PurgeAllAsync(CancellationToken.None);
    return Results.Ok();
});

await app.RunAsync();
