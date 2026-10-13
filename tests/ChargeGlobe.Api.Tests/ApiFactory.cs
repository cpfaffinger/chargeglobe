using ChargeGlobe.Application.Health;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChargeGlobe.Api.Tests;

/// <summary>
/// Hosts the API in-process without a database: the probe is stubbed and the startup
/// migration is removed. Everything that needs Postgres lives in ChargeGlobe.IntegrationTests.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public bool DatabaseReachable { get; set; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", "Host=unused.invalid;Database=unused");
        builder.UseSetting("JWT_SIGNING_KEY", "unit-test-signing-key-0123456789abcdef");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDatabaseProbe>();
            services.Remove(services.Single(service => service.ImplementationType == typeof(DatabaseInitializationService)));
            services.AddSingleton<IDatabaseProbe>(new StubDatabaseProbe(this));
        });
    }

    private sealed class StubDatabaseProbe(ApiFactory factory) : IDatabaseProbe
    {
        public Task<bool> CanConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(factory.DatabaseReachable);
    }
}
