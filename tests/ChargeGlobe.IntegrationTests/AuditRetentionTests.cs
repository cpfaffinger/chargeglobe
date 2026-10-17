using System.Text.Json;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Infrastructure.Identity;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ChargeGlobe.IntegrationTests;

/// <summary>Retention of the audit log (audit.retention_days, default 365). A database of its own.</summary>
public class AuditRetentionTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>
{
    private static readonly DateTimeOffset Now = new(2027, 10, 17, 3, 0, 0, TimeSpan.Zero);

    private Task<int> PurgeAsync() =>
        factory.WithScopeAsync(services => new AuditRetention(
            services.GetRequiredService<AppDbContext>(), services.GetRequiredService<IAppConfig>(), new FakeTimeProvider(Now)).PurgeAsync(CancellationToken.None));

    [Fact]
    public async Task Entries_older_than_the_retention_are_deleted()
    {
        await factory.WithDbAsync(db =>
        {
            db.AuditLog.Where(entry => entry.Target.StartsWith("retention:")).ExecuteDelete();
            db.AuditLog.AddRange(
                new AuditLogEntry { Action = "test.old", Target = "retention:old", OccurredAt = Now.AddDays(-400) },
                new AuditLogEntry { Action = "test.month", Target = "retention:month", OccurredAt = Now.AddDays(-100) },
                new AuditLogEntry { Action = "test.new", Target = "retention:new", OccurredAt = Now.AddDays(-1) });
            return db.SaveChangesAsync();
        });
        async Task<string[]> LeftAsync() =>
            [.. await factory.WithDbAsync(db => db.AuditLog.Where(entry => entry.Target.StartsWith("retention:")).OrderBy(entry => entry.OccurredAt).Select(entry => entry.Target).ToListAsync())];

        // The other entries of the database (setup, logins) are newer than a year before "now", which lies in 2027.
        Assert.Equal(1, await PurgeAsync());
        Assert.Equal(["retention:month", "retention:new"], await LeftAsync());

        try
        {
            await factory.WithScopeAsync(async services =>
            {
                await services.GetRequiredService<IAppConfig>().SetAsync(
                    SettingsCatalog.AuditRetentionDays, SettingDefinition.GlobalScope, JsonDocument.Parse("30").RootElement, new Actor(null), CancellationToken.None);
                return true;
            });
            Assert.InRange(await PurgeAsync(), 1, int.MaxValue);
            Assert.Equal(["retention:new"], await LeftAsync());
        }
        finally
        {
            await factory.WithScopeAsync(async services =>
            {
                await services.GetRequiredService<IAppConfig>().SetAsync(
                    SettingsCatalog.AuditRetentionDays, SettingDefinition.GlobalScope, null, new Actor(null), CancellationToken.None);
                return true;
            });
        }
    }
}
