// Copyright (c) OrgName. All rights reserved.

using System.Data;
using System.Text.Json;
using Dapper;

namespace Outbox.SqlServer;

internal sealed class JsonDictionaryTypeHandler : SqlMapper.TypeHandler<Dictionary<string, string>?>
{
    public override void SetValue(IDbDataParameter parameter, Dictionary<string, string>? value)
    {
        parameter.Value = value is null ? DBNull.Value : JsonSerializer.Serialize(value);
    }

    public override Dictionary<string, string>? Parse(object value)
    {
        return value is string s
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(s)
            : null;
    }
}

/// <summary>
///     SQL Server stores UTC timestamps as <c>datetime</c>/<c>datetime2</c> without offset info.
///     Dapper reads them as <see cref="DateTime" /> (Kind = Unspecified). This handler converts
///     to <see cref="DateTimeOffset" /> assuming UTC.
/// </summary>
internal sealed class UtcDateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
    {
        parameter.Value = value.UtcDateTime;
    }

    public override DateTimeOffset Parse(object value)
    {
        return value switch
        {
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            DateTimeOffset dto => dto,
            _ => throw new InvalidCastException($"Cannot convert {value.GetType()} to DateTimeOffset")
        };
    }
}

internal static class DapperConfiguration
{
    private static int _initialized;

    public static void EnsureInitialized()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            return;

        SqlMapper.AddTypeHandler(new JsonDictionaryTypeHandler());
        SqlMapper.AddTypeHandler(new UtcDateTimeOffsetTypeHandler());
    }
}
