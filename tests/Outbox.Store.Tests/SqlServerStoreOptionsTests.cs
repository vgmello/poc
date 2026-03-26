// Copyright (c) OrgName. All rights reserved.

using System.ComponentModel.DataAnnotations;
using Outbox.SqlServer;
using Xunit;

namespace Outbox.Store.Tests;

public class SqlServerStoreOptionsTests
{
    private static List<ValidationResult> Validate(SqlServerStoreOptions options)
    {
        var context = new ValidationContext(options);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, context, results, validateAllProperties: true);

        return results;
    }

    [Fact]
    public void DefaultSchemaName_IsDbo()
    {
        var options = new SqlServerStoreOptions();
        Assert.Equal("dbo", options.SchemaName);
    }

    [Fact]
    public void DefaultCommandTimeoutSeconds_Is30()
    {
        var options = new SqlServerStoreOptions();
        Assert.Equal(30, options.CommandTimeoutSeconds);
    }

    [Fact]
    public void DefaultTransientRetryMaxAttempts_Is6()
    {
        var options = new SqlServerStoreOptions();
        Assert.Equal(6, options.TransientRetryMaxAttempts);
    }

    [Fact]
    public void DefaultTransientRetryBackoffMs_Is1000()
    {
        var options = new SqlServerStoreOptions();
        Assert.Equal(1000, options.TransientRetryBackoffMs);
    }

    [Theory]
    [InlineData("dbo")]
    [InlineData("outbox")]
    [InlineData("my_schema")]
    [InlineData("_private")]
    [InlineData("Schema123")]
    public void ValidSchemaName_SetsSuccessfully(string schemaName)
    {
        var options = new SqlServerStoreOptions { SchemaName = schemaName };
        var results = Validate(options);
        Assert.Empty(results);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123abc")]
    [InlineData("my-schema")]
    [InlineData("my space")]
    [InlineData("schema;DROP TABLE")]
    [InlineData("public.outbox")]
    public void InvalidSchemaName_FailsValidation(string schemaName)
    {
        var options = new SqlServerStoreOptions { SchemaName = schemaName };
        var results = Validate(options);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("SchemaName"));
    }

    [Fact]
    public void TablePrefix_DefaultsToEmpty()
    {
        var opts = new SqlServerStoreOptions();
        Assert.Equal("", opts.TablePrefix);
    }

    [Theory]
    [InlineData("Orders")]
    [InlineData("my_prefix_")]
    [InlineData("Foo")]
    [InlineData("_prefix")]
    public void TablePrefix_ValidPrefixes_Accepted(string prefix)
    {
        var opts = new SqlServerStoreOptions { TablePrefix = prefix };
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
        var opts = new SqlServerStoreOptions { TablePrefix = prefix };
        var results = Validate(opts);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("TablePrefix"));
    }

    [Fact]
    public void TablePrefix_EmptyString_Allowed()
    {
        var opts = new SqlServerStoreOptions { TablePrefix = "" };
        var results = Validate(opts);
        Assert.Empty(results);
    }

    [Fact]
    public void TablePrefix_Null_FailsValidation()
    {
        var opts = new SqlServerStoreOptions { TablePrefix = null! };
        var results = Validate(opts);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.MemberNames.Contains("TablePrefix"));
    }

    [Fact]
    public void GroupName_DefaultsToNull()
    {
        var opts = new SqlServerStoreOptions();
        Assert.Null(opts.GroupName);
    }

    [Fact]
    public void SharedSchemaName_DefaultsToNull()
    {
        var opts = new SqlServerStoreOptions();
        Assert.Null(opts.SharedSchemaName);
    }

    [Fact]
    public void OutboxTableName_WhenNoPrefix_DefaultsToOutbox()
    {
        var opts = new SqlServerStoreOptions();
        Assert.Equal("Outbox", opts.GetOutboxTableName());
    }

    [Fact]
    public void OutboxTableName_WhenPrefix_DerivesPrefixedName()
    {
        var opts = new SqlServerStoreOptions { TablePrefix = "Orders_" };
        Assert.Equal("Orders_Outbox", opts.GetOutboxTableName());
    }

    [Fact]
    public void GetSharedSchemaName_WhenNull_FallsBackToSchemaName()
    {
        var opts = new SqlServerStoreOptions { SchemaName = "custom" };
        Assert.Equal("custom", opts.GetSharedSchemaName());
    }

    [Fact]
    public void GetSharedSchemaName_WhenSet_ReturnsExplicitValue()
    {
        var opts = new SqlServerStoreOptions { SchemaName = "custom", SharedSchemaName = "shared" };
        Assert.Equal("shared", opts.GetSharedSchemaName());
    }
}
