// Copyright (c) OrgName. All rights reserved.

using System.ComponentModel.DataAnnotations;
using Outbox.Core.Options;
using Xunit;

namespace Outbox.Core.Tests;

public class OutboxPublisherOptionsTests
{
    private static OutboxPublisherOptions ValidOptions() => new()
    {
        BatchSize = 100,
        MaxPublishAttempts = 10,
        RetryBackoffBaseMs = 100,
        RetryBackoffMaxMs = 2000,
        CircuitBreakerFailureThreshold = 3,
        CircuitBreakerOpenDurationSeconds = 30,
        PartitionGracePeriodSeconds = 60,
        HeartbeatIntervalMs = 10_000,
        HeartbeatTimeoutSeconds = 30,
        MinPollIntervalMs = 100,
        MaxPollIntervalMs = 5000,
        RebalanceIntervalMs = 30_000,
        OrphanSweepIntervalMs = 60_000,
        PublishThreadCount = 4
    };

    private static List<ValidationResult> Validate(OutboxPublisherOptions options)
    {
        var context = new ValidationContext(options);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, context, results, validateAllProperties: true);

        return results;
    }

    [Fact]
    public void ValidOptions_ReturnsSuccess()
    {
        Assert.Empty(Validate(ValidOptions()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BatchSize_Invalid_ReturnsFailure(int value)
    {
        var options = ValidOptions();
        options.BatchSize = value;
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("BatchSize"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxPublishAttempts_Invalid_ReturnsFailure(int value)
    {
        var options = ValidOptions();
        options.MaxPublishAttempts = value;
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("MaxPublishAttempts"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MinPollIntervalMs_Invalid_ReturnsFailure(int value)
    {
        var options = ValidOptions();
        options.MinPollIntervalMs = value;
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("MinPollIntervalMs"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxPollIntervalMs_Invalid_ReturnsFailure(int value)
    {
        var options = ValidOptions();
        options.MaxPollIntervalMs = value;
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("MaxPollIntervalMs"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void HeartbeatIntervalMs_Invalid_ReturnsFailure(int value)
    {
        var options = ValidOptions();
        options.HeartbeatIntervalMs = value;
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("HeartbeatIntervalMs"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void HeartbeatTimeoutSeconds_Invalid_ReturnsFailure(int value)
    {
        var options = ValidOptions();
        options.HeartbeatTimeoutSeconds = value;
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("HeartbeatTimeoutSeconds"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PartitionGracePeriodSeconds_Invalid_ReturnsFailure(int value)
    {
        var options = ValidOptions();
        options.PartitionGracePeriodSeconds = value;
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("PartitionGracePeriodSeconds"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RebalanceIntervalMs_Invalid_ReturnsFailure(int value)
    {
        var options = ValidOptions();
        options.RebalanceIntervalMs = value;
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("RebalanceIntervalMs"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void OrphanSweepIntervalMs_Invalid_ReturnsFailure(int value)
    {
        var options = ValidOptions();
        options.OrphanSweepIntervalMs = value;
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("OrphanSweepIntervalMs"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CircuitBreakerFailureThreshold_Invalid_ReturnsFailure(int value)
    {
        var options = ValidOptions();
        options.CircuitBreakerFailureThreshold = value;
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("CircuitBreakerFailureThreshold"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CircuitBreakerOpenDurationSeconds_Invalid_ReturnsFailure(int value)
    {
        var options = ValidOptions();
        options.CircuitBreakerOpenDurationSeconds = value;
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("CircuitBreakerOpenDurationSeconds"));
    }

    [Fact]
    public void PublishThreadCount_Zero_ReturnsFailure()
    {
        var options = ValidOptions();
        options.PublishThreadCount = 0;
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("PublishThreadCount"));
    }

    [Fact]
    public void PublishThreadCount_One_ReturnsSuccess()
    {
        var options = ValidOptions();
        options.PublishThreadCount = 1;
        var results = Validate(options);
        Assert.DoesNotContain(results, r => r.MemberNames.Contains("PublishThreadCount"));
    }

    [Fact]
    public void PublishThreadCount_Four_ReturnsSuccess()
    {
        var options = ValidOptions();
        options.PublishThreadCount = 4;
        var results = Validate(options);
        Assert.DoesNotContain(results, r => r.MemberNames.Contains("PublishThreadCount"));
    }

    [Fact]
    public void MaxPollIntervalMs_LessThanMinPollIntervalMs_ReturnsFailure()
    {
        var options = ValidOptions();
        options.MinPollIntervalMs = 5000;
        options.MaxPollIntervalMs = 100;
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.ErrorMessage!.Contains("MaxPollIntervalMs must be >= MinPollIntervalMs"));
    }

    [Fact]
    public void CrossField_HeartbeatTimeoutTooShort_ReturnsFailure()
    {
        var options = ValidOptions();
        options.HeartbeatIntervalMs = 5000;
        options.HeartbeatTimeoutSeconds = 10;
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r =>
            r.MemberNames.Contains("HeartbeatTimeoutSeconds") &&
            r.MemberNames.Contains("HeartbeatIntervalMs"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RetryBackoffBaseMs_Invalid_ReturnsFailure(int value)
    {
        var options = ValidOptions();
        options.RetryBackoffBaseMs = value;
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("RetryBackoffBaseMs"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RetryBackoffMaxMs_Invalid_ReturnsFailure(int value)
    {
        var options = ValidOptions();
        options.RetryBackoffMaxMs = value;
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("RetryBackoffMaxMs"));
    }

    [Fact]
    public void CrossField_RetryBackoffMaxLessThanBase_ReturnsFailure()
    {
        var options = ValidOptions();
        options.RetryBackoffBaseMs = 1000;
        options.RetryBackoffMaxMs = 500;
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r =>
            r.MemberNames.Contains("RetryBackoffMaxMs") &&
            r.MemberNames.Contains("RetryBackoffBaseMs"));
    }
}
