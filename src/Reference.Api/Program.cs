using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Reference.Api;

var builder = WebApplication.CreateBuilder(args);
var local = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");
var provider = builder.Configuration["Database:Provider"] ?? (local ? "Sqlite" : "SqlServer");
var seedDemo = builder.Configuration.GetValue<bool>("Demo:Seed");
if (seedDemo && (!builder.Environment.IsDevelopment() || provider != "Sqlite"))
    throw new InvalidOperationException("El seeder demo solo está disponible en Development con SQLite.");
var connection = builder.Configuration.GetConnectionString("Payroll")
    ?? (local && provider == "Sqlite" ? "Data Source=reference.db" : throw new InvalidOperationException("Configura ConnectionStrings__Payroll."));

builder.Services.AddDbContext<AppDb>(options =>
{
    if (provider == "SqlServer") options.UseSqlServer(connection);
    else if (local && provider == "Sqlite") options.UseSqlite(connection);
    else throw new InvalidOperationException("Usa SqlServer para el despliegue; Sqlite solo está habilitado en desarrollo y pruebas.");
});
builder.Services.AddProblemDetails();
if (builder.Configuration["Security:KeyDirectory"] is { Length: > 0 } keyDirectory)
{
    builder.Services.AddDataProtection()
        .SetApplicationName("Payroll.Reference")
        .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
}
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(
    new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)));
builder.Services.AddScoped<IPasswordHasher<Person>, PasswordHasher<Person>>();
builder.Services.AddSingleton<DummyPassword>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "__Host-reference";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = false;
    options.Events.OnValidatePrincipal = Access.ValidateSession;
    options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-Token";
    options.Cookie.Name = "__Host-reference-csrf";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
app.UseExceptionHandler();
if (!local) app.UseHsts();
app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var headers = ctx.Context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        if (ctx.Context.Request.Path.StartsWithSegments("/assets"))
            headers.CacheControl = "public, max-age=31536000, immutable";
        if (ctx.File.Name != "index.html") return;
        headers.CacheControl = "no-store";
        headers.ContentSecurityPolicy = "default-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
        headers["Referrer-Policy"] = "no-referrer";
    }
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        var method = context.Request.Method;
        if (context.GetEndpoint() is not null && (HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method)))
        {
            try
            {
                await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                await Results.Problem(statusCode: 400, title: "Token CSRF faltante o inválido.").ExecuteAsync(context);
                return;
            }
        }
    }
    await next(context);
});

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (AppDb db, CancellationToken ct) =>
{
    try
    {
        await db.RevisionAsync(ct);
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception error) when (error is not OperationCanceledException)
    {
        app.Logger.LogWarning("La base no está lista ({ErrorType}).", error.GetType().Name);
        return Results.StatusCode(503);
    }
});
app.MapAuth();
app.MapAdministration();
app.MapPayroll();
app.MapActivities();

var initialize = app.Configuration.GetValue<bool>("Database:Initialize");
if (initialize || seedDemo || app.Configuration["Bootstrap:Password"] is not null)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    if (initialize)
    {
        if (db.Database.IsSqlServer()) await db.Database.MigrateAsync();
        else await db.Database.EnsureCreatedAsync();
    }
    var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<Person>>();
    await Access.Bootstrap(db, hasher, app.Configuration);
    if (seedDemo)
        await DemoSeed.Load(db, hasher, app.Configuration, app.Lifetime.ApplicationStopping);
}

app.Run();

public partial class Program;
