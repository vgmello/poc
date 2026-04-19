// Copyright (c) OrgName. All rights reserved.

using System.Data.Common;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using NSubstitute;
using Outbox.SqlServer;
using Xunit;

namespace Outbox.Store.Tests;

public class SqlServerRetryHelperTests
{
    private static SqlServerStoreOptions FastOptions(int maxAttempts = 3) => new()
    {
        TransientRetryMaxAttempts = maxAttempts,
        TransientRetryBackoffMs = 1
    };

    private static IOptionsMonitor<SqlServerStoreOptions> Monitor(SqlServerStoreOptions options)
    {
        var monitor = Substitute.For<IOptionsMonitor<SqlServerStoreOptions>>();
        monitor.Get(Arg.Any<string>()).Returns(options);
        monitor.CurrentValue.Returns(options);
        return monitor;
    }

    private static SqlException CreateSqlException(int errorNumber)
    {
        var errorCollection = (SqlErrorCollection)Activator.CreateInstance(
            typeof(SqlErrorCollection),
            BindingFlags.NonPublic | BindingFlags.Instance,
            null, null, null)!;

        var error = (SqlError)Activator.CreateInstance(
            typeof(SqlError),
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new object?[] { errorNumber, (byte)0, (byte)0, "server", "error", "proc", 0, (uint)0, null },
            null)!;

        typeof(SqlErrorCollection)
            .GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(errorCollection, new object[] { error });

        return (SqlException)Activator.CreateInstance(
            typeof(SqlException),
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new object?[] { "test", errorCollection, null, Guid.Empty },
            null)!;
    }

    [Fact]
    public async Task NonTransientOnFirstAttempt_ThrowsWithoutRetry()
    {
        // SqlConnection is sealed and cannot be mocked, so we test retry behavior
        // via factory-level exceptions (throwing before a connection is returned).
        // Non-transient error 2627 (unique violation) should not be retried.
        var factoryCallCount = 0;
        var serviceProvider = Substitute.For<IServiceProvider>();
        var nonTransientEx = CreateSqlException(2627);

        Func<IServiceProvider, CancellationToken, Task<DbConnection>> factory = (_, _) =>
        {
            factoryCallCount++;

            throw nonTransientEx;
        };

        var options = FastOptions(maxAttempts: 3);
        options.ConnectionFactory = factory;
        var helper = new SqlServerDbHelper(serviceProvider, Monitor(options), Options.DefaultName);

        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            helper.ExecuteWithRetryAsync((_, _) => Task.CompletedTask, CancellationToken.None));

        Assert.Equal(1, factoryCallCount);
        Assert.Equal(2627, ex.Number);
    }

    [Fact]
    public async Task TransientMaxAttemptsExceeded_ThrowsAfterMaxAttempts()
    {
        var factoryCallCount = 0;
        var serviceProvider = Substitute.For<IServiceProvider>();

        Func<IServiceProvider, CancellationToken, Task<DbConnection>> factory = (_, _) =>
        {
            factoryCallCount++;

            throw CreateSqlException(1205); // deadlock — transient
        };

        var options = FastOptions(maxAttempts: 3);
        options.ConnectionFactory = factory;
        var helper = new SqlServerDbHelper(serviceProvider, Monitor(options), Options.DefaultName);

        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            helper.ExecuteWithRetryAsync((_, _) => Task.CompletedTask, CancellationToken.None));

        // maxAttempts=3: attempts 1 and 2 are retried (attempt < maxAttempts), attempt 3 throws
        Assert.Equal(3, factoryCallCount);
        Assert.Equal(1205, ex.Number);
    }

    [Fact]
    public async Task TransientMaxAttempts1_ThrowsImmediately()
    {
        // With maxAttempts=1, even transient errors should not be retried (no attempt < 1 retry)
        var factoryCallCount = 0;
        var serviceProvider = Substitute.For<IServiceProvider>();

        Func<IServiceProvider, CancellationToken, Task<DbConnection>> factory = (_, _) =>
        {
            factoryCallCount++;

            throw CreateSqlException(40613); // Azure SQL unavailable — transient
        };

        var options = FastOptions(maxAttempts: 1);
        options.ConnectionFactory = factory;
        var helper = new SqlServerDbHelper(serviceProvider, Monitor(options), Options.DefaultName);

        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            helper.ExecuteWithRetryAsync((_, _) => Task.CompletedTask, CancellationToken.None));

        Assert.Equal(1, factoryCallCount);
        Assert.Equal(40613, ex.Number);
    }
}
