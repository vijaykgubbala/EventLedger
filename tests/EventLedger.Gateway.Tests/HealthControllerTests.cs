using System.Net;
using EventLedger.Gateway.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EventLedger.Gateway.Tests;

public class HealthControllerTests
{
    [Fact]
    public async Task GetHealth_DatabaseReachable_Returns200WithOkStatus()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("{\"status\":\"ok\",\"database\":\"ok\"}", body);
    }

    [Fact]
    public async Task GetHealth_DatabaseUnreachable_Returns200WithDegradedStatus()
    {
        // Startup itself runs EnsureGatewayDatabaseCreated(), which throws if the DB is
        // unreachable at boot — so the directory must exist (and EnsureCreated() must succeed)
        // before the factory starts. Connectivity is broken only afterward, isolating the
        // failure to the /health request itself rather than crashing the whole host.
        var tempDir = Path.Combine(Path.GetTempPath(), $"gateway-health-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var connectionString = $"Data Source={Path.Combine(tempDir, "test.db")}";

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<GatewayDbContext>>();
                services.AddDbContext<GatewayDbContext>(opt => opt.UseSqlite(connectionString));
            }));

        // Forces host startup (and EnsureGatewayDatabaseCreated()) to run while the directory
        // still exists.
        _ = factory.Services;

        SqliteConnection.ClearAllPools();
        Directory.Delete(tempDir, recursive: true);

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("{\"status\":\"degraded\",\"database\":\"unreachable\"}", body);
    }
}
