# Parallel Publish Threads Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add configurable parallel publish threads within a publisher instance so N workers concurrently process different partitions from the same leased batch.

**Architecture:** Single coordinator polls `LeaseBatchAsync`, groups messages by `(TopicName, PartitionKey)`, assigns groups to N workers via `partition_id % PublishThreadCount`, and launches workers concurrently via `Task.WhenAll`. Workers share the circuit breaker and health state. Ordering within a partition key is preserved because the same key always routes to the same worker.

**Tech Stack:** .NET 8, xUnit, NSubstitute

**Spec:** `docs/superpowers/specs/2026-03-26-parallel-publish-threads-design.md`

---

### Task 1: Add `PublishThreadCount` option with validation

**Files:**
- Modify: `src/Outbox.Core/Options/OutboxPublisherOptions.cs:50-51` (add property after `CircuitBreakerOpenDurationSeconds`)
- Test: `tests/Outbox.Core.Tests/OutboxPublisherOptionsValidatorTests.cs`

No changes needed to the `Validate()` method body — the `[Range]` attribute handles the >= 1 constraint.

- [ ] **Step 1: Write failing test for `PublishThreadCount` range validation**

In `tests/Outbox.Core.Tests/OutboxPublisherOptionsValidatorTests.cs`, add `PublishThreadCount = 4` to the `ValidOptions()` helper (lines 11-26), and add new tests after the CircuitBreakerOpenDurationSeconds tests (after line 197):

```csharp
// In ValidOptions() helper, add to the initializer:
PublishThreadCount = 4,

// New tests:
[Fact]
public void PublishThreadCount_zero_is_invalid()
{
    var opts = ValidOptions();
    opts.PublishThreadCount = 0;
    var results = Validate(opts);
    Assert.Contains(results, r => r.MemberNames.Contains(nameof(OutboxPublisherOptions.PublishThreadCount)));
}

[Fact]
public void PublishThreadCount_one_is_valid()
{
    var opts = ValidOptions();
    opts.PublishThreadCount = 1;
    var results = Validate(opts);
    Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(OutboxPublisherOptions.PublishThreadCount)));
}

[Fact]
public void PublishThreadCount_four_is_valid()
{
    var opts = ValidOptions();
    opts.PublishThreadCount = 4;
    var results = Validate(opts);
    Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(OutboxPublisherOptions.PublishThreadCount)));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Outbox.Core.Tests/ --filter "PublishThreadCount" -v n`
Expected: compilation error — `PublishThreadCount` doesn't exist yet.

- [ ] **Step 3: Add `PublishThreadCount` property to `OutboxPublisherOptions`**

In `src/Outbox.Core/Options/OutboxPublisherOptions.cs`, after `CircuitBreakerOpenDurationSeconds` (line 51), add:

```csharp
[Range(1, int.MaxValue)]
public int PublishThreadCount { get; set; } = 4;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Outbox.Core.Tests/ --filter "PublishThreadCount" -v n`
Expected: all 3 tests PASS.

- [ ] **Step 5: Run full unit test suite to check no regressions**

Run: `dotnet test tests/Outbox.Core.Tests/ -v n`
Expected: all tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Outbox.Core/Options/OutboxPublisherOptions.cs tests/Outbox.Core.Tests/OutboxPublisherOptionsValidatorTests.cs
git commit -m "feat: add PublishThreadCount option (default 4) with validation"
```

---

### Task 2: Extract worker method from publish loop

Extract the inner group-processing logic (lines 325-581 of `OutboxPublisherService.cs`) into a separate method that processes a list of groups and returns whether it published anything. Also change `unprocessedSequences` from `HashSet<long>` to `ConcurrentDictionary<long, byte>` in preparation for concurrent access.

**Files:**
- Modify: `src/Outbox.Core/Engine/OutboxPublisherService.cs:234-637`
- Add `using System.Collections.Concurrent;` to imports if not already present

- [ ] **Step 1: Run existing unit tests to establish baseline**

Run: `dotnet test tests/Outbox.Core.Tests/OutboxPublisherServiceTests.cs -v n`
Expected: all tests PASS.

- [ ] **Step 2: Extract `ProcessGroupsAsync` worker method**

Add a new private method to `OutboxPublisherService`. This method contains the logic currently in the `foreach (var group in groups)` block (lines 325-581), operating on a subset of groups:

```csharp
/// <summary>
///     Processes a list of message groups: checks circuit breaker, sends via transport,
///     handles success/partial/failure, and removes finalized sequences from the tracking set.
///     Returns true if at least one group was successfully published.
/// </summary>
private async Task<bool> ProcessGroupsAsync(
    string publisherId,
    IReadOnlyList<IGrouping<(string TopicName, string PartitionKey), OutboxMessage>> groups,
    TopicCircuitBreaker circuitBreaker,
    ConcurrentDictionary<long, byte> unprocessedSequences,
    CancellationToken ct)
{
    var publishedAny = false;

    foreach (var group in groups)
    {
        var topicName = group.Key.TopicName;
        var partitionKey = group.Key.PartitionKey;
        var groupMessages = group.OrderBy(m => m.SequenceNumber).ToList();
        var sequenceNumbers = groupMessages.Select(m => m.SequenceNumber).ToList();

        // ... exact same logic as lines 332-580 from the current PublishLoopAsync
    }

    return publishedAny;
}
```

**All 4 sites where `unprocessedSequences` is accessed must be updated:**

1. **Initialization** (line 283): `new HashSet<long>(...)` → `new ConcurrentDictionary<long, byte>(batch.Select(m => new KeyValuePair<long, byte>(m.SequenceNumber, 0)))`
2. **Poison removal** (line 296): `unprocessedSequences.Remove(sn)` → `unprocessedSequences.TryRemove(sn, out _)`
3. **Per-group removals** in the extracted `ProcessGroupsAsync` method (lines 338, 363, 447, 461, 479, 537): all `.Remove(sn)` → `.TryRemove(sn, out _)`
4. **Finally block** (lines 586-602): `unprocessedSequences.Count > 0` → `!unprocessedSequences.IsEmpty`, `unprocessedSequences.ToList()` → `unprocessedSequences.Keys.ToList()`

- [ ] **Step 3: Update `PublishLoopAsync` to call the new method**

Replace the `try { foreach (var group in groups) { ... } }` block (lines 321-603) with:

```csharp
var publishedAny = false;

try
{
    publishedAny = await ProcessGroupsAsync(
        publisherId, groups, circuitBreaker, unprocessedSequences, ct);
}
finally
{
    // Release any leases not yet processed (e.g., on cancellation).
    if (!unprocessedSequences.IsEmpty)
    {
        try
        {
            await _store.ReleaseLeaseAsync(
                publisherId,
                unprocessedSequences.Keys.ToList(),
                incrementRetry: false,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to release {Count} leases during shutdown — they will expire after lease duration",
                unprocessedSequences.Count);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify no regression**

Run: `dotnet test tests/Outbox.Core.Tests/ -v n`
Expected: all tests PASS — behavior is identical, only data structure changed.

- [ ] **Step 5: Commit**

```bash
git add src/Outbox.Core/Engine/OutboxPublisherService.cs
git commit -m "refactor: extract ProcessGroupsAsync worker method from publish loop"
```

---

### Task 3: Add parallel worker dispatch and stub `GetTotalPartitionsAsync` in tests

With the worker method extracted, add the coordinator logic that splits groups across N workers and dispatches them concurrently.

**Files:**
- Modify: `src/Outbox.Core/Engine/OutboxPublisherService.cs`
- Modify: `tests/Outbox.Core.Tests/OutboxPublisherServiceTests.cs` (stub `GetTotalPartitionsAsync`)

- [ ] **Step 1: Stub `GetTotalPartitionsAsync` in test constructor**

In `tests/Outbox.Core.Tests/OutboxPublisherServiceTests.cs`, in the constructor (after line 60, after `_optionsMonitor.Get(...)` setup), add:

```csharp
_store.GetTotalPartitionsAsync(Arg.Any<CancellationToken>()).Returns(64);
```

This is required because the new coordinator calls `GetTotalPartitionsAsync` on every publish cycle. Without this stub, NSubstitute returns `0` by default, causing a `DivideByZeroException` in the worker assignment logic.

- [ ] **Step 2: Run existing tests to verify the stub doesn't break anything**

Run: `dotnet test tests/Outbox.Core.Tests/ -v n`
Expected: all tests PASS.

- [ ] **Step 3: Add `ComputeWorkerIndex` helper method**

Add a private static method to `OutboxPublisherService`:

```csharp
private static int ComputeWorkerIndex(string partitionKey, int totalPartitions, int workerCount)
{
    // Deterministic hash for in-memory worker assignment within a poll cycle.
    // Does NOT need to match the SQL hash — only needs consistency within a single process.
    var hash = (uint)partitionKey.GetHashCode();
    var partitionId = (int)(hash % (uint)totalPartitions);
    return partitionId % workerCount;
}
```

- [ ] **Step 4: Add partition-to-worker assignment and parallel dispatch**

In `PublishLoopAsync`, replace the single `ProcessGroupsAsync` call (from Task 2) with the coordinator logic:

```csharp
// Snapshot total partitions once for this cycle
var totalPartitions = await _store.GetTotalPartitionsAsync(ct);

// Assign groups to workers by partition affinity
var workerCount = opts.PublishThreadCount;
var workerGroups = new List<IGrouping<(string TopicName, string PartitionKey), OutboxMessage>>[workerCount];
for (var i = 0; i < workerCount; i++)
    workerGroups[i] = [];

foreach (var group in groups)
{
    var workerIndex = ComputeWorkerIndex(group.Key.PartitionKey, totalPartitions, workerCount);
    workerGroups[workerIndex].Add(group);
}

var publishedAny = false;

try
{
    // Launch workers concurrently
    var workerTasks = workerGroups
        .Where(wg => wg.Count > 0)
        .Select(wg => ProcessGroupsAsync(
            publisherId, wg, circuitBreaker, unprocessedSequences, ct))
        .ToArray();

    try
    {
        await Task.WhenAll(workerTasks);
    }
    catch
    {
        // Log ALL faulted worker exceptions (Task.WhenAll only throws the first)
        foreach (var task in workerTasks.Where(t => t.IsFaulted))
        {
            foreach (var ex in task.Exception!.InnerExceptions)
            {
                _logger.LogError(ex, "Publish worker faulted unexpectedly");
            }
        }
    }

    // Aggregate publishedAny from all completed workers
    publishedAny = workerTasks
        .Where(t => t.IsCompletedSuccessfully)
        .Any(t => t.Result);
}
finally
{
    // ... same finally block as before (release unprocessed sequences) ...
}
```

- [ ] **Step 5: Run tests to verify**

Run: `dotnet test tests/Outbox.Core.Tests/ -v n`
Expected: all tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Outbox.Core/Engine/OutboxPublisherService.cs tests/Outbox.Core.Tests/OutboxPublisherServiceTests.cs
git commit -m "feat: add parallel worker dispatch for publish loop"
```

---

### Task 4: Add unit tests for parallel publishing behavior

**Files:**
- Test: `tests/Outbox.Core.Tests/OutboxPublisherServiceTests.cs`

Tests follow the existing pattern in the file: `StartAsync` → `Task.Delay` → `StopAsync`. The existing helper `MakeMessage(long seq, string topic, string key, int retryCount)` is used for message creation.

- [ ] **Step 1: Add test — messages with different partition keys are all published**

```csharp
[Fact]
public async Task PublishLoop_with_parallel_threads_publishes_all_partition_key_groups()
{
    _options.PublishThreadCount = 4;

    _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
        .Returns("publisher-1");

    var messages = new[]
    {
        MakeMessage(1, "orders", "pk-a"),
        MakeMessage(2, "orders", "pk-b"),
        MakeMessage(3, "orders", "pk-c"),
        MakeMessage(4, "orders", "pk-d"),
    };

    var callCount = 0;
    _store.LeaseBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
        .Returns(ci =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
                return messages;
            return Array.Empty<OutboxMessage>();
        });

    var service = CreateService();
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

    await service.StartAsync(cts.Token);

    try { await Task.Delay(600, CancellationToken.None); }
    catch { /* Intentionally empty */ }

    await service.StopAsync(CancellationToken.None);

    // All 4 groups (different partition keys) should have been sent
    await _transport.Received(4).SendAsync(
        Arg.Any<string>(), Arg.Any<string>(),
        Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Add test — `PublishThreadCount = 1` processes all groups**

```csharp
[Fact]
public async Task PublishLoop_with_single_thread_publishes_all_groups()
{
    _options.PublishThreadCount = 1;

    _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
        .Returns("publisher-1");

    var messages = new[] { MakeMessage(1, "orders", "pk-a"), MakeMessage(2, "orders", "pk-b") };

    var callCount = 0;
    _store.LeaseBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
        .Returns(ci =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
                return messages;
            return Array.Empty<OutboxMessage>();
        });

    var service = CreateService();
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

    await service.StartAsync(cts.Token);

    try { await Task.Delay(600, CancellationToken.None); }
    catch { /* Intentionally empty */ }

    await service.StopAsync(CancellationToken.None);

    await _transport.Received(2).SendAsync(
        Arg.Any<string>(), Arg.Any<string>(),
        Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());
}
```

- [ ] **Step 3: Add test — one worker's transport failure doesn't prevent other worker from publishing**

This is the key cross-worker isolation test. Two messages on different partition keys go to different workers. One worker's transport fails; the other should still succeed.

```csharp
[Fact]
public async Task PublishLoop_worker_failure_does_not_block_other_workers()
{
    _options.PublishThreadCount = 2;

    _store.RegisterPublisherAsync(Arg.Any<CancellationToken>())
        .Returns("publisher-1");

    var messages = new[] { MakeMessage(1, "orders", "pk-a"), MakeMessage(2, "orders", "pk-b") };

    var callCount = 0;
    _store.LeaseBatchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
        .Returns(ci =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
                return messages;
            return Array.Empty<OutboxMessage>();
        });

    // Make transport throw only for pk-a
    _transport.SendAsync("orders", "pk-a", Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
        .ThrowsAsync(new InvalidOperationException("broker down"));

    var service = CreateService();
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

    await service.StartAsync(cts.Token);

    try { await Task.Delay(600, CancellationToken.None); }
    catch { /* Intentionally empty */ }

    await service.StopAsync(CancellationToken.None);

    // pk-b should still be published successfully
    await _transport.Received().SendAsync(
        "orders", "pk-b",
        Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());

    // pk-a should have its lease released with retry increment
    await _store.Received().ReleaseLeaseAsync(
        Arg.Any<string>(),
        Arg.Is<IReadOnlyList<long>>(ids => ids.Contains(1)),
        true,
        CancellationToken.None);

    // pk-b should be deleted (published successfully)
    await _store.Received().DeletePublishedAsync(
        Arg.Any<string>(),
        Arg.Is<IReadOnlyList<long>>(ids => ids.Contains(2)),
        Arg.Any<CancellationToken>());
}
```

- [ ] **Step 4: Run all tests**

Run: `dotnet test tests/Outbox.Core.Tests/ -v n`
Expected: all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/Outbox.Core.Tests/OutboxPublisherServiceTests.cs
git commit -m "test: add unit tests for parallel publish thread behavior"
```

---

### Task 5: Update documentation

**Files:**
- Modify: `docs/architecture.md` (after line 208)
- Modify: `docs/getting-started.md` (after line 246)
- Modify: `docs/known-limitations.md` (after line 23)

- [ ] **Step 1: Update `docs/architecture.md`**

After the "The grace period" section (after line 207), add a new subsection:

```markdown
### Parallel publish threads

Each publisher runs `PublishThreadCount` (default 4) concurrent workers within its single publish loop. The coordinator polls a batch of messages, groups them by `(TopicName, PartitionKey)`, and assigns each group to a worker using:

```
worker_index = (hash(partition_key) % total_partitions) % PublishThreadCount
```

This guarantees that messages with the same partition key are always processed by the same worker, preserving ordering. Different partition keys can be processed concurrently across workers.

The 4 housekeeping loops (heartbeat, rebalance, orphan sweep, dead-letter sweep) remain single-instance. Only the publish work is parallelized.

Set `PublishThreadCount = 1` to revert to sequential single-thread processing.
```

- [ ] **Step 2: Update `docs/getting-started.md`**

In the "Running multiple publishers" section, after the partition distribution list and before the "Rebalancing happens automatically" line (after line 244), add:

```markdown
Each publisher instance also processes its partitions using 4 parallel threads by default. Tune this with `PublishThreadCount`:

```csharp
services.AddOutbox(options =>
{
    options.PublishThreadCount = 8; // default: 4
});
```

The parallelism benefit scales with partition key diversity — workloads concentrated on a single partition key will see no improvement since ordering requires sequential processing per key.
```

- [ ] **Step 3: Update `docs/known-limitations.md`**

After the "Residual risk" line of the Kafka flush section (after line 23, before the `---` separator on line 25), add:

```markdown
**Interaction with `PublishThreadCount`:** With the default `PublishThreadCount = 4`, up to 4 concurrent `SendAsync` calls can each trigger a long-running flush thread. This multiplies the thread usage but does not affect the ThreadPool since each uses `TaskCreationOptions.LongRunning`. If thread pressure is observed under high load, reduce `PublishThreadCount` for Kafka transports.
```

- [ ] **Step 4: Commit**

```bash
git add docs/architecture.md docs/getting-started.md docs/known-limitations.md
git commit -m "docs: document PublishThreadCount option and parallel publish threads"
```

---

### Task 6: Run full test suite and verify

- [ ] **Step 1: Run all unit tests**

Run: `dotnet test tests/Outbox.Core.Tests/ && dotnet test tests/Outbox.Kafka.Tests/ && dotnet test tests/Outbox.EventHub.Tests/ && dotnet test tests/Outbox.Store.Tests/`
Expected: all PASS.

- [ ] **Step 2: Build entire solution**

Run: `dotnet build src/Outbox.slnx`
Expected: build succeeds with no errors or warnings.

- [ ] **Step 3: Commit any fixes if needed**

Only if previous steps revealed issues that needed fixing.
