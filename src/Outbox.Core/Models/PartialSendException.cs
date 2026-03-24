// Copyright (c) OrgName. All rights reserved.

namespace Outbox.Core.Models;

/// <summary>
///     Thrown by a transport when some messages in a batch were successfully sent
///     but others failed. Allows the publisher to delete succeeded messages and
///     only retry the failed ones.
/// </summary>
public sealed class PartialSendException : Exception
{
    public IReadOnlyList<long> SucceededSequenceNumbers { get; }
    public IReadOnlyList<long> FailedSequenceNumbers { get; }

    public PartialSendException(
        IReadOnlyList<long> succeededSequenceNumbers,
        IReadOnlyList<long> failedSequenceNumbers,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        SucceededSequenceNumbers = succeededSequenceNumbers;
        FailedSequenceNumbers = failedSequenceNumbers;
    }
}
