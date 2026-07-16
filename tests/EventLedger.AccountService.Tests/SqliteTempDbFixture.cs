using EventLedger.AccountService.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventLedger.AccountService.Tests;

public sealed class SqliteTempDbFixture : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"account-test-{Guid.NewGuid():N}.db");

    public string ConnectionString => $"Data Source={_dbPath}";

    public AccountDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AccountDbContext>()
            .UseSqlite(ConnectionString)
            .Options;
        return new AccountDbContext(options);
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
