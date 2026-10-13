using System.Diagnostics.CodeAnalysis;
using ChargeGlobe.Api.Health;
using ChargeGlobe.Infrastructure;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(
    builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' is not configured."));

var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapHealthEndpoints();

app.Run();

// Composition root only: wires services and endpoints and has no logic of its own.
// It is exercised by the WebApplicationFactory tests, but excluded from the coverage
// metric because the host bootstrap (app.Run, missing-configuration guard) cannot be
// covered in-process.
[ExcludeFromCodeCoverage]
public partial class Program;
