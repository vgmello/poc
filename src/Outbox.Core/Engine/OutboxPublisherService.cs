// Copyright (c) OrgName. All rights reserved.

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;
using Outbox.Core.Observability;
using Outbox.Core.Options;

namespace Outbox.Core.Engine;

#pragma warning disable S3776 // Cognitive Complexity — publish loop and restart loop are inherently complex orchestration methods
#pragma warning disable S107 // Constructor has too many parameters — DI requires all dependencies
internal sealed class OutboxPublisherService : BackgroundService
{
    private const int MaxConsecutiveRestarts = 5;
    private static readonly TimeSpan RestartBaseDelay = TimeSpan.FromSeconds(2);

    private readonly IOutboxStore _store;
    private readonly IOutboxTransport _transport;
    private readonly IOutboxEventHandler _eventHandler;
    private readonly IOptionsMonitor<OutboxPublisherOptions> _options;
    private readonly ILogger<OutboxPublisherService> _logger;
    private readonly OutboxInstrumentation _instrumentation;
    private readonly OutboxHealthState _healthState;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly List<IOutboxMessageInterceptor> _interceptors;
    private readonly string? _groupName;

    public OutboxPublisherService(
        IServiceProvider serviceProvider,
        IOptionsMonitor<OutboxPublisherOptions> options,
        IHostApplicationLifetime appLifetime,
        string? groupName = null)
    {
        _groupName = groupName;
        _options = options;
        _appLifetime = appLifetime;
        _logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<OutboxPublisherService>();

        if (groupName is not null)
        {
            _store = serviceProvider.GetRequiredKeyedService<IOutboxStore>(groupName);
            _transport = serviceProvider.GetRequiredKeyedService<IOutboxTransport>(groupName);
            _eventHandler = serviceProvider.GetKeyedService<IOutboxEventHandler>(groupName)
                ?? serviceProvider.GetRequiredService<IOutboxEventHandler>();
            _instrumentation = serviceProvider.GetRequiredKeyedService<OutboxInstrumentation>(groupName);
            _healthState = serviceProvider.GetRequiredKeyedService<OutboxHealthState>(groupName);
            _interceptors = serviceProvider.GetKeyedServices<IOutboxMessageInterceptor>(groupName).ToList();
        }
        else
        {
            _store = serviceProvider.GetRequiredService<IOutboxStore>();
            _transport = serviceProvider.GetRequiredService<IOutboxTransport>();
            _eventHandler = serviceProvider.GetRequiredService<IOutboxEventHandler>();
            _instrumentation = serviceProvider.GetRequiredService<OutboxInstrumentation>();
            _healthState = serviceProvider.GetRequiredService<OutboxHealthState>();
            _interceptors = serviceProvider.GetServices<IOutboxMessageInterceptor>().ToList();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = GetOptions();
        LogConfigurationSummary(opts);

        using var groupScope = _groupName is not null
            ? _logger.BeginScope(new Dictionary<string, object?> { ["OutboxGroup"] = _groupName })
            : null;

        var circuitBreaker = new TopicCircuitBreaker(
            opts.CircuitBreakerFailureThreshold,
            opts.CircuitBreakerOpenDurationSeconds);

        var publisherId = await RegisterPublisherWithRetryAsync(stoppingToken);

        if (publisherId is null)
            return;

        _logger.LogInformation("Outbox publisher registered as publisher {PublisherId}", publisherId);

        _instrumentation.RegisterPendingGauge();

        try
        {
            await RunLoopsWithRestartAsync(publisherId, circuitBreaker, stoppingToken);
        }
        finally
        {
            _healthState.SetPublishLoopRunning(false);

            try
            {
                await _store.UnregisterPublisherAsync(publisherId, CancellationToken.None);
                _logger.LogInformation("Outbox publisher unregistered publisher {PublisherId}", publisherId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to unregister publisher {PublisherId} during shutdown", publisherId);
            }
        }
    }

    /// <summary>
    ///     Runs all loops with a linked CancellationTokenSource. If any loop exits
    ///     unexpectedly, all loops are cancelled and restarted with exponential backoff.
    ///     After <see cref="MaxConsecutiveRestarts" /> consecutive failures, the host is stopped.
    /// </summary>
    private async Task RunLoopsWithRestartAsync(
        string publisherId, TopicCircuitBreaker circuitBreaker, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var ct = linkedCts.Token;

            _healthState.SetPublishLoopRunning(true);

            try
            {
                var tasks = new[]
                {
                    PublishLoopAsync(publisherId, circuitBreaker, ct),
                    HeartbeatLoopAsync(publisherId, ct),
                    RebalanceLoopAsync(publisherId, ct),
                    OrphanSweepLoopAsync(publisherId, ct),
                    DeadLetterSweepLoopAsync(publisherId, ct)
                };

                // Wait for the first task to complete (success or failure).
                var completed = await Task.WhenAny(tasks);

                // A loop exited — cancel the others.
                await linkedCts.CancelAsync();

                // Wait for all to finish cleanup. This helper inspects every task
                // individually so non-cancellation faults aren't swallowed when a
                // concurrent OperationCanceledException is unwrapped first.
                await AwaitAllSuppressingCancellationAsync(tasks);

                // If the host is stopping, exit cleanly.
                if (stoppingToken.IsCancellationRequested)
                    return;

                // Faults were already logged by the helper; only log here when the
                // first completed loop exited cleanly without error.
                if (!completed.IsFaulted)
                {
                    _logger.LogWarning("Outbox loop exited unexpectedly without error, will attempt restart");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in outbox loop orchestration");

                if (stoppingToken.IsCancellationRequested)
                    return;
            }

            _healthState.SetPublishLoopRunning(false);
            _healthState.RecordLoopRestart();
            var restarts = _healthState.ConsecutiveLoopRestarts;

            if (restarts >= MaxConsecutiveRestarts)
            {
                _logger.LogCritical(
                    "Outbox loops have restarted {Count} consecutive times — stopping host",
                    restarts);
                _appLifetime.StopApplication();

                return;
            }

            // Exponential backoff before restart, capped at 2 minutes.
            var delay = TimeSpan.FromTicks((long)(RestartBaseDelay.Ticks * Math.Pow(2, restarts - 1)));
            if (delay > TimeSpan.FromMinutes(2))
                delay = TimeSpan.FromMinutes(2);
            _logger.LogWarning(
                "Restarting outbox loops in {Delay:F1}s (attempt {Attempt}/{Max})",
                delay.TotalSeconds, restarts, MaxConsecutiveRestarts);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task PublishLoopAsync(
        string publisherId, TopicCircuitBreaker circuitBreaker, CancellationToken ct)
    {
        var opts = GetOptions();
        var pollIntervalMs = opts.MinPollIntervalMs;

        // Reset restart counter after sustained healthy operation (30s of successful polling
        // without any loop crashes), rather than on the very first poll.
        var loopStartedAt = Environment.TickCount64;
        const long healthyRunThresholdMs = 30_000;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                opts = GetOptions();
                var pollSw = Stopwatch.StartNew();

                var batch = await _store.FetchBatchAsync(
                    publisherId, opts.BatchSize,
                    opts.MaxRetryCount, ct);

                pollSw.Stop();
                _instrumentation.PollDuration.Record(pollSw.Elapsed.TotalMilliseconds);
                _instrumentation.BatchSize.Record(batch.Count);
                _healthState.RecordPoll();

                if (_healthState.ConsecutiveLoopRestarts > 0 &&
                    Environment.TickCount64 - loopStartedAt >= healthyRunThresholdMs)
                {
                    _healthState.ResetLoopRestarts();
                }

                if (batch.Count == 0)
                {
                    pollIntervalMs = Math.Min(pollIntervalMs * 2, opts.MaxPollIntervalMs);
                    await Task.Delay(pollIntervalMs, ct);

                    continue;
                }

                pollIntervalMs = opts.MinPollIntervalMs;

                // Group messages by (TopicName, PartitionKey)
                var groups = batch
                    .GroupBy(m => (m.TopicName, m.PartitionKey))
                    .ToList();

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

                // Launch workers concurrently
                var workerTasks = workerGroups
                    .Where(wg => wg.Count > 0)
                    .Select(wg => ProcessGroupsAsync(
                        wg, circuitBreaker, ct))
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

                // If no messages were actually published (e.g., all circuits
                // open or all errored), apply adaptive backoff to avoid a hot loop.
                if (!publishedAny && batch.Count > 0)
                {
                    pollIntervalMs = Math.Min(pollIntervalMs * 2, opts.MaxPollIntervalMs);

                    try { await Task.Delay(pollIntervalMs, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                }

                // If cancellation was requested during group processing, exit now.
                if (ct.IsCancellationRequested)
                    break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in publish loop");

                try
                {
                    await Task.Delay(GetOptions().MaxPollIntervalMs, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

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
            var groupMessages = group.OrderBy(m => m.EventDateTimeUtc).ThenBy(m => m.EventOrdinal).ThenBy(m => m.SequenceNumber).ToList();
            var sequenceNumbers = groupMessages.Select(m => m.SequenceNumber).ToList();

            if (circuitBreaker.IsOpen(topicName))
            {
                // Circuit open — skip without incrementing retry count.
                continue;
            }

            try
            {
                var publishSw = Stopwatch.StartNew();

                using var activity = _instrumentation.ActivitySource.StartActivity("outbox.publish");
                activity?.SetTag("messaging.destination.name", topicName);
                activity?.SetTag("messaging.batch.message_count", groupMessages.Count);

                var effectiveMessages = await ApplyInterceptorsAsync(groupMessages, ct);
                await _transport.SendAsync(topicName, partitionKey, effectiveMessages, ct);

                publishSw.Stop();
                _instrumentation.PublishDuration.Record(publishSw.Elapsed.TotalMilliseconds);

                // Transport succeeded — record success metrics before attempting delete.
                _instrumentation.MessagesPublished.Add(groupMessages.Count);
                _healthState.RecordSuccessfulPublish();
                publishedAny = true;

                var (stateChanged, newState) = circuitBreaker.RecordSuccess(topicName);

                if (stateChanged)
                {
                    _healthState.SetCircuitClosed(topicName);
                    _instrumentation.CircuitBreakerStateChanges.Add(1);

                    try
                    {
                        await _eventHandler.OnCircuitBreakerStateChangedAsync(topicName, newState, ct);
                    }
                    catch (Exception handlerEx) when (handlerEx is not OperationCanceledException)
                    {
                        _logger.LogWarning(handlerEx,
                            "OnCircuitBreakerStateChangedAsync handler threw for topic {Topic} — " +
                            "circuit state is already updated, continuing", topicName);
                    }
                }

                foreach (var msg in groupMessages)
                {
                    try
                    {
                        await _eventHandler.OnMessagePublishedAsync(msg, ct);
                    }
                    catch (Exception handlerEx) when (handlerEx is not OperationCanceledException)
                    {
                        _logger.LogWarning(handlerEx,
                            "OnMessagePublishedAsync handler threw for message {Seq} on topic {Topic} — " +
                            "message fate is already finalized (transport succeeded), continuing",
                            msg.SequenceNumber, topicName);
                    }
                }

                // Delete from outbox — separate try since transport already succeeded.
                try
                {
                    await _store.DeletePublishedAsync(sequenceNumbers, ct);
                }
                catch (Exception deleteEx)
                {
                    _logger.LogWarning(deleteEx,
                        "Failed to delete {Count} published messages — they will be re-delivered on next poll",
                        sequenceNumbers.Count);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Graceful shutdown — exit the group processing loop.
                break;
            }
            catch (PartialSendException pex)
            {
                // Some messages were sent, others failed.
                _logger.LogWarning(pex.InnerException,
                    "Partial send: {Succeeded} messages sent, {Failed} failed for topic {Topic}",
                    pex.SucceededSequenceNumbers.Count, pex.FailedSequenceNumbers.Count, topicName);

                _instrumentation.PublishFailures.Add(1);
                publishedAny = true; // Some messages did get through

                // Delete the succeeded messages — they're already on the broker
                try
                {
                    await _store.DeletePublishedAsync(pex.SucceededSequenceNumbers, CancellationToken.None);
                    _instrumentation.MessagesPublished.Add(pex.SucceededSequenceNumbers.Count);
                    _healthState.RecordSuccessfulPublish();
                }
                catch (Exception deleteEx)
                {
                    _logger.LogWarning(deleteEx,
                        "Failed to delete {Count} partially-sent messages — they will be re-delivered",
                        pex.SucceededSequenceNumbers.Count);
                }

                // Increment retry count for failed messages
                try
                {
                    await _store.IncrementRetryCountAsync(pex.FailedSequenceNumbers, CancellationToken.None);
                }
                catch (Exception retryEx)
                {
                    _logger.LogWarning(retryEx,
                        "Failed to increment retry count for {Count} failed messages",
                        pex.FailedSequenceNumbers.Count);
                }

                var failedMessages = groupMessages
                    .Where(m => pex.FailedSequenceNumbers.Contains(m.SequenceNumber))
                    .ToList();

                await HandlePublishFailureAsync(topicName, failedMessages, pex, circuitBreaker, ct);
            }
            catch (Exception ex)
            {
                // Transport failure — increment retry count.
                _logger.LogError(ex, "Failed to publish {Count} messages to topic {Topic}", groupMessages.Count, topicName);
                _instrumentation.PublishFailures.Add(1);

                // Use CancellationToken.None — this must complete even during shutdown
                // so the retry count is correctly incremented.
                try
                {
                    await _store.IncrementRetryCountAsync(sequenceNumbers, CancellationToken.None);
                }
                catch (Exception retryEx)
                {
                    _logger.LogWarning(retryEx,
                        "Failed to increment retry count for {Count} messages",
                        sequenceNumbers.Count);
                }

                await HandlePublishFailureAsync(topicName, groupMessages, ex, circuitBreaker, ct);
            }
        }

        return publishedAny;
    }

    /// <summary>
    ///     Records a publish failure against the circuit breaker, updates health state,
    ///     and invokes the corresponding event handlers. Used from both the partial-send
    ///     and general-exception paths in <see cref="ProcessGroupsAsync" />.
    /// </summary>
    private async Task HandlePublishFailureAsync(
        string topicName,
        IReadOnlyList<OutboxMessage> failedMessages,
        Exception exception,
        TopicCircuitBreaker circuitBreaker,
        CancellationToken ct)
    {
        var (stateChanged, newState) = circuitBreaker.RecordFailure(topicName);

        // Update health state before event handler to avoid skipping on handler exception.
        if (stateChanged)
        {
            _healthState.SetCircuitOpen(topicName);
            _instrumentation.CircuitBreakerStateChanges.Add(1);
        }

        try
        {
            await _eventHandler.OnPublishFailedAsync(failedMessages, exception, ct);
        }
        catch (Exception handlerEx) when (handlerEx is not OperationCanceledException)
        {
            _logger.LogWarning(handlerEx,
                "OnPublishFailedAsync handler threw for topic {Topic} — " +
                "message fates are already finalized, continuing", topicName);
        }

        if (stateChanged)
        {
            try
            {
                await _eventHandler.OnCircuitBreakerStateChangedAsync(topicName, newState, ct);
            }
            catch (Exception handlerEx) when (handlerEx is not OperationCanceledException)
            {
                _logger.LogWarning(handlerEx,
                    "OnCircuitBreakerStateChangedAsync handler threw for topic {Topic} — " +
                    "circuit state is already updated, continuing", topicName);
            }
        }
    }

    /// <summary>
    ///     Registers the publisher with the store, retrying with exponential backoff on transient
    ///     failures. Returns the assigned publisher id, or <c>null</c> if cancellation was requested
    ///     before registration succeeded.
    /// </summary>
    private async Task<string?> RegisterPublisherWithRetryAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            attempt++;

            try
            {
                return await _store.RegisterPublisherAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(2 * Math.Pow(2, attempt - 1), 60));
                _logger.LogError(ex,
                    "Failed to register outbox publisher {PublisherId} (attempt {Attempt}), retrying in {Delay:F0}s",
                    _store.PublisherId, attempt, delay.TotalSeconds);

                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return null; }
            }
        }

        return null;
    }

    /// <summary>
    ///     Awaits all provided tasks, suppressing <see cref="OperationCanceledException" /> from
    ///     any of them (expected when we cancel during shutdown/restart) while still logging any
    ///     non-cancellation faults from every task. This avoids the pitfall where
    ///     <c>await Task.WhenAll</c> unwraps only the first inner exception and silently drops
    ///     concurrent real faults that occurred alongside a cancellation.
    /// </summary>
    private async Task AwaitAllSuppressingCancellationAsync(Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // Swallow — we inspect every task below to surface all faults.
        }

        foreach (var task in tasks)
        {
            if (!task.IsFaulted || task.Exception is null)
                continue;

            foreach (var ex in task.Exception.InnerExceptions)
            {
                if (ex is OperationCanceledException)
                    continue;

                _logger.LogError(ex, "Outbox loop task faulted");
            }
        }
    }

    private async Task HeartbeatLoopAsync(string publisherId, CancellationToken ct)
    {
        var consecutiveFailures = 0;
        const int maxConsecutiveHeartbeatFailures = 3;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(GetOptions().HeartbeatIntervalMs, ct);
                await _store.HeartbeatAsync(publisherId, ct);
                _healthState.RecordHeartbeat();
                consecutiveFailures = 0;

                // Also update pending count metric while we're at it.
                try
                {
                    var pending = await _store.GetPendingCountAsync(ct);
                    _instrumentation.UpdatePendingCount(pending);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to query pending message count");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                _logger.LogError(ex,
                    "Error in heartbeat loop (consecutive failure {Count}/{Max})",
                    consecutiveFailures, maxConsecutiveHeartbeatFailures);

                if (consecutiveFailures >= maxConsecutiveHeartbeatFailures)
                {
                    _logger.LogCritical(
                        "Heartbeat failed {Count} consecutive times — exiting loop to trigger restart",
                        consecutiveFailures);

                    throw;
                }
            }
        }
    }

    private async Task RebalanceLoopAsync(string publisherId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(GetOptions().RebalanceIntervalMs, ct);
                await _store.RebalanceAsync(publisherId, ct);
                var ownedPartitions = await _store.GetOwnedPartitionsAsync(publisherId, ct);

                try
                {
                    await _eventHandler.OnRebalanceAsync(publisherId, ownedPartitions, ct);
                }
                catch (Exception handlerEx) when (handlerEx is not OperationCanceledException)
                {
                    _logger.LogWarning(handlerEx,
                        "OnRebalanceAsync handler threw for publisher {PublisherId} — continuing",
                        publisherId);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in rebalance loop");
            }
        }
    }

    private async Task OrphanSweepLoopAsync(string publisherId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(GetOptions().OrphanSweepIntervalMs, ct);
                await _store.ClaimOrphanPartitionsAsync(publisherId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in orphan sweep loop");
            }
        }
    }

    private async Task DeadLetterSweepLoopAsync(string publisherId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(GetOptions().DeadLetterSweepIntervalMs, ct);
                await _store.SweepDeadLettersAsync(publisherId, GetOptions().MaxRetryCount, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in dead letter sweep loop");
            }
        }
    }

    private async Task<IReadOnlyList<OutboxMessage>> ApplyInterceptorsAsync(
        List<OutboxMessage> messages, CancellationToken ct)
    {
        if (_interceptors.Count == 0)
            return messages;

        List<OutboxMessage>? result = null;

        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            OutboxMessageContext? context = null;

            foreach (var interceptor in _interceptors)
            {
                if (interceptor.AppliesTo(msg))
                {
                    context ??= new OutboxMessageContext(msg);
                    await interceptor.InterceptAsync(context, ct);
                }
            }

            if (context is not null)
            {
                result ??= [..messages];
                result[i] = context.ToOutboxMessage();
            }
        }

        return result ?? messages;
    }

    private static int ComputeWorkerIndex(string partitionKey, int totalPartitions, int workerCount)
    {
        // Deterministic hash for in-memory worker assignment within a poll cycle.
        // Does NOT need to match the SQL hash — only needs consistency within a single process.
        var hash = (uint)partitionKey.GetHashCode();
        var partitionId = (int)(hash % (uint)totalPartitions);
        return partitionId % workerCount;
    }

    private OutboxPublisherOptions GetOptions() =>
        _options.Get(_groupName ?? Microsoft.Extensions.Options.Options.DefaultName);

    private void LogConfigurationSummary(OutboxPublisherOptions opts)
    {
        _logger.LogInformation(
            "Outbox publisher starting with configuration: " +
            "BatchSize={BatchSize}, MaxRetry={MaxRetry}, " +
            "Poll={MinPoll}-{MaxPoll}ms, Heartbeat={HbInterval}ms/timeout={HbTimeout}s, " +
            "GracePeriod={Grace}s, CircuitBreaker={CbThreshold}failures/{CbDuration}s, " +
            "PublishThreads={PublishThreads}, Interceptors={InterceptorCount}",
            opts.BatchSize,
            opts.MaxRetryCount,
            opts.MinPollIntervalMs,
            opts.MaxPollIntervalMs,
            opts.HeartbeatIntervalMs,
            opts.HeartbeatTimeoutSeconds,
            opts.PartitionGracePeriodSeconds,
            opts.CircuitBreakerFailureThreshold,
            opts.CircuitBreakerOpenDurationSeconds,
            opts.PublishThreadCount,
            _interceptors.Count);
    }
}
