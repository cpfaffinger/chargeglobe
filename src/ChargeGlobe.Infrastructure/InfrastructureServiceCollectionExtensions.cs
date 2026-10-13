using System.Data.Common;
using ChargeGlobe.Application.Health;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Mail;
using ChargeGlobe.Infrastructure.Health;
using ChargeGlobe.Infrastructure.Identity;
using ChargeGlobe.Infrastructure.Mail;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace ChargeGlobe.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, string connectionString, AuthSettings authSettings)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<DbDataSource>(provider => provider.GetRequiredService<NpgsqlDataSource>());
        services.AddSingleton<IDatabaseProbe, DatabaseProbe>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(authSettings);

        services.AddDbContext<AppDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));

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
        services.AddScoped<AuditLog>();
        services.AddScoped<PermissionService>();
        services.AddScoped<AccountService>();
        services.AddScoped<SessionService>();
        services.AddScoped<ApiKeyService>();
        services.AddScoped<DatabaseInitializer>();
        return services;
    }
}
