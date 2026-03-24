// Copyright (c) OrgName. All rights reserved.

using System.Data;
using System.Text.Json;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace Outbox.PostgreSQL;

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
///     Dapper custom parameter for PostgreSQL <c>bigint[]</c> arrays (used with <c>= ANY(@ids)</c>).
/// </summary>
internal sealed class BigintArrayParam : SqlMapper.ICustomQueryParameter
{
    private readonly long[] _values;

    public BigintArrayParam(IReadOnlyList<long> values) => _values = values.ToArray();

    public void AddParameter(IDbCommand command, string name)
    {
#pragma warning disable S3265 // NpgsqlDbType.Array requires bitwise OR by design
        var param = new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Bigint)
#pragma warning restore S3265
        {
            Value = _values
        };
        command.Parameters.Add(param);
    }
}

internal static class DapperConfiguration
{
    private static volatile bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized) return;

        SqlMapper.AddTypeHandler(new JsonDictionaryTypeHandler());
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        _initialized = true;
    }
}
