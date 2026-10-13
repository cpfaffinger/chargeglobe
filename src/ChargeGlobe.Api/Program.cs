using System.Diagnostics.CodeAnalysis;
using ChargeGlobe.Api;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var authSettings = AuthSettings.Create(name => builder.Configuration[name]);
builder.Services.AddInfrastructure(
    builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' is not configured."),
    authSettings);
builder.Services.AddApi(authSettings);

var app = builder.Build();

app.UseApi();

app.Run();

// Composition root only: wires services and endpoints and has no logic of its own.
// It is exercised by the WebApplicationFactory tests, but excluded from the coverage
// metric because the host bootstrap (app.Run, missing-configuration guard) cannot be
// covered in-process.
[ExcludeFromCodeCoverage]
public partial class Program;
