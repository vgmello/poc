using System.Data.Common;
using Npgsql;

namespace Outbox.IntegrationTests.Helpers;

/// <summary>
/// Connection factory that can be toggled to simulate DB outages.
/// </summary>
public sealed class ToggleableConnectionFactory
{
    private readonly string _connectionString;
    private volatile bool _failing;

    public ToggleableConnectionFactory(string connectionString) =>
        _connectionString = connectionString;

    public void SetFailing(bool failing) => _failing = failing;

    public Task<DbConnection> CreateConnectionAsync(IServiceProvider sp, CancellationToken ct)
    {
        if (_failing)
            throw new NpgsqlException("Simulated DB connection failure");

        return Task.FromResult<DbConnection>(new NpgsqlConnection(_connectionString));
    }
}
