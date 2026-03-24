// Copyright (c) OrgName. All rights reserved.

using System.Net.Sockets;
using System.Reflection;
using Npgsql;
using Outbox.PostgreSQL;
using Xunit;

namespace Outbox.Store.Tests;

public class PostgreSqlTransientErrorTests
{
    private static NpgsqlException CreateNpgsqlExceptionWithInner(Exception inner)
    {
        var ex = (NpgsqlException)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(NpgsqlException));
        typeof(Exception)
            .GetField("_innerException", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(ex, inner);

        return ex;
    }

    [Theory]
    [InlineData("40001")]
    [InlineData("40P01")]
    [InlineData("57P01")]
    [InlineData("57014")]
    [InlineData("57P03")]
    public void ExactTransientSqlStates_ReturnTrue(string sqlState)
    {
        var ex = new PostgresException("test error", "ERROR", "ERROR", sqlState);
        Assert.True(PostgreSqlDbHelper.IsTransientNpgsqlError(ex));
    }

    [Theory]
    [InlineData("08000")]
    [InlineData("08003")]
    [InlineData("08006")]
    public void SqlStateStartingWith08_ReturnTrue(string sqlState)
    {
        var ex = new PostgresException("connection error", "ERROR", "ERROR", sqlState);
        Assert.True(PostgreSqlDbHelper.IsTransientNpgsqlError(ex),
            $"SQL state {sqlState} (class 08) should be transient");
    }

    [Theory]
    [InlineData("53000")]
    [InlineData("53100")]
    [InlineData("53200")]
    public void SqlStateStartingWith53_ReturnTrue(string sqlState)
    {
        var ex = new PostgresException("resource limit", "ERROR", "ERROR", sqlState);
        Assert.True(PostgreSqlDbHelper.IsTransientNpgsqlError(ex),
            $"SQL state {sqlState} (class 53) should be transient");
    }

    [Fact]
    public void InnerException_IOException_ReturnTrue()
    {
        var ex = CreateNpgsqlExceptionWithInner(new IOException("connection reset"));
        Assert.True(PostgreSqlDbHelper.IsTransientNpgsqlError(ex));
    }

    [Fact]
    public void InnerException_SocketException_ReturnTrue()
    {
        var ex = CreateNpgsqlExceptionWithInner(new SocketException());
        Assert.True(PostgreSqlDbHelper.IsTransientNpgsqlError(ex));
    }

    [Fact]
    public void NonTransientSqlState_UniqueViolation_ReturnFalse()
    {
        var ex = new PostgresException("test error", "ERROR", "ERROR", "23505");
        Assert.False(PostgreSqlDbHelper.IsTransientNpgsqlError(ex));
    }

    [Fact]
    public void NoSqlStateAndNoMatchingInner_ReturnFalse()
    {
        var ex = CreateNpgsqlExceptionWithInner(new InvalidOperationException("not transient"));
        Assert.False(PostgreSqlDbHelper.IsTransientNpgsqlError(ex));
    }
}
