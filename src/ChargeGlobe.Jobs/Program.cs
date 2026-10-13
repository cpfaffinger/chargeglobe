using System.Diagnostics.CodeAnalysis;
using ChargeGlobe.Jobs;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(JobOptions.Create(builder.Configuration["role"], Environment.MachineName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<HeartbeatService>();

builder.Build().Run();

// Composition root only: reads --role, registers services and blocks in Run().
// Excluded from the coverage metric because starting the real host cannot be
// covered in a unit test; everything it wires is tested on its own.
[ExcludeFromCodeCoverage]
internal partial class Program;
