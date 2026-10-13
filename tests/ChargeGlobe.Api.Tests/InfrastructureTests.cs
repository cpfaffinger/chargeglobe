using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using ChargeGlobe.Application.Health;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Infrastructure;
using ChargeGlobe.Infrastructure.Health;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChargeGlobe.Api.Tests;

public class InfrastructureTests
{
    [Fact]
    public async Task AddInfrastructure_registers_the_database_probe_on_the_npgsql_data_source()
    {
        var services = new ServiceCollection().AddLogging();

        services.AddInfrastructure(
            "Host=localhost;Database=unused",
            AuthSettings.Create(name => name == "JWT_SIGNING_KEY" ? new string('k', 32) : null));

        await using var provider = services.BuildServiceProvider();
        Assert.IsType<DatabaseProbe>(provider.GetRequiredService<IDatabaseProbe>());
        Assert.Same(provider.GetRequiredService<Npgsql.NpgsqlDataSource>(), provider.GetRequiredService<DbDataSource>());
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Probe_reports_whether_a_connection_can_be_opened(bool reachable)
    {
        var probe = new DatabaseProbe(new FakeDataSource(reachable), NullLogger<DatabaseProbe>.Instance);

        Assert.Equal(reachable, await probe.CanConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Probe_propagates_cancellation()
    {
        var probe = new DatabaseProbe(new FakeDataSource(reachable: true), NullLogger<DatabaseProbe>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.CanConnectAsync(new CancellationToken(canceled: true)));
    }

    private sealed class FakeDataSource(bool reachable) : DbDataSource
    {
        public override string ConnectionString => string.Empty;

        protected override DbConnection CreateDbConnection() => new FakeConnection(reachable);
    }

    private sealed class FakeConnection(bool reachable) : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => string.Empty;

        public override string DataSource => string.Empty;

        public override string ServerVersion => string.Empty;

        public override ConnectionState State => ConnectionState.Closed;

        public override void Open()
        {
            if (!reachable)
            {
                throw new InvalidOperationException("Connection refused.");
            }
        }

        public override void Close()
        {
        }

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
