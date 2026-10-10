using System.Threading.RateLimiting;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Data;
using ClinicHub.Api.Endpoints;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("ClinicHub")
    ?? throw new InvalidOperationException("ConnectionStrings:ClinicHub is not configured.");
builder.Services.AddDbContext<ClinicHubDbContext>(o => o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection("Demo"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddClinicAuth();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull);

var loginsPerMinute = builder.Configuration.GetValue("RateLimits:LoginsPerMinute", 10);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.LoginRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginsPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var db = services.GetRequiredService<ClinicHubDbContext>();
    await db.Database.MigrateAsync();
    await ReferenceSeeder.SeedAsync(db);
    if (services.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled)
        await DemoSeeder.SeedAsync(db, services.GetRequiredService<IPasswordHasher<User>>(), services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseDefaultFiles();
app.UseStaticFiles();
if (app.Environment.IsDevelopment())
    app.MapOpenApi().AllowAnonymous();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseCsrfHeaderCheck();

app.MapGet("/healthz", () => Results.Ok("ok")).AllowAnonymous();
app.MapGet("/api/settings", (IOptions<DemoOptions> demo) => new { Demo = demo.Value.Enabled, DemoUsers = demo.Value.Enabled ? DemoSeeder.DemoUsers : [] }).AllowAnonymous();
app.MapAuthEndpoints();
app.MapFrontDeskEndpoints();
app.MapEncounterEndpoints();
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program;
