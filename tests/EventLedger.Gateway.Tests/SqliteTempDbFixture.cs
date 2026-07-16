using EventLedger.Gateway.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventLedger.Gateway.Tests;

public sealed class SqliteTempDbFixture : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"gateway-test-{Guid.NewGuid():N}.db");

    public string ConnectionString => $"Data Source={_dbPath}";

    public GatewayDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite(ConnectionString)
            .Options;
        return new GatewayDbContext(options);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
