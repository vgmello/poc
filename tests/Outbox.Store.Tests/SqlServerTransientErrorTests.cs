// Copyright (c) OrgName. All rights reserved.

using System.Reflection;
using Microsoft.Data.SqlClient;
using Outbox.SqlServer;
using Xunit;

namespace Outbox.Store.Tests;

public class SqlServerTransientErrorTests
{
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

    [Theory]
    [InlineData(1205)] // deadlock victim
    [InlineData(-2)] // timeout
    [InlineData(40613)] // Azure SQL database not available
    [InlineData(40197)] // Azure SQL service error
    [InlineData(40501)] // Azure SQL service busy
    [InlineData(49918)]
    [InlineData(49919)]
    [InlineData(49920)]
    public void TransientErrorNumbers_ReturnTrue(int errorNumber)
    {
        var ex = CreateSqlException(errorNumber);
        Assert.True(SqlServerDbHelper.IsTransientSqlError(ex));
    }

    [Theory]
    [InlineData(547)] // FK violation
    [InlineData(2627)] // unique violation
    [InlineData(8152)] // string truncation
    [InlineData(0)] // generic
    public void NonTransientErrorNumbers_ReturnFalse(int errorNumber)
    {
        var ex = CreateSqlException(errorNumber);
        Assert.False(SqlServerDbHelper.IsTransientSqlError(ex));
    }
}
