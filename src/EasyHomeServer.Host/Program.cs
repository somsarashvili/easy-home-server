using EasyHomeServer.Host;
using EasyHomeServer.Host.Components;
using EasyHomeServer.Host.Data;
using EasyHomeServer.Host.Infrastructure;
using EasyHomeServer.Host.Modules;
using EasyHomeServer.Host.Security;
using EasyHomeServer.Sdk;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Detects systemd at runtime and no-ops elsewhere, so `dotnet run` and the Docker image are
// unaffected. Under the unit it enables Type=notify readiness and drops timestamps from log
// lines, which journald already stamps.
builder.Host.UseSystemd();

builder.Services
    .AddOptions<EasyHomeServerOptions>()
    .Bind(builder.Configuration.GetSection(EasyHomeServerOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var options = builder.Configuration
    .GetSection(EasyHomeServerOptions.SectionName)
    .Get<EasyHomeServerOptions>() ?? new EasyHomeServerOptions();

var modulesPath = Path.GetFullPath(options.ModulesPath, builder.Environment.ContentRootPath);
var dataPath = Path.GetFullPath(options.DataPath, builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataPath);

// Modules must be discovered before the container is built, because each one contributes
// service registrations. A bootstrap logger carries the scan's output until the real logging
// pipeline exists.
using var bootstrapLoggerFactory = LoggerFactory.Create(logging =>
    logging.AddConfiguration(builder.Configuration.GetSection("Logging")).AddConsole());

var bootstrapLogger = bootstrapLoggerFactory.CreateLogger<ModuleLoader>();
var scanned = new ModuleLoader(bootstrapLogger).Load(modulesPath);

builder.Services.AddSingleton<IEventBus, EventBus>();
builder.Services.AddSingleton<ISystemRunner, SystemRunner>();
builder.Services.AddSingleton<AdminAccount>();
builder.Services.AddSingleton<HostInfo>();

builder.Services.AddDbContextFactory<AppDbContext>(db =>
    db.UseSqlite($"Data Source={Path.Combine(dataPath, "easyhomeserver.db")}"));

// Cookies are encrypted with data protection keys. Without an explicit, persisted key ring
// they are regenerated on every start and every session is silently signed out on restart.
builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(Directory.CreateDirectory(Path.Combine(dataPath, "keys")))
    .SetApplicationName("EasyHomeServer");

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookie =>
    {
        cookie.Cookie.Name = "easyhomeserver.auth";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Lax;

        // The tool is HTTP on a LAN for now; requiring a secure cookie would lock users out.
        // Revisit when HTTPS lands.
        cookie.Cookie.SecurePolicy = CookieSecurePolicy.None;
        cookie.LoginPath = "/login";
        cookie.AccessDeniedPath = "/login";
        cookie.ExpireTimeSpan = TimeSpan.FromDays(14);
        cookie.SlidingExpiration = true;
    });

// Deny by default: anything that does not opt out with [AllowAnonymous] needs a signed-in admin.
builder.Services
    .AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMudServices();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Modules contribute their own services. A module that throws here is dropped rather than
// taking the host down with it, so the final catalog is only known after this loop.
var configuredModules = new List<LoadedModule>();
var failures = new List<ModuleLoadFailure>(scanned.Failures);

foreach (var module in scanned.Modules)
{
    try
    {
        var context = ModuleContext.Create(module.Manifest.Id, builder.Configuration, dataPath);
        module.Instance.ConfigureServices(builder.Services, context);
        configuredModules.Add(module);
    }
    catch (Exception ex)
    {
        bootstrapLogger.LogError(
            ex,
            "Module {ModuleId} threw from ConfigureServices and will not be loaded.",
            module.Manifest.Id);

        failures.Add(new ModuleLoadFailure
        {
            Name = module.Manifest.Id,
            Path = module.Directory,
            Kind = ModuleLoadFailureKind.InitializationError,
            Reason = $"ConfigureServices threw: {ex.Message}",
        });
    }
}

var catalog = new ModuleCatalog(configuredModules, failures);
builder.Services.AddSingleton(catalog);

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseStaticFiles();

// Static assets embedded in dynamically loaded module assemblies. Registered before the
// component endpoints so /_content/* is served as a file rather than falling through to
// routing and 404ing.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = PluginStaticFileProvider.Create(
        catalog,
        app.Services.GetRequiredService<ILogger<PluginStaticFileProvider>>()),
    RequestPath = string.Empty,
});

app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<FirstRunSetupMiddleware>();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies([.. catalog.ComponentAssemblies]);

foreach (var module in catalog.Modules)
{
    try
    {
        module.Instance.MapEndpoints(app);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "Module {ModuleId} threw from MapEndpoints; its endpoints are unavailable but the host continues.",
            module.Manifest.Id);
    }
}

app.MapPost("/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    return Results.Redirect("/login");
});

app.Run();
