// Copyright (c) OrgName. All rights reserved.

using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using Npgsql;
using Outbox.PostgreSQL;
using Xunit;

namespace Outbox.Store.Tests;

public class PostgreSqlStoreOptionsTests
{
    private static List<ValidationResult> Validate(PostgreSqlStoreOptions options)
    {
        var context = new ValidationContext(options);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, context, results, validateAllProperties: true);

        return results;
    }

    [Fact]
    public void DefaultSchemaName_IsPublic()
    {
        var options = new PostgreSqlStoreOptions();
        Assert.Equal("public", options.SchemaName);
    }

    [Fact]
    public void DefaultCommandTimeoutSeconds_Is30()
    {
        var options = new PostgreSqlStoreOptions();
        Assert.Equal(30, options.CommandTimeoutSeconds);
    }

    [Fact]
    public void DefaultTransientRetryMaxAttempts_Is6()
    {
        var options = new PostgreSqlStoreOptions();
        Assert.Equal(6, options.TransientRetryMaxAttempts);
    }

    [Fact]
    public void DefaultTransientRetryBackoffMs_Is1000()
    {
        var options = new PostgreSqlStoreOptions();
        Assert.Equal(1000, options.TransientRetryBackoffMs);
    }

    [Theory]
    [InlineData("public")]
    [InlineData("outbox")]
    [InlineData("my_schema")]
    [InlineData("_private")]
    [InlineData("Schema123")]
    public void ValidSchemaName_SetsSuccessfully(string schemaName)
    {
        var options = new PostgreSqlStoreOptions { SchemaName = schemaName, ConnectionString = "Host=localhost" };
        var results = Validate(options);
        Assert.Empty(results);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123abc")]
    [InlineData("my-schema")]
    [InlineData("my schema")]
    [InlineData("schema;DROP TABLE")]
    [InlineData("public.outbox")]
    public void InvalidSchemaName_FailsValidation(string schemaName)
    {
        var options = new PostgreSqlStoreOptions { SchemaName = schemaName };
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("SchemaName"));
    }

    [Fact]
    public void TablePrefix_DefaultsToEmpty()
    {
        var opts = new PostgreSqlStoreOptions();
        Assert.Equal("", opts.TablePrefix);
    }

    [Theory]
    [InlineData("orders_")]
    [InlineData("my_prefix_")]
    [InlineData("Foo")]
    [InlineData("_prefix")]
    public void TablePrefix_ValidPrefixes_Accepted(string prefix)
    {
        var opts = new PostgreSqlStoreOptions { TablePrefix = prefix, ConnectionString = "Host=localhost" };
        var results = Validate(opts);
        Assert.Empty(results);
    }

    [Theory]
    [InlineData("123abc")]
    [InlineData("my-prefix")]
    [InlineData("prefix;DROP")]
    [InlineData("has space")]
    [InlineData("   ")]
    public void TablePrefix_InvalidPrefixes_FailsValidation(string prefix)
    {
        var opts = new PostgreSqlStoreOptions { TablePrefix = prefix };
        var results = Validate(opts);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("TablePrefix"));
    }

    [Fact]
    public void TablePrefix_EmptyString_Allowed()
    {
        var opts = new PostgreSqlStoreOptions { TablePrefix = "", ConnectionString = "Host=localhost" };
        var results = Validate(opts);
        Assert.Empty(results);
    }

    [Fact]
    public void TablePrefix_Null_FailsValidation()
    {
        var opts = new PostgreSqlStoreOptions { TablePrefix = null! };
        var results = Validate(opts);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("TablePrefix"));
    }

    [Fact]
    public void GroupName_DefaultsToNull()
    {
        var opts = new PostgreSqlStoreOptions();
        Assert.Null(opts.GroupName);
    }

    [Fact]
    public void SharedSchemaName_DefaultsToNull()
    {
        var opts = new PostgreSqlStoreOptions();
        Assert.Null(opts.SharedSchemaName);
    }

    [Fact]
    public void OutboxTableName_WhenNoPrefix_DefaultsToOutbox()
    {
        var opts = new PostgreSqlStoreOptions();
        Assert.Equal("outbox", opts.GetOutboxTableName());
    }

    [Fact]
    public void OutboxTableName_WhenPrefix_DerivesPrefixedName()
    {
        var opts = new PostgreSqlStoreOptions { TablePrefix = "orders_" };
        Assert.Equal("orders_outbox", opts.GetOutboxTableName());
    }

    [Fact]
    public void GetSharedSchemaName_WhenNull_FallsBackToSchemaName()
    {
        var opts = new PostgreSqlStoreOptions { SchemaName = "custom" };
        Assert.Equal("custom", opts.GetSharedSchemaName());
    }

    [Fact]
    public void GetSharedSchemaName_WhenSet_ReturnsExplicitValue()
    {
        var opts = new PostgreSqlStoreOptions { SchemaName = "custom", SharedSchemaName = "shared" };
        Assert.Equal("shared", opts.GetSharedSchemaName());
    }

    [Fact]
    public void ConnectionString_set_without_factory_is_valid()
    {
        var opts = new PostgreSqlStoreOptions { ConnectionString = "Host=localhost;Database=test" };
        var results = Validate(opts);
        Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(PostgreSqlStoreOptions.ConnectionString)));
    }

    [Fact]
    public void ConnectionFactory_set_without_connectionString_is_valid()
    {
        var opts = new PostgreSqlStoreOptions
        {
            ConnectionFactory = (_, _) => Task.FromResult<DbConnection>(new NpgsqlConnection())
        };
        var results = Validate(opts);
        Assert.Empty(results.Where(r =>
            r.MemberNames.Contains(nameof(PostgreSqlStoreOptions.ConnectionString)) ||
            r.MemberNames.Contains(nameof(PostgreSqlStoreOptions.ConnectionFactory))));
    }

    [Fact]
    public void Both_connectionString_and_factory_set_is_valid()
    {
        var opts = new PostgreSqlStoreOptions
        {
            ConnectionString = "Host=localhost;Database=test",
            ConnectionFactory = (_, _) => Task.FromResult<DbConnection>(new NpgsqlConnection())
        };
        var results = Validate(opts);
        Assert.Empty(results.Where(r =>
            r.MemberNames.Contains(nameof(PostgreSqlStoreOptions.ConnectionString)) ||
            r.MemberNames.Contains(nameof(PostgreSqlStoreOptions.ConnectionFactory))));
    }

    [Fact]
    public void Neither_connectionString_nor_factory_is_invalid()
    {
        var opts = new PostgreSqlStoreOptions();
        var results = Validate(opts);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(PostgreSqlStoreOptions.ConnectionString)));
    }

    [Fact]
    public void Empty_connectionString_without_factory_is_invalid()
    {
        var opts = new PostgreSqlStoreOptions { ConnectionString = "" };
        var results = Validate(opts);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(PostgreSqlStoreOptions.ConnectionString)));
    }
}
