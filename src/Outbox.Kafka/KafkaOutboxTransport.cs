// Copyright (c) OrgName. All rights reserved.

using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Outbox.Core.Abstractions;
using Outbox.Core.Models;

namespace Outbox.Kafka;

internal sealed class KafkaOutboxTransport : IOutboxTransport
{
    private readonly IProducer<string, byte[]> _producer;
    private readonly ILogger<KafkaOutboxTransport> _logger;
    private readonly int _sendTimeoutMs;
    private readonly int _maxBatchSizeBytes;
    private readonly List<ITransportMessageInterceptor<Message<string, byte[]>>> _interceptors;

    public KafkaOutboxTransport(
        IProducer<string, byte[]> producer,
        IOptions<KafkaTransportOptions> options,
        ILogger<KafkaOutboxTransport> logger,
        IEnumerable<ITransportMessageInterceptor<Message<string, byte[]>>> interceptors)
    {
        _producer = producer;
        _logger = logger;
        _sendTimeoutMs = options.Value.SendTimeoutSeconds * 1000;
        _maxBatchSizeBytes = options.Value.MaxBatchSizeBytes;
        _interceptors = interceptors.ToList();
    }

    public async Task SendAsync(
        string topicName,
        string partitionKey,
        IReadOnlyList<OutboxMessage> messages,
        CancellationToken cancellationToken)
    {
        var subBatches = KafkaMessageHelper.SplitIntoSubBatches(partitionKey, messages, _maxBatchSizeBytes);

        if (subBatches.Count > 1)
        {
            _logger.LogDebug(
                "Splitting {Total} messages into {BatchCount} sub-batches for topic {Topic}",
                messages.Count, subBatches.Count, topicName);
        }

        var succeededSequences = new List<long>();

        foreach (var subBatch in subBatches)
        {
            try
            {
                await SendSubBatchAsync(topicName, partitionKey, subBatch, cancellationToken)
                    .ConfigureAwait(false);
                succeededSequences.AddRange(subBatch.Select(m => m.SequenceNumber));
            }
            catch (PartialSendException pex) when (succeededSequences.Count > 0 || pex.SucceededSequenceNumbers.Count > 0)
            {
                // Merge inner succeeded sequences with prior sub-batch successes
                succeededSequences.AddRange(pex.SucceededSequenceNumbers);

                var succeededSet = succeededSequences.ToHashSet();
                var failedSequences = messages
                    .Where(m => !succeededSet.Contains(m.SequenceNumber))
                    .Select(m => m.SequenceNumber)
                    .ToList();

                throw new PartialSendException(
                    succeededSequences, failedSequences,
                    $"Partial send failure: {succeededSequences.Count} messages sent, {failedSequences.Count} failed for topic '{topicName}'",
                    pex.InnerException ?? pex);
            }
            catch (Exception ex) when (succeededSequences.Count > 0)
            {
                // Non-partial failure after prior sub-batches succeeded
                var succeededSet = succeededSequences.ToHashSet();
                var failedSequences = messages
                    .Where(m => !succeededSet.Contains(m.SequenceNumber))
                    .Select(m => m.SequenceNumber)
                    .ToList();

                throw new PartialSendException(
                    succeededSequences, failedSequences,
                    $"Partial send failure: {succeededSequences.Count} messages sent, {failedSequences.Count} failed for topic '{topicName}'",
                    ex);
            }
        }
    }

    /// <inheritdoc />
    public bool IsTransient(Exception exception)
    {
        return exception switch
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
        // Rare on non-idempotent producers; classified transient to avoid
        // dead-lettering on transient network duplication.
        ErrorCode.OutOfOrderSequenceNumber => true,
        // Client-side transient errors (librdkafka local errors)
        ErrorCode.Local_Transport => true,
        ErrorCode.Local_AllBrokersDown => true,
        ErrorCode.Local_TimedOut => true,
        ErrorCode.Local_QueueFull => true,
        ErrorCode.Local_MsgTimedOut => true,
        _ => false
    };

    // Cognitive complexity is inherent to the produce-flush-error-handling flow.
    // Splitting further would obscure the delivery-report callback logic.
#pragma warning disable S3776
    private async Task SendSubBatchAsync(
        string topicName,
        string partitionKey,
        List<OutboxMessage> messages,
        CancellationToken cancellationToken)
    {
        var deliveryErrors = new List<Exception>();
        var succeededInBatch = new List<long>();
        var pending = messages.Count;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            var seqNum = msg.SequenceNumber;
            var kafkaMessage = new Message<string, byte[]>
            {
                Key = partitionKey,
                Value = msg.Payload,
                Headers = KafkaMessageHelper.ParseHeaders(msg.Headers, msg.EventType)
            };

            if (_interceptors.Count > 0)
            {
                TransportMessageContext<Message<string, byte[]>>? transportCtx = null;

                foreach (var interceptor in _interceptors)
                {
                    if (interceptor.AppliesTo(msg))
                    {
                        transportCtx ??= new TransportMessageContext<Message<string, byte[]>>(msg, kafkaMessage);
                        await interceptor.InterceptAsync(transportCtx, cancellationToken);
                    }
                }

                if (transportCtx is not null)
                {
                    kafkaMessage = transportCtx.Envelope;
                }
            }

            _producer.Produce(topicName, kafkaMessage, report =>
            {
                if (report.Error is { IsError: true })
                {
                    lock (deliveryErrors)
                    {
                        deliveryErrors.Add(new ProduceException<string, byte[]>(report.Error, report));
                    }
                }
                else
                {
                    lock (succeededInBatch)
                    {
                        succeededInBatch.Add(seqNum);
                    }
                }

                if (Interlocked.Decrement(ref pending) == 0)
                    tcs.TrySetResult();
            });
        }

        // Flush queued messages on a dedicated long-running thread to avoid
        // blocking ThreadPool threads. Kafka's Flush() is synchronous by design,
        // so running it on the ThreadPool can starve heartbeat/rebalance loops
        // under backpressure (see docs/known-limitations.md).
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(_sendTimeoutMs));

        var flushTask = Task.Factory.StartNew(() =>
        {
            while (!tcs.Task.IsCompleted)
            {
                cts.Token.ThrowIfCancellationRequested();
                _producer.Flush(TimeSpan.FromMilliseconds(100));
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        await flushTask.ConfigureAwait(false);
        await tcs.Task.ConfigureAwait(false); // propagate any exceptions

        if (deliveryErrors.Count > 0)
        {
            var succeededSet = succeededInBatch.ToHashSet();
            var failedSequences = messages
                .Where(m => !succeededSet.Contains(m.SequenceNumber))
                .Select(m => m.SequenceNumber)
                .ToList();

            if (succeededInBatch.Count > 0)
            {
                throw new PartialSendException(
                    succeededInBatch, failedSequences,
                    $"Partial delivery: {succeededInBatch.Count} succeeded, {deliveryErrors.Count} failed for topic '{topicName}'",
                    new AggregateException(deliveryErrors));
            }

            throw new AggregateException(
                $"Failed to deliver {deliveryErrors.Count}/{messages.Count} messages to topic '{topicName}'",
                deliveryErrors);
        }
    }
#pragma warning restore S3776

    public ValueTask DisposeAsync()
    {
        // Don't dispose the producer — it's owned by the DI container.
        // Just flush any remaining messages with a short timeout.
        try { _producer.Flush(TimeSpan.FromSeconds(5)); }
        catch
        {
            /* best effort during shutdown */
        }

        return ValueTask.CompletedTask;
    }
}
