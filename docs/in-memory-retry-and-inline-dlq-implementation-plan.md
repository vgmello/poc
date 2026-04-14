# In-Memory Retry and Inline DLQ Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move outbox retry tracking from a DB column to a per-batch in-memory loop, dead-letter inline when retries exhaust, and cleanly separate transient (broker-health) from non-transient (message-poison) failures.

**Architecture:** `ProcessGroupAsync` in `OutboxPublisherService` is rewritten around a tight retry loop that lives entirely on the worker thread's stack. Transient errors trip the per-topic circuit breaker (no retry burned). Non-transient errors burn an attempt; once exhausted, the failed sub-group is dead-lettered inline via `DeadLetterAsync`. The dead-letter sweep loop, the `IncrementRetryCount` operation, and the `RetryCount` column on the outbox table are deleted entirely. The dead-letter table's `RetryCount` column is renamed to `AttemptCount`.

**Tech Stack:** .NET 10, xUnit, NSubstitute, Confluent.Kafka, Azure.Messaging.EventHubs, Microsoft.Data.SqlClient (SQL Server), Npgsql (PostgreSQL), Testcontainers (integration tests).

**Reference spec:** `docs/in-memory-retry-and-inline-dlq-design.md`. Read it first if anything in this plan is unclear.

---

## File Structure

**Created:**
- `tests/Outbox.Core.Tests/Behavioral/RetryLoopTests.cs` — unit tests for the new in-batch retry loop (replaces `RetryCountAccuracyTests.cs`).
- `tests/Outbox.Kafka.Tests/KafkaOutboxTransportIsTransientTests.cs` — pinning tests for Kafka's transient classification.
- `tests/Outbox.EventHub.Tests/EventHubOutboxTransportIsTransientTests.cs` — pinning tests for EventHub's transient classification.

**Modified:**
- `src/Outbox.Core/Models/OutboxMessage.cs` — drop `RetryCount` field.
- `src/Outbox.Core/Models/PublishFailureReason.cs` — new file (created in Task 1).
- `src/Outbox.Core/Abstractions/IOutboxTransport.cs` — add `IsTransient(Exception ex)`.
- `src/Outbox.Core/Abstractions/IOutboxStore.cs` — remove `IncrementRetryCountAsync`/`SweepDeadLettersAsync`, modify `FetchBatchAsync`/`DeadLetterAsync` signatures.
- `src/Outbox.Core/Abstractions/IOutboxEventHandler.cs` — add `PublishFailureReason` parameter to `OnPublishFailedAsync`.
- `src/Outbox.Core/Options/OutboxPublisherOptions.cs` — rename `MaxRetryCount` → `MaxPublishAttempts`, remove `DeadLetterSweepIntervalMs`, remove the `MaxRetry > CircuitThreshold` validation, add `RetryBackoffBaseMs` + `RetryBackoffMaxMs`.
- `src/Outbox.Core/Engine/OutboxPublisherService.cs` — rewrite `ProcessGroupsAsync`, delete `DeadLetterSweepLoopAsync`, update `RunLoopsWithRestartAsync` to manage 4 loops instead of 5.
- `src/Outbox.Kafka/KafkaOutboxTransport.cs` — implement `IsTransient`.
- `src/Outbox.EventHub/EventHubOutboxTransport.cs` — implement `IsTransient`.
- `src/Outbox.SqlServer/db_scripts/install.sql` — drop `Outbox.RetryCount`, rename `OutboxDeadLetter.RetryCount` to `AttemptCount`, fix index INCLUDE columns, fix views.
- `src/Outbox.SqlServer/SqlServerQueries.cs` — update `FetchBatch`, update `DeadLetter`, delete `IncrementRetryCount` and `SweepDeadLetters`.
- `src/Outbox.SqlServer/SqlServerOutboxStore.cs` — drop `IncrementRetryCountAsync`/`SweepDeadLettersAsync`, update `FetchBatchAsync`/`DeadLetterAsync`.
- `src/Outbox.PostgreSQL/db_scripts/install.sql` — drop `outbox.retry_count`, rename `outbox_dead_letter.retry_count` to `attempt_count`, fix index INCLUDE columns, fix views.
- `src/Outbox.PostgreSQL/PostgreSqlQueries.cs` — same query updates as SQL Server.
- `src/Outbox.PostgreSQL/PostgreSqlOutboxStore.cs` — same store updates as SQL Server.
- `tests/Outbox.Core.Tests/TestOutboxServiceFactory.cs` — drop `MaxRetryCount`/`DeadLetterSweepIntervalMs`, add `MaxPublishAttempts`/backoff options, update `MakeMessage` (no `retryCount` parameter), update `SetupSingleBatch` to match new `FetchBatchAsync` signature.
- `tests/Outbox.Core.Tests/Behavioral/MessageDeliveryContractTests.cs`, `EventHandlerIsolationTests.cs`, `GracefulShutdownContractTests.cs`, `OutboxPublisherServiceTests.cs`, `OutboxMessageInterceptorOrchestrationTests.cs`, `OutboxMessageContextTests.cs`, `ModelTests.cs`, `OutboxBuilderTests.cs`, `OutboxHealthCheckTests.cs`, `OutboxPublisherOptionsValidatorTests.cs` — update for new API.
- `tests/Outbox.IntegrationTests/Helpers/OutboxTestHelper.cs`, `Helpers/SqlServerTestHelper.cs` — drop retry_count column references.
- `tests/Outbox.IntegrationTests/Scenarios/PoisonMessageTests.cs` (and SqlServer variant) — assert inline DLQing instead of sweep-loop DLQing.
- `tests/Outbox.IntegrationTests/Scenarios/CircuitBreakerRetryTests.cs` — assert no DLQ during outage.
- `tests/Outbox.IntegrationTests/Scenarios/IntermittentFailureTests.cs` (and SqlServer variant) — adjust expectations for in-memory retry.
- `tests/Outbox.IntegrationTests/Scenarios/DeadLetterReplayTests.cs` (and SqlServer variant) — read from `AttemptCount` not `RetryCount`.
- `tests/Outbox.IntegrationTests/Scenarios/ProcessKillTests.cs` — update for fresh-attempt-on-restart behavior.
- `tests/Outbox.IntegrationTests/Scenarios/ConcurrentTransactionOrderingTests.cs` (and SqlServer variant) — drop any retry_count references in fixture inserts.
- `tests/Outbox.PerformanceTests/Helpers/PerfTestOptions.cs` — rename `MaxRetryCount` to `MaxPublishAttempts`, drop `DeadLetterSweepIntervalMs`.
- `docs/outbox-requirements-invariants.md`, `docs/known-limitations.md`, `docs/failure-scenarios-and-integration-tests.md`, `CLAUDE.md`, `src/Outbox.Core/README.md`, `src/Outbox.SqlServer/README.md`, `src/Outbox.PostgreSQL/README.md` — text updates.

**Deleted:**
- `tests/Outbox.Core.Tests/Behavioral/RetryCountAccuracyTests.cs` — replaced by `RetryLoopTests.cs`.

---

## Build and test commands

Run the full unit-test sweep after each phase boundary:

```bash
dotnet test tests/Outbox.Core.Tests/ \
  && dotnet test tests/Outbox.Kafka.Tests/ \
  && dotnet test tests/Outbox.EventHub.Tests/ \
  && dotnet test tests/Outbox.Store.Tests/
```

Integration tests (after integration test updates):

```bash
dotnet test tests/Outbox.IntegrationTests/      # ~3 minutes, requires Docker
```

A single test by name:

```bash
dotnet test tests/Outbox.Core.Tests/ --filter "FullyQualifiedName~RetryLoopTests.NonTransientFailures_DeadLetters_AfterMaxAttempts"
```

---

## Task 1: Add `PublishFailureReason` enum

This is a pure addition; the codebase still compiles and runs after this commit.

**Files:**
- Create: `src/Outbox.Core/Models/PublishFailureReason.cs`

- [ ] **Step 1: Create the enum file**

```csharp
// src/Outbox.Core/Models/PublishFailureReason.cs
// Copyright (c) OrgName. All rights reserved.

namespace Outbox.Core.Models;

/// <summary>
///     Why a publish attempt for a (topic, partitionKey) group did not result in
///     full delivery. Passed to <see cref="Abstractions.IOutboxEventHandler.OnPublishFailedAsync" />
///     when the in-batch retry loop exits without successfully delivering every message.
/// </summary>
public enum PublishFailureReason
{
    /// <summary>
    ///     The retry loop exhausted <c>MaxPublishAttempts</c> without success. The
    ///     remaining messages have been moved to the dead-letter table inline. This
    ///     handler call is followed by one <c>OnMessageDeadLetteredAsync</c> per
    ///     dead-lettered message.
    /// </summary>
    RetriesExhausted,

    /// <summary>
    ///     A transient transport failure tripped the topic's circuit breaker before
    ///     retries could exhaust. The remaining messages stay in the outbox and will
    ///     be retried after the circuit closes.
    /// </summary>
    CircuitOpened,

    /// <summary>
    ///     The publisher was shut down while the retry loop was running. The remaining
    ///     messages stay in the outbox.
    /// </summary>
    Cancelled
}
```

- [ ] **Step 2: Build the Core project to confirm it compiles**

Run: `dotnet build src/Outbox.Core/Outbox.Core.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Outbox.Core/Models/PublishFailureReason.cs
git commit -m "feat(outbox): add PublishFailureReason enum"
```

---

## Task 2: Update `OutboxPublisherOptions` (new options, validation cleanup)

This is a breaking API change for anything that reads `MaxRetryCount` or `DeadLetterSweepIntervalMs`. Compilation will break in `OutboxPublisherService.cs`, the test factory, and one perf-test helper. Those are fixed in later tasks; this task just makes the option surface correct.

**Files:**
- Modify: `src/Outbox.Core/Options/OutboxPublisherOptions.cs`

- [ ] **Step 1: Replace the entire option file**

```csharp
// src/Outbox.Core/Options/OutboxPublisherOptions.cs
// Copyright (c) OrgName. All rights reserved.

using System.ComponentModel.DataAnnotations;

namespace Outbox.Core.Options;

public sealed class OutboxPublisherOptions : IValidatableObject
{
    [Required]
    public string PublisherName { get; set; } = "outbox-publisher";

    public string? GroupName { get; set; }

    [Range(1, int.MaxValue)]
    public int BatchSize { get; set; } = 100;

    /// <summary>
    ///     Maximum number of times a (topic, partitionKey) group will be sent before
    ///     the failed messages are dead-lettered. Counts total attempts including the
    ///     first send. Only non-transient failures consume an attempt; transient
    ///     failures (broker unreachable, timeouts, etc.) do not.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxPublishAttempts { get; set; } = 5;

    /// <summary>
    ///     Initial backoff (ms) between in-batch retry attempts.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int RetryBackoffBaseMs { get; set; } = 100;

    /// <summary>
    ///     Maximum backoff (ms) between in-batch retry attempts. The actual delay is
    ///     <c>min(RetryBackoffBaseMs * 2^(attempt-1), RetryBackoffMaxMs)</c>.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int RetryBackoffMaxMs { get; set; } = 2000;

    [Range(1, int.MaxValue)]
    public int MinPollIntervalMs { get; set; } = 100;

    [Range(1, int.MaxValue)]
    public int MaxPollIntervalMs { get; set; } = 5000;

    [Range(1, int.MaxValue)]
    public int HeartbeatIntervalMs { get; set; } = 10_000;

    [Range(1, int.MaxValue)]
    public int HeartbeatTimeoutSeconds { get; set; } = 30;

    [Range(1, int.MaxValue)]
    public int PartitionGracePeriodSeconds { get; set; } = 60;

    [Range(1, int.MaxValue)]
    public int RebalanceIntervalMs { get; set; } = 30_000;

    [Range(1, int.MaxValue)]
    public int OrphanSweepIntervalMs { get; set; } = 60_000;

    [Range(1, int.MaxValue)]
    public int CircuitBreakerFailureThreshold { get; set; } = 3;

    [Range(1, int.MaxValue)]
    public int CircuitBreakerOpenDurationSeconds { get; set; } = 30;

    [Range(1, int.MaxValue)]
    public int PublishThreadCount { get; set; } = 4;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxPollIntervalMs < MinPollIntervalMs)
        {
            yield return new ValidationResult(
                "MaxPollIntervalMs must be >= MinPollIntervalMs.",
                new[] { nameof(MaxPollIntervalMs), nameof(MinPollIntervalMs) });
        }

        var heartbeatTimeoutMs = HeartbeatTimeoutSeconds * 1000;

        if (HeartbeatIntervalMs > 0 && heartbeatTimeoutMs > 0 &&
            heartbeatTimeoutMs < HeartbeatIntervalMs * 3)
        {
            yield return new ValidationResult(
                $"HeartbeatTimeoutSeconds ({HeartbeatTimeoutSeconds}s) should be >= " +
                $"3x HeartbeatIntervalMs ({HeartbeatIntervalMs}ms) to tolerate at least 2 missed heartbeats. " +
                "Current timeout fires after only " +
                $"{(double)heartbeatTimeoutMs / HeartbeatIntervalMs:F1}x intervals, " +
                "which may cause false staleness detection and unnecessary rebalancing.",
                new[] { nameof(HeartbeatTimeoutSeconds), nameof(HeartbeatIntervalMs) });
        }

        if (RetryBackoffMaxMs < RetryBackoffBaseMs)
        {
            yield return new ValidationResult(
                "RetryBackoffMaxMs must be >= RetryBackoffBaseMs.",
                new[] { nameof(RetryBackoffMaxMs), nameof(RetryBackoffBaseMs) });
        }
    }
}
```

The previous `MaxRetryCount > CircuitBreakerFailureThreshold` validation rule is intentionally removed — under the spec's clean separation of transient/non-transient signals, the two settings never interact.

- [ ] **Step 2: Build the Core project (will fail at OutboxPublisherService callsites)**

Run: `dotnet build src/Outbox.Core/Outbox.Core.csproj`
Expected: build fails with errors like `'OutboxPublisherOptions' does not contain a definition for 'MaxRetryCount'` or `'DeadLetterSweepIntervalMs'`. **This is expected — the publisher service is updated in a later task.**

- [ ] **Step 3: Commit (the broken state is intentional and short-lived)**

```bash
git add src/Outbox.Core/Options/OutboxPublisherOptions.cs
git commit -m "feat(outbox): rename MaxRetryCount to MaxPublishAttempts and add backoff options"
```

---

## Task 3: Update `OutboxPublisherOptionsValidatorTests`

The validation rule we removed is asserted in this test file. Update it to assert the new rule (`RetryBackoffMaxMs >= RetryBackoffBaseMs`) and remove the assertion for the old rule.

**Files:**
- Modify: `tests/Outbox.Core.Tests/OutboxPublisherOptionsValidatorTests.cs`

- [ ] **Step 1: Read the current file**

Run: `cat tests/Outbox.Core.Tests/OutboxPublisherOptionsValidatorTests.cs` (or use the Read tool).

- [ ] **Step 2: Remove the assertion(s) that check `MaxRetryCount > CircuitBreakerFailureThreshold`**

Search for `MaxRetryCount` and `CircuitBreakerFailureThreshold` in the file. Delete the test method(s) that exercise this rule — typically one `[Fact]` named something like `Validation_FailsWhen_MaxRetryCountLessThanOrEqualToCircuitThreshold`.

- [ ] **Step 3: Add an assertion for the new backoff validation rule**

Add to the same file:

```csharp
[Fact]
public void Validation_FailsWhen_RetryBackoffMaxLessThanBase()
{
    var opts = new OutboxPublisherOptions
    {
        RetryBackoffBaseMs = 1000,
        RetryBackoffMaxMs = 500
    };

    var ctx = new ValidationContext(opts);
    var results = opts.Validate(ctx).ToList();

    Assert.Contains(results,
        r => r.ErrorMessage!.Contains("RetryBackoffMaxMs must be >= RetryBackoffBaseMs"));
}

[Fact]
public void Validation_Passes_WithDefaultBackoff()
{
    var opts = new OutboxPublisherOptions();   // defaults: 100, 2000
    var ctx = new ValidationContext(opts);
    var results = opts.Validate(ctx).ToList();

    Assert.DoesNotContain(results,
        r => r.ErrorMessage!.Contains("RetryBackoff"));
}
```

- [ ] **Step 4: Replace any references to `MaxRetryCount` in this file with `MaxPublishAttempts`**

Search for `MaxRetryCount` in the file. Each occurrence in a property initializer should become `MaxPublishAttempts`. (Tests that validated the old rule should already be deleted from Step 2.)

- [ ] **Step 5: Build the test project**

Run: `dotnet build tests/Outbox.Core.Tests/Outbox.Core.Tests.csproj`
Expected: build still fails because of other unfixed callsites in this project (test factory), but errors about this specific file should be gone.

- [ ] **Step 6: Commit**

```bash
git add tests/Outbox.Core.Tests/OutboxPublisherOptionsValidatorTests.cs
git commit -m "test(outbox): update options validator tests for new backoff rule"
```

---

## Task 4: Update `IOutboxTransport` interface (add `IsTransient`)

This adds a new abstract method to the contract, which will break compilation in `KafkaOutboxTransport` and `EventHubOutboxTransport`. Those are fixed in Tasks 5 and 6.

**Files:**
- Modify: `src/Outbox.Core/Abstractions/IOutboxTransport.cs`

- [ ] **Step 1: Replace the interface file**

```csharp
// src/Outbox.Core/Abstractions/IOutboxTransport.cs
// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;

namespace Outbox.Core.Abstractions;

public interface IOutboxTransport : IAsyncDisposable
{
    Task SendAsync(
        string topicName,
        string partitionKey,
        IReadOnlyList<OutboxMessage> messages,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Classifies a transport-layer exception as transient (broker is sick — should
    ///     NOT burn an attempt; SHOULD trip the circuit breaker) or non-transient
    ///     (message is bad — SHOULD burn an attempt; should NOT trip the circuit
    ///     breaker). Implementations must inspect their broker SDK's exception types.
    ///     Default for unknown exception types is <c>false</c> (non-transient) — this
    ///     biases toward dead-lettering over infinite retry, which is the safer default
    ///     for an outbox.
    /// </summary>
    bool IsTransient(Exception ex);
}
```

- [ ] **Step 2: Build (will fail in transports)**

Run: `dotnet build src/Outbox.Core/Outbox.Core.csproj`
Expected: Outbox.Core builds. The Kafka and EventHub projects will fail to build until Tasks 5 and 6 — but we don't build them here.

- [ ] **Step 3: Commit**

```bash
git add src/Outbox.Core/Abstractions/IOutboxTransport.cs
git commit -m "feat(outbox): add IsTransient classifier to IOutboxTransport"
```

---

## Task 5: Implement `KafkaOutboxTransport.IsTransient` (TDD)

**Files:**
- Create: `tests/Outbox.Kafka.Tests/KafkaOutboxTransportIsTransientTests.cs`
- Modify: `src/Outbox.Kafka/KafkaOutboxTransport.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Outbox.Kafka.Tests/KafkaOutboxTransportIsTransientTests.cs
// Copyright (c) OrgName. All rights reserved.

using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;
using Xunit;

namespace Outbox.Kafka.Tests;

public sealed class KafkaOutboxTransportIsTransientTests
{
    private readonly KafkaOutboxTransport _transport;

    public KafkaOutboxTransportIsTransientTests()
    {
        var producer = Substitute.For<IProducer<string, byte[]>>();
        var options = Options.Create(new KafkaTransportOptions
        {
            SendTimeoutSeconds = 30,
            MaxBatchSizeBytes = 1_000_000
        });
        _transport = new KafkaOutboxTransport(
            producer,
            options,
            NullLogger<KafkaOutboxTransport>.Instance,
            Array.Empty<ITransportMessageInterceptor<Message<string, byte[]>>>());
    }

    [Theory]
    [InlineData(ErrorCode.BrokerNotAvailable)]
    [InlineData(ErrorCode.NetworkException)]
    [InlineData(ErrorCode.RequestTimedOut)]
    [InlineData(ErrorCode.LeaderNotAvailable)]
    [InlineData(ErrorCode.NotLeaderForPartition)]
    [InlineData(ErrorCode.NotEnoughReplicas)]
    [InlineData(ErrorCode.NotEnoughReplicasAfterAppend)]
    [InlineData(ErrorCode.OutOfOrderSequenceNumber)]
    [InlineData(ErrorCode.Local_Transport)]
    [InlineData(ErrorCode.Local_AllBrokersDown)]
    [InlineData(ErrorCode.Local_TimedOut)]
    [InlineData(ErrorCode.PolicyViolation)]
    public void IsTransient_True_ForKnownTransientCodes(ErrorCode code)
    {
        var ex = new ProduceException<string, byte[]>(
            new Error(code, "test"),
            new DeliveryResult<string, byte[]>());
        Assert.True(_transport.IsTransient(ex));
    }

    [Theory]
    [InlineData(ErrorCode.MessageSizeTooLarge)]
    [InlineData(ErrorCode.InvalidMsg)]
    [InlineData(ErrorCode.UnknownTopicOrPart)]
    [InlineData(ErrorCode.TopicAuthorizationFailed)]
    [InlineData(ErrorCode.ClusterAuthorizationFailed)]
    public void IsTransient_False_ForKnownNonTransientCodes(ErrorCode code)
    {
        var ex = new ProduceException<string, byte[]>(
            new Error(code, "test"),
            new DeliveryResult<string, byte[]>());
        Assert.False(_transport.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_True_ForAggregateException_AllInnerTransient()
    {
        var inner1 = new ProduceException<string, byte[]>(
            new Error(ErrorCode.NetworkException, "n"),
            new DeliveryResult<string, byte[]>());
        var inner2 = new ProduceException<string, byte[]>(
            new Error(ErrorCode.RequestTimedOut, "t"),
            new DeliveryResult<string, byte[]>());

        Assert.True(_transport.IsTransient(new AggregateException(inner1, inner2)));
    }

    [Fact]
    public void IsTransient_False_ForAggregateException_AnyInnerNonTransient()
    {
        var transient = new ProduceException<string, byte[]>(
            new Error(ErrorCode.NetworkException, "n"),
            new DeliveryResult<string, byte[]>());
        var poison = new ProduceException<string, byte[]>(
            new Error(ErrorCode.MessageSizeTooLarge, "p"),
            new DeliveryResult<string, byte[]>());

        Assert.False(_transport.IsTransient(new AggregateException(transient, poison)));
    }

    [Fact]
    public void IsTransient_False_ForUnknownExceptionType()
    {
        Assert.False(_transport.IsTransient(new InvalidOperationException("???")));
    }

    [Fact]
    public void IsTransient_True_ForKafkaException_TransientCode()
    {
        var ex = new KafkaException(new Error(ErrorCode.NetworkException, "n"));
        Assert.True(_transport.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_True_ForOperationCanceledException()
    {
        // Cancellation during a send is treated as transient — the message will be retried
        // on the next poll, not dead-lettered.
        Assert.True(_transport.IsTransient(new OperationCanceledException()));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail (compilation error first)**

Run: `dotnet test tests/Outbox.Kafka.Tests/Outbox.Kafka.Tests.csproj --filter "FullyQualifiedName~KafkaOutboxTransportIsTransientTests"`
Expected: build error — `KafkaOutboxTransport` does not implement `IOutboxTransport.IsTransient`.

- [ ] **Step 3: Implement `IsTransient` on `KafkaOutboxTransport`**

Add this method to `src/Outbox.Kafka/KafkaOutboxTransport.cs`, right after the `SendAsync` method (before `SendSubBatchAsync`):

```csharp
/// <inheritdoc />
public bool IsTransient(Exception ex)
{
    return ex switch
    {
        OperationCanceledException => true,
        AggregateException agg => agg.InnerExceptions.Count > 0
            && agg.InnerExceptions.All(IsTransient),
        ProduceException<string, byte[]> pex => IsTransientCode(pex.Error.Code),
        KafkaException kex => IsTransientCode(kex.Error.Code),
        _ => false
    };
}

private static bool IsTransientCode(ErrorCode code) => code switch
{
    // Broker-side transient errors
    ErrorCode.BrokerNotAvailable => true,
    ErrorCode.LeaderNotAvailable => true,
    ErrorCode.NotLeaderForPartition => true,
    ErrorCode.NotEnoughReplicas => true,
    ErrorCode.NotEnoughReplicasAfterAppend => true,
    ErrorCode.NetworkException => true,
    ErrorCode.RequestTimedOut => true,
    ErrorCode.OutOfOrderSequenceNumber => true,
    ErrorCode.PolicyViolation => true,
    // Client-side transient errors (librdkafka local errors)
    ErrorCode.Local_Transport => true,
    ErrorCode.Local_AllBrokersDown => true,
    ErrorCode.Local_TimedOut => true,
    ErrorCode.Local_QueueFull => true,
    ErrorCode.Local_MsgTimedOut => true,
    _ => false
};
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Outbox.Kafka.Tests/Outbox.Kafka.Tests.csproj --filter "FullyQualifiedName~KafkaOutboxTransportIsTransientTests"`
Expected: all `KafkaOutboxTransportIsTransientTests` tests PASS. (Other Kafka tests in the project may still fail until later tasks fix unrelated breakage; that's fine for this task.)

- [ ] **Step 5: Commit**

```bash
git add src/Outbox.Kafka/KafkaOutboxTransport.cs tests/Outbox.Kafka.Tests/KafkaOutboxTransportIsTransientTests.cs
git commit -m "feat(kafka): implement IsTransient classifier"
```

---

## Task 6: Implement `EventHubOutboxTransport.IsTransient` (TDD)

**Files:**
- Create: `tests/Outbox.EventHub.Tests/EventHubOutboxTransportIsTransientTests.cs`
- Modify: `src/Outbox.EventHub/EventHubOutboxTransport.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Outbox.EventHub.Tests/EventHubOutboxTransportIsTransientTests.cs
// Copyright (c) OrgName. All rights reserved.

using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;
using Xunit;

namespace Outbox.EventHub.Tests;

public sealed class EventHubOutboxTransportIsTransientTests
{
    private readonly EventHubOutboxTransport _transport;

    public EventHubOutboxTransportIsTransientTests()
    {
        var options = Options.Create(new EventHubTransportOptions
        {
            SendTimeoutSeconds = 30,
            MaxBatchSizeBytes = 1_000_000
        });
        var clientFactory = new EventHubClientFactory(_ => Substitute.For<EventHubProducerClient>());
        _transport = new EventHubOutboxTransport(
            options,
            NullLogger<EventHubOutboxTransport>.Instance,
            Array.Empty<ITransportMessageInterceptor<EventData>>(),
            clientFactory);
    }

    [Fact]
    public void IsTransient_True_WhenEventHubsExceptionIsTransient()
    {
        var ex = new EventHubsException(
            isTransient: true,
            eventHubsResource: "test-hub",
            message: "broker timeout");
        Assert.True(_transport.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_False_WhenEventHubsExceptionIsNotTransient()
    {
        var ex = new EventHubsException(
            isTransient: false,
            eventHubsResource: "test-hub",
            message: "auth failed");
        Assert.False(_transport.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_True_ForOperationCanceledException()
    {
        Assert.True(_transport.IsTransient(new OperationCanceledException()));
    }

    [Fact]
    public void IsTransient_True_ForTimeoutException()
    {
        Assert.True(_transport.IsTransient(new TimeoutException("send timed out")));
    }

    [Fact]
    public void IsTransient_False_ForInvalidOperationException()
    {
        // E.g., "message too large for batch" — message is bad, not transport.
        Assert.False(_transport.IsTransient(new InvalidOperationException("too large")));
    }

    [Fact]
    public void IsTransient_True_ForAggregateException_AllInnerTransient()
    {
        var inner1 = new EventHubsException(true, "h", "t1");
        var inner2 = new TimeoutException("t2");

        Assert.True(_transport.IsTransient(new AggregateException(inner1, inner2)));
    }

    [Fact]
    public void IsTransient_False_ForAggregateException_AnyInnerNonTransient()
    {
        var transient = new EventHubsException(true, "h", "t");
        var poison = new InvalidOperationException("bad");

        Assert.False(_transport.IsTransient(new AggregateException(transient, poison)));
    }

    [Fact]
    public void IsTransient_False_ForUnknownExceptionType()
    {
        Assert.False(_transport.IsTransient(new ApplicationException("???")));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Outbox.EventHub.Tests/Outbox.EventHub.Tests.csproj --filter "FullyQualifiedName~EventHubOutboxTransportIsTransientTests"`
Expected: build error — `EventHubOutboxTransport` does not implement `IOutboxTransport.IsTransient`.

- [ ] **Step 3: Implement `IsTransient` on `EventHubOutboxTransport`**

Add this method to `src/Outbox.EventHub/EventHubOutboxTransport.cs`, right after the `SendAsync` method (before `DisposeAsync`):

```csharp
/// <inheritdoc />
public bool IsTransient(Exception ex)
{
    return ex switch
    {
        OperationCanceledException => true,
        TimeoutException => true,
        EventHubsException eh => eh.IsTransient,
        AggregateException agg => agg.InnerExceptions.Count > 0
            && agg.InnerExceptions.All(IsTransient),
        _ => false
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Outbox.EventHub.Tests/Outbox.EventHub.Tests.csproj --filter "FullyQualifiedName~EventHubOutboxTransportIsTransientTests"`
Expected: all `EventHubOutboxTransportIsTransientTests` tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Outbox.EventHub/EventHubOutboxTransport.cs tests/Outbox.EventHub.Tests/EventHubOutboxTransportIsTransientTests.cs
git commit -m "feat(eventhub): implement IsTransient classifier"
```

---

## Task 7: Update `IOutboxEventHandler` interface (new `OnPublishFailedAsync` signature)

This is a breaking change for any consumer that overrode `OnPublishFailedAsync`. We update the interface and the no-op default implementation in this task; consumers in tests and integrations are updated in their respective tasks.

**Files:**
- Modify: `src/Outbox.Core/Abstractions/IOutboxEventHandler.cs`

- [ ] **Step 1: Replace the interface file**

```csharp
// src/Outbox.Core/Abstractions/IOutboxEventHandler.cs
// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;

namespace Outbox.Core.Abstractions;

public interface IOutboxEventHandler
{
    Task OnMessagePublishedAsync(OutboxMessage message, CancellationToken ct) =>
        Task.CompletedTask;

    Task OnMessageDeadLetteredAsync(OutboxMessage message, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>
    ///     Fires once per <c>(topic, partitionKey)</c> group when the in-batch retry
    ///     loop exits without fully delivering every message. The <paramref name="reason" />
    ///     identifies which exit path was taken. Per-attempt failures inside the retry
    ///     loop are observable via metrics and logs; this handler is outcome-level only.
    /// </summary>
    Task OnPublishFailedAsync(
        IReadOnlyList<OutboxMessage> messages,
        Exception lastError,
        PublishFailureReason reason,
        CancellationToken ct) =>
        Task.CompletedTask;

    Task OnCircuitBreakerStateChangedAsync(
        string topicName, CircuitState state, CancellationToken ct) =>
        Task.CompletedTask;

    Task OnRebalanceAsync(
        string publisherId, IReadOnlyList<int> ownedPartitions, CancellationToken ct) =>
        Task.CompletedTask;
}
```

- [ ] **Step 2: Build the Core project**

Run: `dotnet build src/Outbox.Core/Outbox.Core.csproj`
Expected: build still fails because `OutboxPublisherService` calls the old 3-arg signature. **That callsite is fixed in Task 12.**

- [ ] **Step 3: Commit**

```bash
git add src/Outbox.Core/Abstractions/IOutboxEventHandler.cs
git commit -m "feat(outbox): add PublishFailureReason to OnPublishFailedAsync"
```

---

## Task 8: Update `IOutboxStore` interface (remove ops, modify signatures)

This breaks both store implementations until Tasks 10 and 11 fix them.

**Files:**
- Modify: `src/Outbox.Core/Abstractions/IOutboxStore.cs`

- [ ] **Step 1: Replace the interface file**

```csharp
// src/Outbox.Core/Abstractions/IOutboxStore.cs
// Copyright (c) OrgName. All rights reserved.

using Outbox.Core.Models;

namespace Outbox.Core.Abstractions;

public interface IOutboxStore
{
    /// <summary>
    ///     Stable identity for this publisher instance, generated when the store is constructed.
    /// </summary>
    string PublisherId { get; }

    Task<string> RegisterPublisherAsync(CancellationToken ct);
    Task UnregisterPublisherAsync(string publisherId, CancellationToken ct);

    /// <summary>
    ///     Fetches the next batch of pending messages for partitions owned by this
    ///     publisher. No retry-count filter is applied — retry tracking is in-memory.
    /// </summary>
    Task<IReadOnlyList<OutboxMessage>> FetchBatchAsync(
        string publisherId, int batchSize, CancellationToken ct);

    Task DeletePublishedAsync(
        IReadOnlyList<long> sequenceNumbers, CancellationToken ct);

    /// <summary>
    ///     Atomically deletes the specified messages from the outbox table and inserts
    ///     them into the dead-letter table. <paramref name="attemptCount" /> records
    ///     how many in-memory attempts were made before giving up; it is written to
    ///     the dead-letter table's <c>AttemptCount</c> column.
    /// </summary>
    Task DeadLetterAsync(
        IReadOnlyList<long> sequenceNumbers,
        int attemptCount,
        string? lastError,
        CancellationToken ct);

    Task HeartbeatAsync(string publisherId, CancellationToken ct);
    Task<int> GetTotalPartitionsAsync(CancellationToken ct);
    Task<IReadOnlyList<int>> GetOwnedPartitionsAsync(string publisherId, CancellationToken ct);
    Task RebalanceAsync(string publisherId, CancellationToken ct);
    Task ClaimOrphanPartitionsAsync(string publisherId, CancellationToken ct);
    Task<long> GetPendingCountAsync(CancellationToken ct);
}
```

The deleted members (commented for reference, do not include in the file):

```csharp
// REMOVED: Task IncrementRetryCountAsync(IReadOnlyList<long> sequenceNumbers, CancellationToken ct);
// REMOVED: Task SweepDeadLettersAsync(string publisherId, int maxRetryCount, CancellationToken ct);
```

- [ ] **Step 2: Build the Core project**

Run: `dotnet build src/Outbox.Core/Outbox.Core.csproj`
Expected: Outbox.Core builds. The store projects will fail to compile.

- [ ] **Step 3: Commit**

```bash
git add src/Outbox.Core/Abstractions/IOutboxStore.cs
git commit -m "feat(outbox): remove IncrementRetryCount/SweepDeadLetters from IOutboxStore"
```

---

## Task 9: Update SQL Server schema (`install.sql`)

**Files:**
- Modify: `src/Outbox.SqlServer/db_scripts/install.sql`

- [ ] **Step 1: Drop the `RetryCount` column from `dbo.Outbox`**

In the `CREATE TABLE dbo.Outbox` block, delete this line:

```sql
RetryCount       INT                   NOT NULL  DEFAULT 0,
```

- [ ] **Step 2: Rename `RetryCount` to `AttemptCount` in `dbo.OutboxDeadLetter`**

In the `CREATE TABLE dbo.OutboxDeadLetter` block, change:

```sql
RetryCount        INT                   NOT NULL,
```

to:

```sql
AttemptCount      INT                   NOT NULL,
```

- [ ] **Step 3: Remove `RetryCount` from the `IX_Outbox_Pending` index INCLUDE list**

Change:

```sql
INCLUDE (SequenceNumber, TopicName, PartitionKey, EventType, Headers, Payload, PayloadContentType, RetryCount, CreatedAtUtc, RowVersion);
```

to:

```sql
INCLUDE (SequenceNumber, TopicName, PartitionKey, EventType, Headers, Payload, PayloadContentType, CreatedAtUtc, RowVersion);
```

- [ ] **Step 4: Update the diagnostic views**

In `vw_Outbox`, remove `RetryCount,` from the SELECT list. The line:

```sql
RetryCount, CreatedAtUtc, EventDateTimeUtc
```

becomes:

```sql
CreatedAtUtc, EventDateTimeUtc
```

In `vw_OutboxDeadLetter`, change `RetryCount,` to `AttemptCount,`. The line:

```sql
RetryCount, CreatedAtUtc, EventDateTimeUtc,
```

becomes:

```sql
AttemptCount, CreatedAtUtc, EventDateTimeUtc,
```

- [ ] **Step 5: Commit (no build/test step yet — this is a SQL file)**

```bash
git add src/Outbox.SqlServer/db_scripts/install.sql
git commit -m "feat(sqlserver): drop Outbox.RetryCount, rename DLQ column to AttemptCount"
```

---

## Task 10: Update SQL Server queries and store implementation

**Files:**
- Modify: `src/Outbox.SqlServer/SqlServerQueries.cs`
- Modify: `src/Outbox.SqlServer/SqlServerOutboxStore.cs`

- [ ] **Step 1: Read `SqlServerQueries.cs` to ground the edits**

Run the Read tool on `src/Outbox.SqlServer/SqlServerQueries.cs`. You need to know the exact text of the `FetchBatch`, `DeadLetter`, `IncrementRetryCount`, and `SweepDeadLetters` query strings, plus the property declarations near the top of the class.

- [ ] **Step 2: Remove `IncrementRetryCount` and `SweepDeadLetters` properties and assignments**

In `SqlServerQueries.cs`:
- Delete the property declarations `public string IncrementRetryCount { get; }` and `public string SweepDeadLetters { get; }`.
- Delete the `IncrementRetryCount = $""" ... """;` assignment block in the constructor.
- Delete the `SweepDeadLetters = $""" ... """;` assignment block in the constructor.

- [ ] **Step 3: Update the `FetchBatch` query**

Replace the existing `FetchBatch` assignment with:

```csharp
FetchBatch = $"""
             SELECT TOP (@BatchSize)
                 o.SequenceNumber, o.TopicName, o.PartitionKey, o.EventType,
                 o.Headers, o.Payload, o.PayloadContentType,
                 o.EventDateTimeUtc, o.EventOrdinal,
                 o.CreatedAtUtc
             FROM {outboxTable} o WITH (NOLOCK)
             WHERE o.PartitionId IN (
                 SELECT op.PartitionId
                 FROM {partitionsTable} op
                 WHERE op.OutboxTableName = @OutboxTableName
                   AND op.OwnerPublisherId = @PublisherId
                   AND (op.GraceExpiresUtc IS NULL OR op.GraceExpiresUtc < SYSUTCDATETIME())
             )
               AND o.RowVersion < MIN_ACTIVE_ROWVERSION()
             ORDER BY o.PartitionId, o.EventDateTimeUtc, o.EventOrdinal, o.SequenceNumber;
             """;
```

Removed: the `o.RetryCount` projection and the `AND o.RetryCount < @MaxRetryCount` filter clause.

- [ ] **Step 4: Update the `DeadLetter` query**

Replace the existing `DeadLetter` assignment with:

```csharp
DeadLetter = $"""
              DELETE o
              OUTPUT deleted.SequenceNumber, deleted.TopicName, deleted.PartitionKey,
                     deleted.EventType, deleted.Headers, deleted.Payload,
                     deleted.PayloadContentType,
                     deleted.CreatedAtUtc, @AttemptCount,
                     deleted.EventDateTimeUtc, deleted.EventOrdinal,
                     SYSUTCDATETIME(), @LastError
              INTO {deadLetterTable}(SequenceNumber, TopicName, PartitionKey, EventType,
                   Headers, Payload, PayloadContentType,
                   CreatedAtUtc, AttemptCount,
                   EventDateTimeUtc, EventOrdinal,
                   DeadLetteredAtUtc, LastError)
              FROM {outboxTable} o
              INNER JOIN @Ids p ON o.SequenceNumber = p.SequenceNumber;
              """;
```

Note: the OUTPUT clause now uses `@AttemptCount` (parameter) instead of `deleted.RetryCount`, and the INTO column list uses `AttemptCount` instead of `RetryCount`.

- [ ] **Step 5: Update `SqlServerOutboxStore.cs`**

Read `src/Outbox.SqlServer/SqlServerOutboxStore.cs`. Make these changes:

a) **Delete the `IncrementRetryCountAsync` method entirely.**

b) **Delete the `SweepDeadLettersAsync` method entirely.**

c) **Change `FetchBatchAsync` signature** — remove the `int maxRetryCount` parameter, and remove the corresponding `cmd.Parameters.AddWithValue("@MaxRetryCount", maxRetryCount)` line in the body. Also remove `o.RetryCount` from the projection mapping (the line that constructs the `OutboxMessage` should drop the `retryCount:` argument — `OutboxMessage` will lose that constructor parameter in Task 13).

d) **Change `DeadLetterAsync` signature** — add an `int attemptCount` parameter between `sequenceNumbers` and `lastError`. In the body, add a parameter to the command:

```csharp
cmd.Parameters.AddWithValue("@AttemptCount", attemptCount);
```

- [ ] **Step 6: Build the SQL Server store project**

Run: `dotnet build src/Outbox.SqlServer/Outbox.SqlServer.csproj`
Expected: the project builds, except possibly for one error in the `FetchBatchAsync` body about `OutboxMessage`'s `retryCount` parameter — that's fixed in Task 13. If you see only that error, proceed; otherwise fix the others.

If the only remaining error is the `OutboxMessage` constructor mismatch, document it and proceed.

- [ ] **Step 7: Commit**

```bash
git add src/Outbox.SqlServer/SqlServerQueries.cs src/Outbox.SqlServer/SqlServerOutboxStore.cs
git commit -m "feat(sqlserver): inline DLQ via attemptCount, drop retry/sweep queries"
```

---

## Task 11: Update PostgreSQL schema (`install.sql`)

**Files:**
- Modify: `src/Outbox.PostgreSQL/db_scripts/install.sql`

- [ ] **Step 1: Drop the `retry_count` column from `outbox`**

In the `CREATE TABLE outbox` block, delete this line:

```sql
retry_count        INT            NOT NULL DEFAULT 0,
```

- [ ] **Step 2: Rename `retry_count` to `attempt_count` in `outbox_dead_letter`**

Change:

```sql
retry_count          INT            NOT NULL,
```

to:

```sql
attempt_count        INT            NOT NULL,
```

- [ ] **Step 3: Remove `retry_count` from the `ix_outbox_pending` index INCLUDE list**

Change:

```sql
INCLUDE (sequence_number, topic_name, partition_key, event_type, retry_count, created_at_utc);
```

to:

```sql
INCLUDE (sequence_number, topic_name, partition_key, event_type, created_at_utc);
```

- [ ] **Step 4: Update the diagnostic views**

In `vw_outbox`, remove `retry_count,` from the SELECT list. The line:

```sql
retry_count, created_at_utc, event_datetime_utc
```

becomes:

```sql
created_at_utc, event_datetime_utc
```

In `vw_outbox_dead_letter`, change `retry_count,` to `attempt_count,`:

```sql
retry_count, created_at_utc, event_datetime_utc,
```

becomes:

```sql
attempt_count, created_at_utc, event_datetime_utc,
```

- [ ] **Step 5: Commit**

```bash
git add src/Outbox.PostgreSQL/db_scripts/install.sql
git commit -m "feat(postgresql): drop outbox.retry_count, rename DLQ column to attempt_count"
```

---

## Task 12: Update PostgreSQL queries and store implementation

**Files:**
- Modify: `src/Outbox.PostgreSQL/PostgreSqlQueries.cs`
- Modify: `src/Outbox.PostgreSQL/PostgreSqlOutboxStore.cs`

- [ ] **Step 1: Read `PostgreSqlQueries.cs`**

Run the Read tool on `src/Outbox.PostgreSQL/PostgreSqlQueries.cs`. Identify the FetchBatch, DeadLetter, IncrementRetryCount, and SweepDeadLetters query strings.

- [ ] **Step 2: Remove `IncrementRetryCount` and `SweepDeadLetters` properties and assignments**

In `PostgreSqlQueries.cs`:
- Delete the `IncrementRetryCount` property declaration and its constructor assignment.
- Delete the `SweepDeadLetters` property declaration and its constructor assignment.

- [ ] **Step 3: Update the `FetchBatch` query**

Replace the `FetchBatch` assignment with the equivalent of the SQL Server version, using PostgreSQL's syntax. Remove the `o.retry_count` projection and the `AND o.retry_count < @max_retry_count` clause. Keep the existing `xmin < pg_snapshot_xmin(pg_current_snapshot())::text::bigint` ceiling filter, the partition ownership filter, and the `ORDER BY` clause exactly as they are.

The new FetchBatch query (drop only the columns/clauses noted above; everything else stays the same as the existing query):

```csharp
FetchBatch = $"""
             SELECT
                 o.sequence_number, o.topic_name, o.partition_key, o.event_type,
                 o.headers, o.payload, o.payload_content_type,
                 o.event_datetime_utc, o.event_ordinal,
                 o.created_at_utc
             FROM {outboxTable} o
             WHERE (hashtext(o.partition_key) % @total_partitions) IN (
                 SELECT op.partition_id
                 FROM {partitionsTable} op
                 WHERE op.outbox_table_name = @outbox_table_name
                   AND op.owner_publisher_id = @publisher_id
                   AND (op.grace_expires_utc IS NULL OR op.grace_expires_utc < clock_timestamp())
             )
               AND o.xmin::text::bigint < pg_snapshot_xmin(pg_current_snapshot())::text::bigint
             ORDER BY o.event_datetime_utc, o.event_ordinal, o.sequence_number
             LIMIT @batch_size;
             """;
```

(If the existing query has a subtly different shape — e.g., a different name for the `total_partitions` parameter, or uses a CTE — preserve those structural elements and only remove the retry-count projection and filter. The Read tool output of the existing file is the source of truth.)

- [ ] **Step 4: Update the `DeadLetter` query**

Replace the `DeadLetter` assignment with a CTE that deletes from outbox and inserts into the dead-letter table, using `@attempt_count` instead of the old `o.retry_count` value:

```csharp
DeadLetter = $"""
              WITH deleted AS (
                  DELETE FROM {outboxTable}
                  WHERE sequence_number = ANY(@sequence_numbers)
                  RETURNING sequence_number, topic_name, partition_key, event_type,
                            headers, payload, payload_content_type,
                            created_at_utc, event_datetime_utc, event_ordinal
              )
              INSERT INTO {deadLetterTable} (
                  sequence_number, topic_name, partition_key, event_type,
                  headers, payload, payload_content_type,
                  created_at_utc, attempt_count,
                  event_datetime_utc, event_ordinal,
                  dead_lettered_at_utc, last_error
              )
              SELECT
                  sequence_number, topic_name, partition_key, event_type,
                  headers, payload, payload_content_type,
                  created_at_utc, @attempt_count,
                  event_datetime_utc, event_ordinal,
                  clock_timestamp(), @last_error
              FROM deleted;
              """;
```

(Again — if the existing query already uses a CTE structure, preserve it; just swap `o.retry_count` for `@attempt_count` in the SELECT and `retry_count` for `attempt_count` in the column list.)

- [ ] **Step 5: Update `PostgreSqlOutboxStore.cs`**

Read `src/Outbox.PostgreSQL/PostgreSqlOutboxStore.cs`. Make these changes:

a) Delete `IncrementRetryCountAsync`.
b) Delete `SweepDeadLettersAsync`.
c) `FetchBatchAsync` — drop the `int maxRetryCount` parameter and the corresponding `cmd.Parameters.AddWithValue("max_retry_count", maxRetryCount)` line. Drop `retry_count` from the projection mapping (the `OutboxMessage` constructor argument).
d) `DeadLetterAsync` — add an `int attemptCount` parameter between `sequenceNumbers` and `lastError`. Add `cmd.Parameters.AddWithValue("attempt_count", attemptCount);` to the body.

- [ ] **Step 6: Build the PostgreSQL store project**

Run: `dotnet build src/Outbox.PostgreSQL/Outbox.PostgreSQL.csproj`
Expected: the project builds. (As with Task 10, the only acceptable remaining error is the `OutboxMessage` constructor mismatch, which is fixed in Task 13.)

- [ ] **Step 7: Commit**

```bash
git add src/Outbox.PostgreSQL/PostgreSqlQueries.cs src/Outbox.PostgreSQL/PostgreSqlOutboxStore.cs
git commit -m "feat(postgresql): inline DLQ via attempt_count, drop retry/sweep queries"
```

---

## Task 13: Drop `RetryCount` from `OutboxMessage` model

**Files:**
- Modify: `src/Outbox.Core/Models/OutboxMessage.cs`

- [ ] **Step 1: Replace the record definition**

```csharp
// src/Outbox.Core/Models/OutboxMessage.cs
// Copyright (c) OrgName. All rights reserved.

namespace Outbox.Core.Models;

/// <summary>
///     Represents a message stored in the transactional outbox table.
/// </summary>
/// <remarks>
///     <para>
///         <b>Ordering contract:</b> Messages sharing the same <see cref="PartitionKey" /> are
///         delivered to the broker in <see cref="EventDateTimeUtc" /> then <see cref="EventOrdinal" />
///         order. This is enforced by the FetchBatch query's ORDER BY clause and the partition-affinity
///         model (one publisher per logical partition at a time). Callers MUST set both fields
///         correctly at insert time to achieve causal ordering.
///     </para>
///
///     <para>
///         <b>EventOrdinal:</b> A tie-breaker for events that share the same
///         <see cref="EventDateTimeUtc" />. Use sequential values (0, 1, 2, ...) within a single
///         transaction to guarantee deterministic ordering. Stored as SQL INT.
///         Defaults to 0 in the database schema if omitted.
///     </para>
///
///     <para>
///         <b>At-least-once guarantee:</b> Messages may be delivered more than once.
///         Consumers must be idempotent. Consider using <see cref="SequenceNumber" /> as a
///         deduplication key on the consumer side.
///     </para>
///
///     <para>
///         <b>Retry tracking:</b> Retry state is held in process memory by the publisher's
///         in-batch retry loop. There is no persistent retry counter on this record; restarts
///         re-fetch failed messages with a fresh attempt budget.
///     </para>
/// </remarks>
public sealed record OutboxMessage(
    long SequenceNumber,
    string TopicName,
    string PartitionKey,
    string EventType,
    Dictionary<string, string>? Headers,
    byte[] Payload,
    string PayloadContentType,
    DateTimeOffset EventDateTimeUtc,
    int EventOrdinal,
    DateTimeOffset CreatedAtUtc);
```

- [ ] **Step 2: Build the entire `src/` tree**

Run: `dotnet build src/Outbox.slnx`
Expected: SQL Server and PostgreSQL stores now compile (their `OutboxMessage` construction no longer needs the dropped argument). `Outbox.Core/Engine/OutboxPublisherService.cs` will still fail because `ProcessGroupsAsync` and `DeadLetterSweepLoopAsync` reference removed members. **That is fixed in Task 14.**

- [ ] **Step 3: Commit**

```bash
git add src/Outbox.Core/Models/OutboxMessage.cs
git commit -m "feat(outbox): drop RetryCount from OutboxMessage record"
```

---

## Task 14: Rewrite `OutboxPublisherService` retry loop and remove sweep loop

This is the largest single task in the plan. It rewrites `ProcessGroupsAsync`/`ProcessGroupAsync` around the in-batch retry loop, deletes `DeadLetterSweepLoopAsync`, and updates `RunLoopsWithRestartAsync` to manage 4 loops instead of 5.

**Files:**
- Modify: `src/Outbox.Core/Engine/OutboxPublisherService.cs`

- [ ] **Step 1: Read the current `OutboxPublisherService.cs`**

Run the Read tool on `src/Outbox.Core/Engine/OutboxPublisherService.cs`. Identify these regions for editing:
- `RunLoopsWithRestartAsync` — the `var tasks = new[] { ... }` block (currently 5 entries).
- `PublishLoopAsync` — currently calls `_store.FetchBatchAsync(publisherId, opts.BatchSize, opts.MaxRetryCount, ct)`.
- `ProcessGroupsAsync` — the worker function that processes a worker's assigned groups. Contains the per-group send/catch logic.
- `HandlePublishFailureAsync` — currently used by both the general-exception path and the `PartialSendException` path.
- `DeadLetterSweepLoopAsync` — to be deleted.
- `LogConfigurationSummary` — references `opts.MaxRetryCount`.

- [ ] **Step 2: Update `RunLoopsWithRestartAsync` to start 4 loops instead of 5**

Find the block that looks like:

```csharp
var tasks = new[]
{
    PublishLoopAsync(publisherId, circuitBreaker, ct),
    HeartbeatLoopAsync(publisherId, ct),
    RebalanceLoopAsync(publisherId, ct),
    OrphanSweepLoopAsync(publisherId, ct),
    DeadLetterSweepLoopAsync(publisherId, ct)
};
```

Change it to:

```csharp
var tasks = new[]
{
    PublishLoopAsync(publisherId, circuitBreaker, ct),
    HeartbeatLoopAsync(publisherId, ct),
    RebalanceLoopAsync(publisherId, ct),
    OrphanSweepLoopAsync(publisherId, ct)
};
```

- [ ] **Step 3: Delete `DeadLetterSweepLoopAsync`**

Find the entire `private async Task DeadLetterSweepLoopAsync(string publisherId, CancellationToken ct) { ... }` method and delete it. Also delete its top-of-file comment block if any.

- [ ] **Step 4: Update `PublishLoopAsync` `FetchBatchAsync` call**

Find the line:

```csharp
var batch = await _store.FetchBatchAsync(
    publisherId, opts.BatchSize,
    opts.MaxRetryCount, ct);
```

Change to:

```csharp
var batch = await _store.FetchBatchAsync(
    publisherId, opts.BatchSize, ct);
```

- [ ] **Step 5: Replace `ProcessGroupsAsync` with the new in-batch retry loop**

Find the existing `private async Task<bool> ProcessGroupsAsync(...)` method (currently iterates over groups and calls the old single-shot send-or-fail logic). Replace its entire body with the version below. Also delete `HandlePublishFailureAsync` — its responsibilities are folded into the new loop exits.

```csharp
private async Task<bool> ProcessGroupsAsync(
    IReadOnlyList<IGrouping<(string TopicName, string PartitionKey), OutboxMessage>> groups,
    TopicCircuitBreaker circuitBreaker,
    CancellationToken ct)
{
    var publishedAny = false;

    foreach (var group in groups)
    {
        var topicName = group.Key.TopicName;
        var partitionKey = group.Key.PartitionKey;

        var groupResult = await ProcessGroupWithRetriesAsync(
            topicName, partitionKey, group.ToList(), circuitBreaker, ct);

        if (groupResult)
            publishedAny = true;
    }

    return publishedAny;
}

/// <summary>
///     Processes one (topic, partitionKey) group with the in-batch retry loop.
///     Returns <c>true</c> if at least one message in the group reached the broker
///     (full success or partial success), <c>false</c> otherwise.
/// </summary>
private async Task<bool> ProcessGroupWithRetriesAsync(
    string topicName,
    string partitionKey,
    List<OutboxMessage> initialGroup,
    TopicCircuitBreaker circuitBreaker,
    CancellationToken ct)
{
    var opts = GetOptions();
    var maxAttempts = opts.MaxPublishAttempts;
    var attempt = 0;
    var publishedAny = false;
    Exception? lastError = null;

    var remaining = initialGroup
        .OrderBy(m => m.EventDateTimeUtc)
        .ThenBy(m => m.EventOrdinal)
        .ThenBy(m => m.SequenceNumber)
        .ToList();

    while (attempt < maxAttempts && !ct.IsCancellationRequested && remaining.Count > 0)
    {
        if (circuitBreaker.IsOpen(topicName))
        {
            // Circuit open — leave remaining messages in the outbox for the next poll.
            await NotifyPublishFailedAsync(topicName, remaining,
                lastError ?? new InvalidOperationException("Circuit breaker open"),
                PublishFailureReason.CircuitOpened, ct);
            return publishedAny;
        }

        try
        {
            var publishSw = Stopwatch.StartNew();

            using var activity = _instrumentation.ActivitySource.StartActivity("outbox.publish");
            activity?.SetTag("messaging.destination.name", topicName);
            activity?.SetTag("messaging.batch.message_count", remaining.Count);

            var effectiveMessages = await ApplyInterceptorsAsync(remaining, ct);
            await _transport.SendAsync(topicName, partitionKey, effectiveMessages, ct);

            publishSw.Stop();
            _instrumentation.PublishDuration.Record(publishSw.Elapsed.TotalMilliseconds);

            // Full success.
            await OnGroupFullySentAsync(topicName, remaining, circuitBreaker, ct);
            publishedAny = true;
            return publishedAny;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — messages stay in the outbox; no handler call (logs only).
            _logger.LogDebug(
                "Publish cancelled mid-retry for topic {Topic}, {Count} messages remain in outbox",
                topicName, remaining.Count);
            return publishedAny;
        }
        catch (PartialSendException pex)
        {
            _logger.LogWarning(pex.InnerException,
                "Partial send: {Succeeded} sent, {Failed} failed for topic {Topic}",
                pex.SucceededSequenceNumbers.Count, pex.FailedSequenceNumbers.Count, topicName);

            _instrumentation.PublishFailures.Add(1);
            publishedAny = true;
            lastError = pex.InnerException ?? pex;

            // Delete the succeeded subset; they're already on the broker.
            var succeededSet = pex.SucceededSequenceNumbers.ToHashSet();
            var succeeded = remaining.Where(m => succeededSet.Contains(m.SequenceNumber)).ToList();
            if (succeeded.Count > 0)
            {
                try
                {
                    await _store.DeletePublishedAsync(
                        succeeded.Select(m => m.SequenceNumber).ToList(),
                        CancellationToken.None);
                    _instrumentation.MessagesPublished.Add(succeeded.Count);
                    _healthState.RecordSuccessfulPublish();

                    foreach (var msg in succeeded)
                    {
                        try { await _eventHandler.OnMessagePublishedAsync(msg, ct); }
                        catch (Exception handlerEx) when (handlerEx is not OperationCanceledException)
                        {
                            _logger.LogWarning(handlerEx,
                                "OnMessagePublishedAsync handler threw for message {Seq} on topic {Topic}",
                                msg.SequenceNumber, topicName);
                        }
                    }
                }
                catch (Exception deleteEx)
                {
                    _logger.LogWarning(deleteEx,
                        "Failed to delete {Count} partially-sent messages — they will be re-delivered",
                        succeeded.Count);
                }
            }

            // Reduce the remaining set to the failed subset and fall through to classification.
            remaining = remaining.Where(m => !succeededSet.Contains(m.SequenceNumber)).ToList();
            if (remaining.Count == 0)
            {
                // Everything that was in flight made it through.
                circuitBreaker.RecordSuccess(topicName);
                return publishedAny;
            }

            ClassifyAndRecord(lastError, topicName, ref attempt, circuitBreaker);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to publish {Count} messages to topic {Topic} (attempt {Attempt}/{Max})",
                remaining.Count, topicName, attempt + 1, maxAttempts);

            _instrumentation.PublishFailures.Add(1);
            lastError = ex;

            ClassifyAndRecord(ex, topicName, ref attempt, circuitBreaker);
        }

        // Backoff before the next attempt (only if we're going to make one).
        if (attempt < maxAttempts && !ct.IsCancellationRequested && remaining.Count > 0)
        {
            try
            {
                await Task.Delay(ComputeBackoff(attempt, opts), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return publishedAny;
            }
        }
    }

    // Loop exited without success.
    if (ct.IsCancellationRequested || remaining.Count == 0)
        return publishedAny;

    // Retries exhausted via non-transient errors → DLQ inline.
    await DeadLetterAndNotifyAsync(
        topicName, remaining, attempt,
        lastError ?? new InvalidOperationException("Retries exhausted with no captured error"),
        ct);

    return publishedAny;
}

/// <summary>
///     Classifies the exception and updates either the attempt counter (non-transient)
///     or the circuit breaker (transient) per the design spec's clean separation rule.
/// </summary>
private void ClassifyAndRecord(
    Exception ex, string topicName, ref int attempt, TopicCircuitBreaker circuitBreaker)
{
    if (_transport.IsTransient(ex))
    {
        var (stateChanged, _) = circuitBreaker.RecordFailure(topicName);
        if (stateChanged)
        {
            _healthState.SetCircuitOpen(topicName);
            _instrumentation.CircuitBreakerStateChanges.Add(1);
        }
        // Do NOT increment attempt — transient does not consume a retry budget.
    }
    else
    {
        attempt++;
        // Do NOT record a circuit failure — message-level poison must not block the topic.
    }
}

private static TimeSpan ComputeBackoff(int attempt, OutboxPublisherOptions opts)
{
    var ms = opts.RetryBackoffBaseMs * Math.Pow(2, Math.Max(0, attempt - 1));
    var capped = Math.Min(ms, opts.RetryBackoffMaxMs);
    return TimeSpan.FromMilliseconds(capped);
}

/// <summary>
///     Full-success exit: delete the group, record success metrics, fire
///     <c>OnMessagePublishedAsync</c> per message, fire <c>OnCircuitBreakerStateChangedAsync</c>
///     if the circuit just closed.
/// </summary>
private async Task OnGroupFullySentAsync(
    string topicName,
    IReadOnlyList<OutboxMessage> sentMessages,
    TopicCircuitBreaker circuitBreaker,
    CancellationToken ct)
{
    _instrumentation.MessagesPublished.Add(sentMessages.Count);
    _healthState.RecordSuccessfulPublish();

    var (stateChanged, newState) = circuitBreaker.RecordSuccess(topicName);
    if (stateChanged)
    {
        _healthState.SetCircuitClosed(topicName);
        _instrumentation.CircuitBreakerStateChanges.Add(1);

        try { await _eventHandler.OnCircuitBreakerStateChangedAsync(topicName, newState, ct); }
        catch (Exception handlerEx) when (handlerEx is not OperationCanceledException)
        {
            _logger.LogWarning(handlerEx,
                "OnCircuitBreakerStateChangedAsync handler threw for topic {Topic}", topicName);
        }
    }

    foreach (var msg in sentMessages)
    {
        try { await _eventHandler.OnMessagePublishedAsync(msg, ct); }
        catch (Exception handlerEx) when (handlerEx is not OperationCanceledException)
        {
            _logger.LogWarning(handlerEx,
                "OnMessagePublishedAsync handler threw for message {Seq} on topic {Topic}",
                msg.SequenceNumber, topicName);
        }
    }

    try
    {
        await _store.DeletePublishedAsync(
            sentMessages.Select(m => m.SequenceNumber).ToList(), ct);
    }
    catch (Exception deleteEx)
    {
        _logger.LogWarning(deleteEx,
            "Failed to delete {Count} published messages — they will be re-delivered",
            sentMessages.Count);
    }
}

/// <summary>
///     Retries-exhausted exit: dead-letter inline, fire <c>OnPublishFailedAsync</c>
///     with <c>RetriesExhausted</c>, fire <c>OnMessageDeadLetteredAsync</c> per message.
///     Each handler call is individually wrapped to honor the "handler exceptions never
///     re-trigger cleanup" anti-pattern.
/// </summary>
private async Task DeadLetterAndNotifyAsync(
    string topicName,
    IReadOnlyList<OutboxMessage> failed,
    int attemptCount,
    Exception lastError,
    CancellationToken ct)
{
    try
    {
        await _store.DeadLetterAsync(
            failed.Select(m => m.SequenceNumber).ToList(),
            attemptCount,
            lastError.ToString(),
            CancellationToken.None);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex,
            "Failed to dead-letter {Count} messages on topic {Topic} after {Attempts} attempts",
            failed.Count, topicName, attemptCount);
        // Surface to the publish loop's catch — these messages will get fresh attempts later.
        return;
    }

    _instrumentation.MessagesDeadLettered.Add(failed.Count);

    await NotifyPublishFailedAsync(topicName, failed, lastError, PublishFailureReason.RetriesExhausted, ct);

    foreach (var msg in failed)
    {
        try { await _eventHandler.OnMessageDeadLetteredAsync(msg, ct); }
        catch (Exception handlerEx) when (handlerEx is not OperationCanceledException)
        {
            _logger.LogWarning(handlerEx,
                "OnMessageDeadLetteredAsync handler threw for message {Seq} on topic {Topic} — " +
                "continuing with remaining poison messages", msg.SequenceNumber, topicName);
        }
    }
}

private async Task NotifyPublishFailedAsync(
    string topicName,
    IReadOnlyList<OutboxMessage> failed,
    Exception lastError,
    PublishFailureReason reason,
    CancellationToken ct)
{
    try
    {
        await _eventHandler.OnPublishFailedAsync(failed, lastError, reason, ct);
    }
    catch (Exception handlerEx) when (handlerEx is not OperationCanceledException)
    {
        _logger.LogWarning(handlerEx,
            "OnPublishFailedAsync handler threw for topic {Topic} (reason {Reason})",
            topicName, reason);
    }
}
```

Notes on the rewrite:
- The previous `HandlePublishFailureAsync` is gone — its responsibilities are folded into `ClassifyAndRecord` (circuit/attempt update) and `NotifyPublishFailedAsync` (handler call).
- The `MessagesDeadLettered` instrumentation counter is referenced. If it does not yet exist on `OutboxInstrumentation`, add it as `public Counter<long> MessagesDeadLettered { get; } = ...;` following the same pattern as `MessagesPublished`. Verify by reading `src/Outbox.Core/Observability/OutboxInstrumentation.cs` and add the counter there if missing.
- All cleanup writes (`DeletePublishedAsync` in the partial-send path, `DeadLetterAsync`) use `CancellationToken.None` per the existing anti-pattern rule.
- Cancellation during the loop causes a `return publishedAny;` — no DLQ, no handler call, just logs.
- The `MaxRetryCount > CircuitBreakerFailureThreshold` config constraint is intentionally not relied on; the structural separation in `ClassifyAndRecord` enforces "circuit-first, then DLQ" by construction.

- [ ] **Step 6: Update `LogConfigurationSummary`**

Find the call:

```csharp
"BatchSize={BatchSize}, MaxRetry={MaxRetry}, " +
```

and the parameters:

```csharp
opts.BatchSize,
opts.MaxRetryCount,
```

Change to:

```csharp
"BatchSize={BatchSize}, MaxAttempts={MaxAttempts}, " +
```

```csharp
opts.BatchSize,
opts.MaxPublishAttempts,
```

- [ ] **Step 7: Add the missing `MessagesDeadLettered` counter to `OutboxInstrumentation` if needed**

Read `src/Outbox.Core/Observability/OutboxInstrumentation.cs`. If `MessagesDeadLettered` is not defined, add it next to `MessagesPublished`:

```csharp
public Counter<long> MessagesDeadLettered { get; }
// ... in the constructor:
MessagesDeadLettered = _meter.CreateCounter<long>(
    "outbox.messages.dead_lettered",
    description: "Number of messages moved to the dead-letter table inline");
```

If it already exists, no change needed.

- [ ] **Step 8: Build the entire `src/` tree**

Run: `dotnet build src/Outbox.slnx`
Expected: build succeeds. (Test projects may still fail — they are updated next.)

- [ ] **Step 9: Commit**

```bash
git add src/Outbox.Core/Engine/OutboxPublisherService.cs src/Outbox.Core/Observability/OutboxInstrumentation.cs
git commit -m "feat(outbox): rewrite ProcessGroupAsync around in-batch retry loop, drop sweep loop"
```

---

## Task 15: Update `TestOutboxServiceFactory` for the new API

**Files:**
- Modify: `tests/Outbox.Core.Tests/TestOutboxServiceFactory.cs`

- [ ] **Step 1: Replace the file**

```csharp
// tests/Outbox.Core.Tests/TestOutboxServiceFactory.cs
// Copyright (c) OrgName. All rights reserved.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Outbox.Core.Abstractions;
using Outbox.Core.Engine;
using Outbox.Core.Models;
using Outbox.Core.Observability;
using Outbox.Core.Options;

namespace Outbox.Core.Tests;

internal sealed class TestOutboxServiceFactory : IDisposable
{
    public IOutboxStore Store { get; }
    public IOutboxTransport Transport { get; }
    public IOutboxEventHandler EventHandler { get; }
    public IOptionsMonitor<OutboxPublisherOptions> OptionsMonitor { get; }
    public OutboxInstrumentation Instrumentation { get; }
    public OutboxHealthState HealthState { get; }
    public IHostApplicationLifetime AppLifetime { get; }
    public OutboxPublisherOptions Options { get; }

    public TestOutboxServiceFactory()
    {
        Store = Substitute.For<IOutboxStore>();
        Transport = Substitute.For<IOutboxTransport>();
        EventHandler = Substitute.For<IOutboxEventHandler>();
        Instrumentation = new OutboxInstrumentation(new TestMeterFactory());
        HealthState = new OutboxHealthState();
        AppLifetime = Substitute.For<IHostApplicationLifetime>();

        Options = new OutboxPublisherOptions
        {
            BatchSize = 10,
            MaxPublishAttempts = 5,
            RetryBackoffBaseMs = 1,           // tiny so tests don't sleep
            RetryBackoffMaxMs = 5,
            MinPollIntervalMs = 10,
            MaxPollIntervalMs = 100,
            HeartbeatIntervalMs = 100_000,
            RebalanceIntervalMs = 100_000,
            OrphanSweepIntervalMs = 100_000,
            CircuitBreakerFailureThreshold = 3,
            CircuitBreakerOpenDurationSeconds = 30
        };

        OptionsMonitor = Substitute.For<IOptionsMonitor<OutboxPublisherOptions>>();
        OptionsMonitor.CurrentValue.Returns(Options);
        OptionsMonitor.Get(Arg.Any<string>()).Returns(Options);

        Store.GetTotalPartitionsAsync(Arg.Any<CancellationToken>()).Returns(64);
        Store.PublisherId.Returns("test-publisher");

        // Default: transport classifies nothing as transient unless tests override.
        Transport.IsTransient(Arg.Any<Exception>()).Returns(false);
    }

    public OutboxPublisherService CreateService(
        IReadOnlyList<IOutboxMessageInterceptor>? interceptors = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Store);
        services.AddSingleton(Transport);
        services.AddSingleton(EventHandler);
        services.AddSingleton(Instrumentation);
        services.AddSingleton(HealthState);
        services.AddLogging();

        if (interceptors is not null)
        {
            foreach (var interceptor in interceptors)
                services.AddSingleton(interceptor);
        }

        var sp = services.BuildServiceProvider();
        return new OutboxPublisherService(sp, OptionsMonitor, AppLifetime);
    }

    public static async Task RunServiceAsync(
        OutboxPublisherService service, int runMs = 300, int waitMs = 350)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(runMs));
        await service.StartAsync(cts.Token);

        try { await Task.Delay(waitMs, CancellationToken.None); }
        catch { /* Intentionally empty */ }

        await service.StopAsync(CancellationToken.None);
    }

    public void SetupSingleBatch(string publisherId, OutboxMessage[] messages)
    {
        Store.RegisterPublisherAsync(Arg.Any<CancellationToken>()).Returns(publisherId);

        var callCount = 0;
        Store.FetchBatchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
                Interlocked.Increment(ref callCount) == 1
                    ? messages
                    : Array.Empty<OutboxMessage>());
    }

    public static OutboxMessage MakeMessage(
        long seq, string topic = "orders", string key = "key-1",
        DateTimeOffset? eventTime = null, int eventOrdinal = 0) =>
        new(seq, topic, key, "OrderCreated", null,
            System.Text.Encoding.UTF8.GetBytes("{}"), "application/json",
            eventTime ?? DateTimeOffset.UtcNow, eventOrdinal, DateTimeOffset.UtcNow);

    public void Dispose()
    {
        Instrumentation.Dispose();
        GC.SuppressFinalize(this);
    }
}
```

Notable changes from the old version:
- `Options` now uses `MaxPublishAttempts`, `RetryBackoffBaseMs`, `RetryBackoffMaxMs`. `DeadLetterSweepIntervalMs` is gone.
- `SetupSingleBatch` calls `FetchBatchAsync` with the new 3-arg signature (no `maxRetryCount`).
- `MakeMessage` no longer takes a `retryCount` parameter and no longer passes one to the `OutboxMessage` constructor.
- `Transport.IsTransient` is mocked to default `false`. Tests that need transient behavior call `Transport.IsTransient(Arg.Any<Exception>()).Returns(true);` or use a typed predicate.

- [ ] **Step 2: Build (other test files in this project will still be broken)**

Run: `dotnet build tests/Outbox.Core.Tests/Outbox.Core.Tests.csproj`
Expected: errors limited to tests that reference deleted/renamed members. `TestOutboxServiceFactory.cs` itself should compile.

- [ ] **Step 3: Commit**

```bash
git add tests/Outbox.Core.Tests/TestOutboxServiceFactory.cs
git commit -m "test(outbox): update TestOutboxServiceFactory for new options/contracts"
```

---

## Task 16: Replace `RetryCountAccuracyTests` with `RetryLoopTests`

This is the focused suite for the new in-batch retry loop. It directly exercises the scenarios from the spec's testing strategy section.

**Files:**
- Delete: `tests/Outbox.Core.Tests/Behavioral/RetryCountAccuracyTests.cs`
- Create: `tests/Outbox.Core.Tests/Behavioral/RetryLoopTests.cs`

- [ ] **Step 1: Delete the old file**

```bash
rm tests/Outbox.Core.Tests/Behavioral/RetryCountAccuracyTests.cs
```

- [ ] **Step 2: Create the new test file**

```csharp
// tests/Outbox.Core.Tests/Behavioral/RetryLoopTests.cs
// Copyright (c) OrgName. All rights reserved.

using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;
using Xunit;

namespace Outbox.Core.Tests.Behavioral;

public sealed class RetryLoopTests : IDisposable
{
    private readonly TestOutboxServiceFactory _f = new();

    public void Dispose() => _f.Dispose();

    // ─── Helper: wire up transient classification ─────────────────────────

    private void SetTransient(Exception ex) =>
        _f.Transport.IsTransient(ex).Returns(true);

    private void SetAllTransient() =>
        _f.Transport.IsTransient(Arg.Any<Exception>()).Returns(true);

    // ─── Scenario: single non-transient failure, then success ──────────────

    [Fact]
    public async Task NonTransientFailures_ThenSuccess_Deletes_AndDoesNotDeadLetter()
    {
        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        _f.SetupSingleBatch("p1", messages);

        var callCount = 0;
        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Interlocked.Increment(ref callCount);
                if (callCount < 3) throw new InvalidOperationException("poison");
                return Task.CompletedTask;
            });

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.Store.Received().DeletePublishedAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Contains(1L)),
            Arg.Any<CancellationToken>());
        await _f.Store.DidNotReceive().DeadLetterAsync(
            Arg.Any<IReadOnlyList<long>>(),
            Arg.Any<int>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ─── Scenario: non-transient failure × MaxPublishAttempts → DLQ inline ──

    [Fact]
    public async Task NonTransientFailures_DeadLetters_AfterMaxAttempts()
    {
        var messages = new[]
        {
            TestOutboxServiceFactory.MakeMessage(1),
            TestOutboxServiceFactory.MakeMessage(2)
        };
        _f.SetupSingleBatch("p1", messages);

        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("permanent poison"));

        // Default IsTransient => false, so every failure burns an attempt.

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.Store.Received(1).DeadLetterAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Count == 2 && s.Contains(1L) && s.Contains(2L)),
            _f.Options.MaxPublishAttempts,
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        await _f.EventHandler.Received(1).OnPublishFailedAsync(
            Arg.Any<IReadOnlyList<OutboxMessage>>(),
            Arg.Any<Exception>(),
            PublishFailureReason.RetriesExhausted,
            Arg.Any<CancellationToken>());

        await _f.EventHandler.Received(2).OnMessageDeadLetteredAsync(
            Arg.Any<OutboxMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonTransientFailures_DoNotRecord_CircuitBreakerFailures()
    {
        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        _f.SetupSingleBatch("p1", messages);

        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("poison"));

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        // The circuit should NOT have transitioned to open.
        await _f.EventHandler.DidNotReceive().OnCircuitBreakerStateChangedAsync(
            Arg.Any<string>(), Arg.Any<CircuitState>(), Arg.Any<CancellationToken>());
    }

    // ─── Scenario: transient × CircuitBreakerFailureThreshold → circuit opens, no DLQ ──

    [Fact]
    public async Task TransientFailures_OpenCircuit_AndDoNotDeadLetter()
    {
        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        _f.SetupSingleBatch("p1", messages);

        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("broker down"));

        SetAllTransient();   // every failure is transient

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        // Circuit should have opened.
        await _f.EventHandler.Received().OnCircuitBreakerStateChangedAsync(
            Arg.Any<string>(), CircuitState.Open, Arg.Any<CancellationToken>());

        // Nothing should have been DLQed.
        await _f.Store.DidNotReceive().DeadLetterAsync(
            Arg.Any<IReadOnlyList<long>>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        // OnPublishFailedAsync should fire with CircuitOpened.
        await _f.EventHandler.Received().OnPublishFailedAsync(
            Arg.Any<IReadOnlyList<OutboxMessage>>(),
            Arg.Any<Exception>(),
            PublishFailureReason.CircuitOpened,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransientFailures_DoNotBurn_AttemptCounter()
    {
        var messages = new[] { TestOutboxServiceFactory.MakeMessage(1) };
        _f.SetupSingleBatch("p1", messages);

        // Far more failures than MaxPublishAttempts; if the counter were burning
        // on transient errors, we'd see a DLQ call after MaxPublishAttempts attempts.
        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("broker down"));
        SetAllTransient();

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.Store.DidNotReceive().DeadLetterAsync(
            Arg.Any<IReadOnlyList<long>>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ─── Scenario: PartialSendException ────────────────────────────────────

    [Fact]
    public async Task PartialSend_DeletesSucceeded_ThenRetriesFailedSubset()
    {
        var messages = new[]
        {
            TestOutboxServiceFactory.MakeMessage(1),
            TestOutboxServiceFactory.MakeMessage(2),
            TestOutboxServiceFactory.MakeMessage(3)
        };
        _f.SetupSingleBatch("p1", messages);

        var attempt = 0;
        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Interlocked.Increment(ref attempt);
                if (attempt == 1)
                {
                    throw new PartialSendException(
                        succeededSequenceNumbers: new long[] { 1L },
                        failedSequenceNumbers: new long[] { 2L, 3L },
                        message: "partial",
                        innerException: new InvalidOperationException("inner"));
                }
                return Task.CompletedTask;   // failed subset succeeds on retry
            });

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        // Succeeded message deleted immediately
        await _f.Store.Received().DeletePublishedAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Count == 1 && s.Contains(1L)),
            Arg.Any<CancellationToken>());

        // Failed subset eventually deleted after the retry succeeded
        await _f.Store.Received().DeletePublishedAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Count == 2 && s.Contains(2L) && s.Contains(3L)),
            Arg.Any<CancellationToken>());

        await _f.Store.DidNotReceive().DeadLetterAsync(
            Arg.Any<IReadOnlyList<long>>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PartialSend_FailedSubset_NonTransientExhaustion_DeadLetters_OnlyFailedSubset()
    {
        var messages = new[]
        {
            TestOutboxServiceFactory.MakeMessage(1),
            TestOutboxServiceFactory.MakeMessage(2),
            TestOutboxServiceFactory.MakeMessage(3)
        };
        _f.SetupSingleBatch("p1", messages);

        var attempt = 0;
        _f.Transport.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Interlocked.Increment(ref attempt);
                if (attempt == 1)
                {
                    throw new PartialSendException(
                        new long[] { 1L }, new long[] { 2L, 3L }, "partial",
                        new InvalidOperationException("poison"));
                }
                throw new InvalidOperationException("poison");
            });

        var service = _f.CreateService();
        await TestOutboxServiceFactory.RunServiceAsync(service);

        await _f.Store.Received().DeletePublishedAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Count == 1 && s.Contains(1L)),
            Arg.Any<CancellationToken>());

        await _f.Store.Received(1).DeadLetterAsync(
            Arg.Is<IReadOnlyList<long>>(s => s.Count == 2 && s.Contains(2L) && s.Contains(3L)),
            _f.Options.MaxPublishAttempts,
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 3: Build the test project**

Run: `dotnet build tests/Outbox.Core.Tests/Outbox.Core.Tests.csproj`
Expected: this file compiles. Other test files in the project may still have errors; those are addressed in the next task.

- [ ] **Step 4: Commit**

```bash
git add tests/Outbox.Core.Tests/Behavioral/RetryLoopTests.cs
git rm tests/Outbox.Core.Tests/Behavioral/RetryCountAccuracyTests.cs
git commit -m "test(outbox): replace RetryCountAccuracyTests with RetryLoopTests for new model"
```

---

## Task 17: Update remaining `Outbox.Core.Tests` files for the new API

Several other test files in `tests/Outbox.Core.Tests/` reference removed members (`MaxRetryCount`, `IncrementRetryCountAsync`, `DeadLetterSweepIntervalMs`, `OnPublishFailedAsync` 3-arg signature, `MakeMessage(retryCount: ...)`, `OutboxMessage(... retryCount, ...)`).

**Files to update:**
- `tests/Outbox.Core.Tests/OutboxPublisherServiceTests.cs`
- `tests/Outbox.Core.Tests/OutboxMessageInterceptorOrchestrationTests.cs`
- `tests/Outbox.Core.Tests/Behavioral/MessageDeliveryContractTests.cs`
- `tests/Outbox.Core.Tests/Behavioral/EventHandlerIsolationTests.cs`
- `tests/Outbox.Core.Tests/Behavioral/GracefulShutdownContractTests.cs`
- `tests/Outbox.Core.Tests/OutboxMessageContextTests.cs`
- `tests/Outbox.Core.Tests/ModelTests.cs`
- `tests/Outbox.Core.Tests/OutboxBuilderTests.cs`
- `tests/Outbox.Core.Tests/OutboxHealthCheckTests.cs`

- [ ] **Step 1: For each file above, run a grep to find what needs updating**

For each file, run the Grep tool with these patterns and fix every occurrence:
- `MaxRetryCount` → `MaxPublishAttempts`
- `DeadLetterSweepIntervalMs` → delete the line entirely (the option no longer exists)
- `IncrementRetryCountAsync` references → delete the assertion (the operation no longer exists; the test is now meaningless)
- `MakeMessage(... retryCount:` → drop the `retryCount` argument from the call
- `new OutboxMessage(... retryCount, createdAtUtc)` literal constructions → drop the `retryCount` argument
- `OnPublishFailedAsync(messages, exception, ct)` (3-arg) → `OnPublishFailedAsync(messages, exception, Arg.Any<PublishFailureReason>(), ct)` for NSubstitute mocks. For tests that assert the *reason*, use the specific value.
- Tests that previously asserted `_f.Store.Received().IncrementRetryCountAsync(...)` should be replaced by assertions about `DeadLetterAsync` or `OnPublishFailedAsync` (whichever matches the scenario), or deleted if the test is no longer meaningful in the new model.

- [ ] **Step 2: For tests that previously asserted retry-count incrementation, decide per-test**

Many of the existing tests are actually testing different invariants (e.g., "handler exceptions don't burn retries", "circuit-open skip doesn't burn retries"). Under the new model, the equivalent assertions are:
- "handler exceptions don't trigger DLQ"
- "circuit-open skip doesn't trigger DLQ or call `OnPublishFailedAsync` with `RetriesExhausted`"

Translate accordingly. When in doubt, delete the test and rely on `RetryLoopTests` (Task 16) for retry-loop coverage.

- [ ] **Step 3: Build the test project incrementally**

After each file is updated, run:

```bash
dotnet build tests/Outbox.Core.Tests/Outbox.Core.Tests.csproj
```

Fix the next reported errors and repeat until the build is green.

- [ ] **Step 4: Run the full Outbox.Core.Tests suite**

```bash
dotnet test tests/Outbox.Core.Tests/Outbox.Core.Tests.csproj
```

Expected: all tests pass. If a test fails because the new code doesn't behave the way the old test asserted, investigate which side is wrong (often the test was testing an implementation detail that no longer exists; delete or rewrite).

- [ ] **Step 5: Commit**

```bash
git add tests/Outbox.Core.Tests/
git commit -m "test(outbox): update Outbox.Core.Tests for in-memory retry/DLQ model"
```

---

## Task 18: Update `Outbox.Store.Tests`

**Files:**
- `tests/Outbox.Store.Tests/` — every file that references `IncrementRetryCountAsync`, `SweepDeadLettersAsync`, `RetryCount`, or the 4-arg `FetchBatchAsync`.

- [ ] **Step 1: Find affected test files**

Run the Grep tool with patterns `IncrementRetryCount`, `SweepDeadLetters`, `RetryCount`, `MaxRetryCount` over `tests/Outbox.Store.Tests/`. List the files.

- [ ] **Step 2: Update or delete each test**

For each file:
- **Tests that exercise `IncrementRetryCountAsync` or `SweepDeadLettersAsync`:** delete them (the operations are gone).
- **Tests that exercise `FetchBatchAsync` with a `maxRetryCount`:** change the call to use the new 3-arg signature and delete any assertion about retry-count filtering.
- **Tests that exercise `DeadLetterAsync(seqs, lastError, ct)`:** change to `DeadLetterAsync(seqs, attemptCount, lastError, ct)`. Pass an explicit attempt count (e.g., `5`) and assert that the DLQ row's `AttemptCount` column equals that value.
- **Any test fixture that inserts a row with `RetryCount = X`:** drop that column from the INSERT.

- [ ] **Step 3: Build and run**

```bash
dotnet build tests/Outbox.Store.Tests/Outbox.Store.Tests.csproj
dotnet test tests/Outbox.Store.Tests/Outbox.Store.Tests.csproj
```

Expected: build green, all tests pass.

- [ ] **Step 4: Commit**

```bash
git add tests/Outbox.Store.Tests/
git commit -m "test(stores): update store contract tests for new retry/DLQ model"
```

---

## Task 19: Update `Outbox.Kafka.Tests` and `Outbox.EventHub.Tests` for collateral fallout

The transport unit-test projects may have failing tests that reference the old `OutboxMessage` constructor (with `retryCount`) or the old test factory pattern.

**Files:**
- Modify: `tests/Outbox.Kafka.Tests/KafkaMessageHelperTests.cs`, `KafkaOutboxTransportSendTests.cs`
- Modify: `tests/Outbox.EventHub.Tests/EventHubMessageHelperTests.cs`, `EventHubOutboxTransportSendTests.cs`

- [ ] **Step 1: Find references**

Grep `RetryCount` and `retryCount` over `tests/Outbox.Kafka.Tests/` and `tests/Outbox.EventHub.Tests/`.

- [ ] **Step 2: Drop the `retryCount` argument from every `new OutboxMessage(...)` call**

The constructor no longer accepts it.

- [ ] **Step 3: Build and run**

```bash
dotnet test tests/Outbox.Kafka.Tests/Outbox.Kafka.Tests.csproj
dotnet test tests/Outbox.EventHub.Tests/Outbox.EventHub.Tests.csproj
```

Expected: green. (The new `IsTransientTests` from Tasks 5 and 6 should already be passing.)

- [ ] **Step 4: Commit**

```bash
git add tests/Outbox.Kafka.Tests/ tests/Outbox.EventHub.Tests/
git commit -m "test(transports): drop retryCount from OutboxMessage constructions"
```

---

## Task 20: Update `Outbox.IntegrationTests`

The integration tests run against real PostgreSQL/SQL Server and real brokers via Testcontainers. They directly insert into and read from the outbox/DLQ tables, so schema and column changes hit them hard.

**Files:**
- `tests/Outbox.IntegrationTests/Helpers/OutboxTestHelper.cs`
- `tests/Outbox.IntegrationTests/Helpers/SqlServerTestHelper.cs`
- `tests/Outbox.IntegrationTests/Scenarios/PoisonMessageTests.cs` (and `SqlServer/SqlServerPoisonMessageTests.cs`)
- `tests/Outbox.IntegrationTests/Scenarios/CircuitBreakerRetryTests.cs`
- `tests/Outbox.IntegrationTests/Scenarios/IntermittentFailureTests.cs` (and `SqlServer/SqlServerIntermittentFailureTests.cs`)
- `tests/Outbox.IntegrationTests/Scenarios/DeadLetterReplayTests.cs` (and `SqlServer/SqlServerDeadLetterReplayTests.cs`)
- `tests/Outbox.IntegrationTests/Scenarios/ProcessKillTests.cs`
- `tests/Outbox.IntegrationTests/Scenarios/ConcurrentTransactionOrderingTests.cs` (and `SqlServer/SqlServerConcurrentTransactionOrderingTests.cs`)
- `tests/Outbox.IntegrationTests/Scenarios/OrderingTests.cs`

- [ ] **Step 1: Update helpers**

In `OutboxTestHelper.cs` and `SqlServerTestHelper.cs`, find INSERT statements that reference `retry_count`/`RetryCount` for the `outbox` table. Drop that column from the column list and the values list. Find SELECT statements that read `retry_count`/`RetryCount` from the `outbox_dead_letter`/`OutboxDeadLetter` table and rename to `attempt_count`/`AttemptCount`.

- [ ] **Step 2: Update `PoisonMessageTests` and the SQL Server variant**

These tests previously waited for the dead-letter sweep loop to move messages. Under the new model:
- The DLQ happens inline within `MaxPublishAttempts * RetryBackoff` of the poison message being fetched.
- Wait time should be much shorter (a few seconds with default backoff).
- Assertions about `RetryCount`/`AttemptCount` on the DLQ row should expect the value to equal `MaxPublishAttempts`.
- Drop any setup that configured `DeadLetterSweepIntervalMs`.

- [ ] **Step 3: Update `CircuitBreakerRetryTests`**

This test should now assert that during a broker outage:
- The circuit opens after `CircuitBreakerFailureThreshold` transient failures.
- **No rows appear in the DLQ table during the outage.** This is the load-bearing new assertion.
- Once the broker recovers, messages drain normally.

- [ ] **Step 4: Update `IntermittentFailureTests`**

Adjust assertions for the in-memory retry budget. The test should check that messages with intermittent transient failures eventually deliver without ending up in the DLQ.

- [ ] **Step 5: Update `DeadLetterReplayTests`**

Wherever the test reads from the DLQ, rename `retry_count`/`RetryCount` to `attempt_count`/`AttemptCount`.

- [ ] **Step 6: Update `ProcessKillTests`**

The test should now assert that after a process kill, on restart, the same messages are re-fetched and given a fresh attempt budget. Any assertion that retry counts persist across restarts must be removed (that property is gone by design).

- [ ] **Step 7: Update `ConcurrentTransactionOrderingTests` and `OrderingTests`**

If they reference `retry_count` in test fixture inserts, drop the column. If they reference `MaxRetryCount` in option setup, rename to `MaxPublishAttempts`. Otherwise leave alone — the ordering contract is unchanged.

- [ ] **Step 8: Build the integration test project**

```bash
dotnet build tests/Outbox.IntegrationTests/Outbox.IntegrationTests.csproj
```

Expected: green build. (Tests aren't run yet — they need Docker.)

- [ ] **Step 9: Run integration tests**

```bash
dotnet test tests/Outbox.IntegrationTests/Outbox.IntegrationTests.csproj
```

Expected: ~3 minutes; all tests pass. If any tests fail, investigate per-failure — the most common causes will be timing assumptions (the new inline DLQ is much faster than the old sweep loop) and DLQ column name mismatches.

- [ ] **Step 10: Commit**

```bash
git add tests/Outbox.IntegrationTests/
git commit -m "test(integration): adapt scenarios for in-memory retry and inline DLQ"
```

---

## Task 21: Update `Outbox.PerformanceTests` options

**Files:**
- Modify: `tests/Outbox.PerformanceTests/Helpers/PerfTestOptions.cs`

- [ ] **Step 1: Find references**

Grep `MaxRetryCount` and `DeadLetterSweepIntervalMs` over `tests/Outbox.PerformanceTests/`.

- [ ] **Step 2: Rename and remove**

In every match:
- `MaxRetryCount = N` → `MaxPublishAttempts = N`
- Delete any line that sets `DeadLetterSweepIntervalMs`.

- [ ] **Step 3: Build**

```bash
dotnet build tests/Outbox.PerformanceTests/Outbox.PerformanceTests.csproj
```

Expected: green.

- [ ] **Step 4: Commit (do NOT run the perf suite — it takes 60 minutes; that's a manual step)**

```bash
git add tests/Outbox.PerformanceTests/
git commit -m "perf: rename MaxRetryCount to MaxPublishAttempts in perf test options"
```

---

## Task 22: Run the full unit test suite as a sanity check

- [ ] **Step 1: Run all unit tests**

```bash
dotnet test tests/Outbox.Core.Tests/ \
  && dotnet test tests/Outbox.Kafka.Tests/ \
  && dotnet test tests/Outbox.EventHub.Tests/ \
  && dotnet test tests/Outbox.Store.Tests/
```

Expected: all green. If anything fails, fix it before proceeding.

- [ ] **Step 2: Run the full solution build**

```bash
dotnet build src/Outbox.slnx
```

Expected: zero warnings, zero errors.

- [ ] **Step 3: No commit (read-only verification)**

If everything is green, proceed to documentation updates. If anything is red, the task that introduced the failure should be revisited.

---

## Task 23: Update documentation

**Files:**
- Modify: `docs/outbox-requirements-invariants.md`
- Modify: `docs/known-limitations.md`
- Modify: `docs/failure-scenarios-and-integration-tests.md`
- Modify: `CLAUDE.md`
- Modify: `src/Outbox.Core/README.md`
- Modify: `src/Outbox.SqlServer/README.md`
- Modify: `src/Outbox.PostgreSQL/README.md`

- [ ] **Step 1: Update `docs/outbox-requirements-invariants.md`**

Replace the **Retry count accuracy** section (lines 22-26 in the current file) with:

```markdown
### Attempt counter and dead-lettering

- Retry tracking is **in-memory only**, scoped to the current attempt of a single (topic, partitionKey) group. There is no persistent retry counter on outbox rows.
- The attempt counter MUST be incremented ONLY when a transport send fails with a **non-transient** error, as classified by `IOutboxTransport.IsTransient`.
- Transient errors (broker unreachable, timeouts, leader-election, etc.) MUST NOT consume the attempt counter. They MUST record a circuit breaker failure instead.
- Dead-lettering happens **inline** within the publish loop: when the attempt counter reaches `MaxPublishAttempts`, the failed messages are moved to the dead-letter table via `IOutboxStore.DeadLetterAsync` before the next group is processed.
- Dead-lettering MUST NOT happen while the circuit breaker is open. The retry loop checks `circuitBreaker.IsOpen(topic)` at the top of each iteration and exits via the `CircuitOpened` branch when the circuit is open.
- On publisher restart, in-memory attempt state is lost. Failed messages are re-fetched on the next poll and given a fresh attempt budget. This is acceptable under at-least-once semantics.
```

Replace the **Circuit breaker behavior** bullet about retry counts:

```markdown
### Circuit breaker behavior

- When a circuit is **open**, the publisher MUST do nothing for the affected topic — messages stay in the outbox and will be picked up when the circuit closes. Broker outages must NOT advance the in-memory attempt counter.
- The circuit breaker MUST be fed by **transient** transport failures only. Non-transient failures (poison messages) MUST NOT trip the circuit, otherwise a single bad message could block the entire topic.
- After `CircuitBreakerOpenDurationSeconds`, the circuit transitions to **half-open** and allows one probe batch through.
- A single success in half-open MUST close the circuit. A failure MUST re-open it.
```

In the **Store Contract** section, update items 3, 4, and 9. The new wording for item 3:

```markdown
3. **DeadLetter:** MUST atomically delete from outbox and insert into dead_letter (CTE/OUTPUT INTO pattern). Does NOT check `lease_owner` (column removed). MUST carry `payload_content_type` through to the dead letter table. MUST write the supplied `attemptCount` parameter to the dead letter row's `AttemptCount` column.
```

Item 4 (the old `IncrementRetryCount` item) is **deleted entirely**.

Item 9 (the old `SweepDeadLetters` item) is **deleted entirely**.

In the **Configuration Constraints** table, delete the row:

```
| `MaxRetryCount > CircuitBreakerFailureThreshold` ...
```

In the **Anti-Patterns to Watch For** section, replace patterns #2 and #7 (which reference IncrementRetryCount and PartialSendException retry-count handling) with:

```markdown
2. **Burning the attempt counter on transient errors.** The `IOutboxTransport.IsTransient` classifier exists so transient outages don't consume the retry budget. A change that increments the attempt counter on a transient catch reintroduces the bug where outages dead-letter healthy messages.

7. **Recording a circuit breaker failure on non-transient errors.** Non-transient (message-poison) failures must not trip the circuit. The whole point of the clean separation is that one bad message can't block other groups for the same topic.
```

Add new patterns:

```markdown
11. **Dead-lettering while the circuit is open.** The retry loop must check `IsOpen(topic)` at the top of every attempt and exit via `CircuitOpened`, not via the DLQ branch. DLQing during an outage produces out-of-order DLQ inserts and confuses operators.

12. **Reintroducing a long-lived retry-count dictionary.** The attempt counter is a local variable on the worker thread's stack, scoped to one group's processing. A change that promotes it to a `ConcurrentDictionary` keyed by sequence number reintroduces the cross-batch state model this design rejects.
```

- [ ] **Step 2: Update `docs/known-limitations.md`**

Search for any text that references retry count durability, `RetryCount` column, or the dead-letter sweep loop. Replace with text that describes the new in-memory model. If the file documents "retry counts persist across publisher restarts," replace with "retry counts are in-memory and are reset on publisher restart — failed messages get a fresh attempt budget on the next poll cycle."

- [ ] **Step 3: Update `docs/failure-scenarios-and-integration-tests.md`**

For each scenario that references retry count durability or sweep-loop DLQing, update the expected behavior to match the new model. New expected behaviors per the spec:

- **Poison message:** DLQed inline within `MaxPublishAttempts * RetryBackoffMaxMs` of being fetched.
- **Broker outage:** circuit opens after `CircuitBreakerFailureThreshold` transient failures; no DLQs during the outage; messages resume after recovery.
- **Mixed poison + outage:** circuit opens before poison message exhausts retries; once recovered, poison message gets fresh attempts on the next poll and DLQs after exhausting them.
- **Publisher restart mid-retry:** in-memory state lost; same messages re-fetched with fresh attempt budget.
- **DLQ ordering:** poison messages for the same partition key DLQed in `sequence_number` order.

- [ ] **Step 4: Update `CLAUDE.md`**

In the **Review Checklist** section, replace the bullet:

```markdown
- [ ] Retry count only incremented on transport failure (never on delete failure or circuit-open skip)
```

with:

```markdown
- [ ] Attempt counter only incremented on **non-transient** transport failure; transient failures record circuit failures instead
- [ ] DLQ never happens while the circuit is open — the retry loop must exit via `CircuitOpened`, not via the DLQ branch
```

- [ ] **Step 5: Update the per-store and core READMEs**

Search each README for `RetryCount`, `retry_count`, `MaxRetryCount`, `IncrementRetryCount`, `SweepDeadLetters`, `DeadLetterSweepInterval`, `DeadLetterSweepLoop`. Replace with references to the new model:
- `MaxRetryCount` → `MaxPublishAttempts`
- "retry count column" → "in-memory attempt counter"
- "dead-letter sweep loop" → "inline dead-lettering during publish"
- Drop any documentation of `IncrementRetryCountAsync` or `SweepDeadLettersAsync` from the IOutboxStore method list.

- [ ] **Step 6: Commit**

```bash
git add docs/ CLAUDE.md src/Outbox.Core/README.md src/Outbox.SqlServer/README.md src/Outbox.PostgreSQL/README.md
git commit -m "docs(outbox): update for in-memory retry and inline dead-lettering"
```

---

## Task 24: Final verification sweep

- [ ] **Step 1: Run all unit tests**

```bash
dotnet test tests/Outbox.Core.Tests/ \
  && dotnet test tests/Outbox.Kafka.Tests/ \
  && dotnet test tests/Outbox.EventHub.Tests/ \
  && dotnet test tests/Outbox.Store.Tests/
```

Expected: all green.

- [ ] **Step 2: Run integration tests**

```bash
dotnet test tests/Outbox.IntegrationTests/
```

Expected: all green, ~3 minutes.

- [ ] **Step 3: Spot-check the schema**

Spin up the Testcontainers PG and SQL Server instances from one of the integration tests and confirm:
- `outbox` table has no `retry_count` / `RetryCount` column.
- `outbox_dead_letter` / `OutboxDeadLetter` table has `attempt_count` / `AttemptCount`, not `retry_count`.

If running the containers manually feels heavy, the schema files (`install.sql`) are the source of truth and were edited in Tasks 9 and 11; the integration test passes are the practical confirmation that the schema works end-to-end.

- [ ] **Step 4: Manual perf re-baseline (out of scope for the agent)**

The 60-minute perf suite is documented in `CLAUDE.md` as a manual step. Note in the final commit message that the perf docs (`docs/postgresql-performance-results.md`, `docs/sqlserver-performance-results.md`) need a re-baseline run before the next release.

- [ ] **Step 5: No commit (read-only verification)**

If everything is green, the change is complete.

---

## Spec coverage check

Mapping each spec section to the tasks that implement it:

| Spec section | Implementing tasks |
|---|---|
| Behavioral model — in-batch retry loop | Task 14 (the rewrite of `ProcessGroupAsync`) |
| Behavioral model — transient/non-transient classification | Tasks 4, 5, 6 (interface + Kafka + EventHub `IsTransient`); Task 14 (`ClassifyAndRecord`) |
| Behavioral model — backoff | Task 2 (options); Task 14 (`ComputeBackoff`) |
| API: `IOutboxTransport.IsTransient` | Task 4, 5, 6 |
| API: `IOutboxStore` removals/changes | Task 8 |
| API: `OutboxMessage` removal | Task 13 |
| API: `IOutboxEventHandler` + `PublishFailureReason` | Task 1 (enum), Task 7 (interface) |
| API: `OutboxPublisherService` rewrite | Task 14 |
| API: `OutboxPublisherOptions` rename and additions | Task 2 |
| Schema: SQL Server | Task 9 (DDL), Task 10 (queries + store) |
| Schema: PostgreSQL | Task 11 (DDL), Task 12 (queries + store) |
| Event handlers: `OnPublishFailedAsync` outcome-level firing | Task 14 (`NotifyPublishFailedAsync`) |
| Event handlers: `OnMessageDeadLetteredAsync` inline firing | Task 14 (`DeadLetterAndNotifyAsync`) |
| Concurrency model (unchanged) | No task — preserved as-is in Task 14 |
| Testing: unit tests for retry loop | Task 16 (`RetryLoopTests`) |
| Testing: transport classification tests | Tasks 5, 6 |
| Testing: store contract tests | Task 18 |
| Testing: integration tests | Task 20 |
| Testing: perf re-baseline | Task 21 (config rename); manual run flagged in Task 24 |
| Anti-patterns documentation | Task 23 (`outbox-requirements-invariants.md` + `CLAUDE.md` updates) |
| Documentation updates | Task 23 |

Every spec section is covered. The plan also covers collateral work the spec implied but didn't enumerate (test factory updates, transport unit-test fallout, perf-test option rename).
