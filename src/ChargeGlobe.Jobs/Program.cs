using System.Diagnostics.CodeAnalysis;
using ChargeGlobe.Infrastructure;
using ChargeGlobe.Jobs;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddInfrastructureCore(
    builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' is not configured."));
builder.Services.AddSettingsCacheRefresh();
builder.Services.AddJobs(JobOptions.Create(builder.Configuration["role"], Environment.MachineName));

builder.Build().Run();

// Composition root only: reads --role and the connection string, registers services and blocks in Run().
// Excluded from the coverage metric because starting the real host cannot be
// covered in a unit test; everything it wires is tested on its own.
[ExcludeFromCodeCoverage]
internal partial class Program;
