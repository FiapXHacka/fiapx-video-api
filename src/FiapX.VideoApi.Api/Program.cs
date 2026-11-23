using FiapX.VideoApi.Api.Auth;
using FiapX.VideoApi.Api.Health;
using FiapX.VideoApi.Api.Observability;
using FiapX.VideoApi.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});

builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddVideoApiInfrastructure(builder.Configuration);
builder.Services.AddVideoApiAuthentication(builder.Configuration);
builder.Services.AddVideoApiTelemetry(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapVideoApiHealthChecks();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await app.MigrateVideoDatabaseAsync();
}

await app.RunAsync();
