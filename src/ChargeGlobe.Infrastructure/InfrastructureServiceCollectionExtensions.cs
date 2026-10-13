using System.Data.Common;
using ChargeGlobe.Application.Health;
using ChargeGlobe.Infrastructure.Health;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ChargeGlobe.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton<DbDataSource>(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<IDatabaseProbe, DatabaseProbe>();
        return services;
    }
}
