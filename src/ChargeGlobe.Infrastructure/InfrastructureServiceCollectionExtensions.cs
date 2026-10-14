using System.Data.Common;
using ChargeGlobe.Application.Evses;
using ChargeGlobe.Application.Favorites;
using ChargeGlobe.Application.Health;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Application.Mail;
using ChargeGlobe.Application.Preferences;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Application.Sources;
using ChargeGlobe.Application.Tariffs;
using ChargeGlobe.Infrastructure.Health;
using ChargeGlobe.Infrastructure.Identity;
using ChargeGlobe.Infrastructure.Ingest;
using ChargeGlobe.Infrastructure.Mail;
using ChargeGlobe.Infrastructure.Persistence;
using ChargeGlobe.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace ChargeGlobe.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// What every process needs (api, crawler, worker): database access, the shared key ring,
    /// the config service and mail delivery.
    /// </summary>
    public static IServiceCollection AddInfrastructureCore(this IServiceCollection services, string connectionString)
    {
        // PostGIS types (location.geom) need the NetTopologySuite plugin on the data source and in EF Core.
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).UseNetTopologySuite().Build());
        services.AddSingleton<DbDataSource>(provider => provider.GetRequiredService<NpgsqlDataSource>());
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<AppDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql.UseNetTopologySuite()));

        // One key ring for all containers, kept in the database so that it survives rebuilds.
        services.AddDataProtection().SetApplicationName("chargeglobe").PersistKeysToDbContext<AppDbContext>();

        services.AddScoped<AuditLog>();
        services.AddScoped<ISourceRegistry, SourceRegistry>();
        services.AddScoped<ILocationQueries, LocationQueries>();
        services.AddScoped<IEvseQueries, EvseQueries>();
        services.AddScoped<ISearchQueries, SearchQueries>();
        services.AddScoped<IOperatorQueries, OperatorQueries>();
        services.AddScoped<IFavorites, Favorites>();
        services.AddScoped<IUserPreferences, UserPreferences>();
        services.AddScoped<ITariffCatalog, TariffCatalog>();
        services.AddScoped<IngestProcessor>();
        services.AddScoped<IIngestQueue, IngestQueue>();
        services.AddScoped<IPollPlanStore, PollPlanStore>();
        services.AddScoped<IRawSnapshotStore, RawSnapshotStore>();
        services.AddScoped<IJobLeases, JobLeases>();
        services.AddScoped<IPollTileStore, PollTileStore>();
        services.AddScoped<IStatusDemand, StatusDemand>();
        services.AddScoped<IProviderBackoff, ProviderBackoffStore>();
        services.AddScoped<ICrawlerRunLog, CrawlerRunLog>();
        services.AddScoped<ICrawlerMonitor, CrawlerMonitor>();
        services.AddSingleton<IProviderRateBudget, ProviderRateBudget>();
        services.AddSingleton<SettingsCache>();
        services.AddSingleton<IAppConfig, AppConfig>();

        services.AddSingleton<IMailTransport, MailKitTransport>();
        services.AddScoped<IOutboxDispatcher, OutboxDispatcher>();
        return services;
    }

    /// <summary>Follows setting changes of other processes; requires the migrated database.</summary>
    public static IServiceCollection AddSettingsCacheRefresh(this IServiceCollection services) =>
        services.AddHostedService<SettingsCacheRefresher>();

    /// <summary>Everything the API needs on top of <see cref="AddInfrastructureCore"/>.</summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, string connectionString, AuthSettings authSettings)
    {
        services.AddInfrastructureCore(connectionString);
        services.AddSingleton<IDatabaseProbe, DatabaseProbe>();
        services.AddSingleton(authSettings);

        services
            .AddIdentityCore<AppUser>(options =>
            {
                // Length over composition rules; the user name is the e-mail address.
                options.Password.RequiredLength = AuthSettings.MinimumPasswordLength;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.User.RequireUniqueEmail = true;
                options.User.AllowedUserNameCharacters = string.Empty;
            })
            .AddRoles<AppRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddTokenProvider<HashedTokenProvider>(TokenOptions.DefaultProvider);

        services.AddScoped<IEmailSender, OutboxEmailSender>();
        services.AddScoped<PermissionService>();
        services.AddScoped<AccountService>();
        services.AddScoped<UserAdministration>();
        services.AddSingleton<SetupToken>();
        services.AddScoped<SetupService>();
        services.AddScoped<SessionService>();
        services.AddScoped<ApiKeyService>();
        services.AddScoped<RegionSeeder>();
        services.AddScoped<DatabaseInitializer>();
        return services;
    }
}
