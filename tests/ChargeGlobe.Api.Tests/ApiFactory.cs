using ChargeGlobe.Application.Health;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChargeGlobe.Api.Tests;

/// <summary>Hosts the API in-process with a stubbed database probe.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public bool DatabaseReachable { get; set; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", "Host=unused.invalid;Database=unused");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDatabaseProbe>();
            services.AddSingleton<IDatabaseProbe>(new StubDatabaseProbe(this));
        });
    }

    private sealed class StubDatabaseProbe(ApiFactory factory) : IDatabaseProbe
    {
        public Task<bool> CanConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(factory.DatabaseReachable);
    }
}
