// Copyright (c) OrgName. All rights reserved.

using System.Data.Common;
using Npgsql;
using NSubstitute;
using Outbox.PostgreSQL;
using Xunit;

namespace Outbox.Store.Tests;

public class PostgreSqlRetryHelperTests
{
    private static PostgreSqlStoreOptions FastOptions(int maxAttempts = 3) => new()
    {
        TransientRetryMaxAttempts = maxAttempts,
        TransientRetryBackoffMs = 1
    };

    [Fact]
    public async Task SuccessOnFirstAttempt_ActionCalledOnce()
    {
        var actionCallCount = 0;
        var factoryCallCount = 0;

        var serviceProvider = Substitute.For<IServiceProvider>();
        var connection = Substitute.For<DbConnection>();
        connection.State.Returns(System.Data.ConnectionState.Open);

        Func<IServiceProvider, CancellationToken, Task<DbConnection>> factory = (_, _) =>
        {
            factoryCallCount++;

            return Task.FromResult(connection);
        };

        var helper = new PostgreSqlDbHelper(factory, serviceProvider, FastOptions());

        await helper.ExecuteWithRetryAsync(
            (conn, _) =>
            {
                actionCallCount++;

                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, factoryCallCount);
        Assert.Equal(1, actionCallCount);
    }

    [Fact]
    public async Task TransientFailureThenSuccess_Retries()
    {
        var factoryCallCount = 0;
        var actionCallCount = 0;

        var serviceProvider = Substitute.For<IServiceProvider>();
        var goodConnection = Substitute.For<DbConnection>();
        goodConnection.State.Returns(System.Data.ConnectionState.Open);

        Func<IServiceProvider, CancellationToken, Task<DbConnection>> factory = (_, _) =>
        {
            factoryCallCount++;

            if (factoryCallCount == 1)
            {
                // Throw a transient error on the first attempt (40001 = serialization failure)
                throw new PostgresException("transient error", "ERROR", "ERROR", "40001");
            }

            return Task.FromResult(goodConnection);
        };

        var helper = new PostgreSqlDbHelper(factory, serviceProvider, FastOptions(maxAttempts: 3));

        await helper.ExecuteWithRetryAsync(
            (conn, _) =>
            {
                actionCallCount++;

                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(2, factoryCallCount);
        Assert.Equal(1, actionCallCount);
    }

    [Fact]
    public async Task MaxAttemptsExceeded_ThrowsLastException()
    {
        var factoryCallCount = 0;

        var serviceProvider = Substitute.For<IServiceProvider>();

        Func<IServiceProvider, CancellationToken, Task<DbConnection>> factory = (_, _) =>
        {
            factoryCallCount++;

            throw new PostgresException("persistent error", "ERROR", "ERROR", "40001");
        };

        var helper = new PostgreSqlDbHelper(factory, serviceProvider, FastOptions(maxAttempts: 3));

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            helper.ExecuteWithRetryAsync((_, _) => Task.CompletedTask, CancellationToken.None));

        // maxAttempts=3: attempts 1 and 2 are retried (attempt < maxAttempts), attempt 3 throws
        Assert.Equal(3, factoryCallCount);
        Assert.Equal("persistent error", ex.MessageText);
    }
}
