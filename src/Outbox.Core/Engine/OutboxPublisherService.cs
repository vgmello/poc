// Copyright (c) OrgName. All rights reserved.

using System.Collections.Concurrent;
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

        string publisherId = null!;
        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            attempt++;

            try
            {
                publisherId = await _store.RegisterPublisherAsync(stoppingToken);

                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(2 * Math.Pow(2, attempt - 1), 60));
                _logger.LogError(ex,
                    "Failed to register outbox publisher (attempt {Attempt}), retrying in {Delay:F0}s",
                    attempt, delay.TotalSeconds);

                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }

        if (stoppingToken.IsCancellationRequested)
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
                    DeadLetterSweepLoopAsync(ct)
                };

                // Wait for the first task to complete (success or failure).
                var completed = await Task.WhenAny(tasks);

                // A loop exited — cancel the others.
                await linkedCts.CancelAsync();

                // Wait for all to finish cleanup (ignore cancellation exceptions).
                // Note: await unwraps AggregateException, so we only see the first inner exception.
                try { await Task.WhenAll(tasks); }
                catch (OperationCanceledException)
                {
                    /* expected — we just cancelled them */
                }

                // If the host is stopping, exit cleanly.
                if (stoppingToken.IsCancellationRequested)
                    return;

                // A loop exited unexpectedly. Check if it faulted.
                if (completed.IsFaulted)
                {
                    _logger.LogError(completed.Exception!.InnerException,
                        "Outbox loop crashed unexpectedly, will attempt restart");
                }
                else
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

                var batch = await _store.LeaseBatchAsync(
                    publisherId, opts.BatchSize, opts.LeaseDurationSeconds,
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

                // Separate poison messages
                var poison = batch.Where(m => m.RetryCount >= opts.MaxRetryCount).ToList();
                var healthy = batch.Where(m => m.RetryCount < opts.MaxRetryCount).ToList();

                // Track all leased sequences (poison + healthy) so the finally
                // block can release any that aren't finalized on cancellation.
                var unprocessedSequences = new ConcurrentDictionary<long, byte>(
                    batch.Select(m => new KeyValuePair<long, byte>(m.SequenceNumber, 0)));

                // Dead-letter poison messages (CancellationToken.None — must complete even during shutdown)
                if (poison.Count > 0)
                {
                    await _store.DeadLetterAsync(
                        publisherId,
                        poison.Select(m => m.SequenceNumber).ToList(),
                        "Max retry count exceeded",
                        CancellationToken.None);

                    foreach (var sn in poison.Select(m => m.SequenceNumber))
                        unprocessedSequences.TryRemove(sn, out _);

                    _instrumentation.MessagesDeadLettered.Add(poison.Count);

                    foreach (var msg in poison)
                    {
                        try
                        {
                            await _eventHandler.OnMessageDeadLetteredAsync(msg, ct);
                        }
                        catch (Exception handlerEx) when (handlerEx is not OperationCanceledException)
                        {
                            _logger.LogWarning(handlerEx,
                                "OnMessageDeadLetteredAsync handler threw for message {Seq} — " +
                                "message is already dead-lettered, continuing",
                                msg.SequenceNumber);
                        }
                    }
                }

                // Group healthy messages by (TopicName, PartitionKey)
                var groups = healthy
                    .GroupBy(m => (m.TopicName, m.PartitionKey))
                    .ToList();

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

                // If we leased messages but none were actually published (e.g., all circuits
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

            if (circuitBreaker.IsOpen(topicName))
            {
                // Circuit open — release without incrementing retry count.
                await _store.ReleaseLeaseAsync(publisherId, sequenceNumbers,
                    incrementRetry: false, CancellationToken.None);
                foreach (var sn in sequenceNumbers)
                    unprocessedSequences.TryRemove(sn, out _);

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

                foreach (var sn in sequenceNumbers)
                    unprocessedSequences.TryRemove(sn, out _);

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
                    await _store.DeletePublishedAsync(publisherId, sequenceNumbers, ct);
                }
                catch (Exception deleteEx)
                {
                    _logger.LogWarning(deleteEx,
                        "Failed to delete {Count} published messages — they will be re-delivered on next poll",
                        sequenceNumbers.Count);

                    // Release WITHOUT retry increment — transport succeeded.
                    try
                    {
                        await _store.ReleaseLeaseAsync(publisherId, sequenceNumbers,
                            incrementRetry: false, CancellationToken.None);
                    }
                    catch (Exception releaseEx)
                    {
                        _logger.LogWarning(releaseEx,
                            "Failed to release lease for {Count} published messages — leases will expire naturally",
                            sequenceNumbers.Count);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Graceful shutdown — don't rethrow yet, let finally release leases.
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
                    await _store.DeletePublishedAsync(publisherId, pex.SucceededSequenceNumbers, CancellationToken.None);
                    _instrumentation.MessagesPublished.Add(pex.SucceededSequenceNumbers.Count);
                    _healthState.RecordSuccessfulPublish();
                    // Only remove from safety net after successful delete
                    foreach (var sn in pex.SucceededSequenceNumbers)
                        unprocessedSequences.TryRemove(sn, out _);
                }
                catch (Exception deleteEx)
                {
                    _logger.LogWarning(deleteEx,
                        "Failed to delete {Count} partially-sent messages — they will be re-delivered",
                        pex.SucceededSequenceNumbers.Count);

                    try
                    {
                        await _store.ReleaseLeaseAsync(publisherId, pex.SucceededSequenceNumbers,
                            incrementRetry: false, CancellationToken.None);
                        // Only remove from safety net after successful release
                        foreach (var sn in pex.SucceededSequenceNumbers)
                            unprocessedSequences.TryRemove(sn, out _);
                    }
                    catch (Exception releaseEx)
                    {
                        _logger.LogWarning(releaseEx,
                            "Failed to release lease for {Count} partially-sent succeeded messages after delete failure — leases will expire naturally",
                            pex.SucceededSequenceNumbers.Count);
                        // Leave in unprocessedSequences — finally block will attempt release
                    }
                }

                // Release the failed messages with retry increment
                try
                {
                    await _store.ReleaseLeaseAsync(publisherId, pex.FailedSequenceNumbers,
                        incrementRetry: true, CancellationToken.None);
                    // Only remove from safety net after successful release
                    foreach (var sn in pex.FailedSequenceNumbers)
                        unprocessedSequences.TryRemove(sn, out _);
                }
                catch (Exception releaseEx)
                {
                    _logger.LogWarning(releaseEx,
                        "Failed to release lease for {Count} failed messages — they will expire after lease duration",
                        pex.FailedSequenceNumbers.Count);
                    // Leave in unprocessedSequences — finally block will attempt release
                }

                // Record failure for circuit breaker (the send did partially fail)
                var (stateChanged, newState) = circuitBreaker.RecordFailure(topicName);

                if (stateChanged)
                {
                    _healthState.SetCircuitOpen(topicName);
                    _instrumentation.CircuitBreakerStateChanges.Add(1);
                }

                try
                {
                    await _eventHandler.OnPublishFailedAsync(groupMessages, pex, ct);
                }
                catch (Exception handlerEx) when (handlerEx is not OperationCanceledException)
                {
                    _logger.LogWarning(handlerEx,
                        "OnPublishFailedAsync handler threw after partial send for topic {Topic} — " +
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
            catch (Exception ex)
            {
                // Transport failure — increment retry count.
                _logger.LogError(ex, "Failed to publish {Count} messages to topic {Topic}", groupMessages.Count, topicName);
                _instrumentation.PublishFailures.Add(1);

                // Use CancellationToken.None — this must complete even during shutdown
                // so the retry count is correctly incremented.
                try
                {
                    await _store.ReleaseLeaseAsync(publisherId, sequenceNumbers,
                        incrementRetry: true, CancellationToken.None);
                    // Only remove from safety net after successful release
                    foreach (var sn in sequenceNumbers)
                        unprocessedSequences.TryRemove(sn, out _);
                }
                catch (Exception releaseEx)
                {
                    _logger.LogWarning(releaseEx,
                        "Failed to release lease with retry increment for {Count} messages — they will expire after lease duration",
                        sequenceNumbers.Count);
                    // Leave in unprocessedSequences — finally block will attempt release
                }

                var (stateChanged, newState) = circuitBreaker.RecordFailure(topicName);

                // Update health state before event handler to avoid skipping on handler exception.
                if (stateChanged)
                {
                    _healthState.SetCircuitOpen(topicName);
                    _instrumentation.CircuitBreakerStateChanges.Add(1);
                }

                try
                {
                    await _eventHandler.OnPublishFailedAsync(groupMessages, ex, ct);
                }
                catch (Exception handlerEx) when (handlerEx is not OperationCanceledException)
                {
                    _logger.LogWarning(handlerEx,
                        "OnPublishFailedAsync handler threw after transport failure for topic {Topic} — " +
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
        }

        return publishedAny;
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

    private async Task DeadLetterSweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(GetOptions().DeadLetterSweepIntervalMs, ct);
                await _store.SweepDeadLettersAsync(GetOptions().MaxRetryCount, ct);
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

    private OutboxPublisherOptions GetOptions() =>
        _options.Get(_groupName ?? Microsoft.Extensions.Options.Options.DefaultName);

    private void LogConfigurationSummary(OutboxPublisherOptions opts)
    {
        _logger.LogInformation(
            "Outbox publisher starting with configuration: " +
            "BatchSize={BatchSize}, LeaseDuration={LeaseDuration}s, MaxRetry={MaxRetry}, " +
            "Poll={MinPoll}-{MaxPoll}ms, Heartbeat={HbInterval}ms/timeout={HbTimeout}s, " +
            "GracePeriod={Grace}s, CircuitBreaker={CbThreshold}failures/{CbDuration}s, " +
            "Interceptors={InterceptorCount}",
            opts.BatchSize,
            opts.LeaseDurationSeconds,
            opts.MaxRetryCount,
            opts.MinPollIntervalMs,
            opts.MaxPollIntervalMs,
            opts.HeartbeatIntervalMs,
            opts.HeartbeatTimeoutSeconds,
            opts.PartitionGracePeriodSeconds,
            opts.CircuitBreakerFailureThreshold,
            opts.CircuitBreakerOpenDurationSeconds,
            _interceptors.Count);
    }
}
