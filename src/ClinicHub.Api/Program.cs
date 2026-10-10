using System.Threading.RateLimiting;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Catalog;
using ClinicHub.Api.Data;
using ClinicHub.Api.Endpoints;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// The catalog (clinics, accounts, roles) and one database per clinic, chosen per request from the signed-in clinic.
builder.Services.AddClinicDatabases(builder.Configuration);
builder.Services.AddScoped<SecurityLog>();
builder.Services.AddScoped<ClinicProvisioning>();
// Two-factor secrets and clinic connection strings are encrypted with these keys: keep them with the data.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("ClinicHub");
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection("Demo"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher<Account>, PasswordHasher<Account>>();
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
    var provisioning = services.GetRequiredService<ClinicProvisioning>();
    await provisioning.UpgradeAllAsync();
    if (services.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled)
        await DemoSeeder.SeedAsync(services.GetRequiredService<CatalogDbContext>(), provisioning, services.GetRequiredService<ClinicDatabases>(),
            services.GetRequiredService<IPasswordHasher<Account>>(), services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);
}

// Clinic data asked for before a clinic was chosen: tell the page to show the clinic picker.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (NoClinicException ex) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
        await ctx.Response.WriteAsJsonAsync(new { title = ex.Message, chooseClinic = true });
    }
});
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
app.MapGet("/api/settings", (IOptions<DemoOptions> demo) => new { Demo = demo.Value.Enabled, DemoUsers = demo.Value.Enabled ? DemoSeeder.DemoUsers : [], AccountsLocked = demo.Value.Enabled && demo.Value.LockAccounts }).AllowAnonymous();
app.MapAuthEndpoints();
app.MapFrontDeskEndpoints();
app.MapEncounterEndpoints();
app.MapBillingEndpoints();
app.MapUserEndpoints();
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program;
