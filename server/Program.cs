using System.Security.Claims;
using System.Threading.RateLimiting;
using KevinSite;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.Configure<PasswordHasherOptions>(options => options.IterationCount = 210_000);
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddSingleton<IAppStore, SqlAppStore>();
builder.Services.AddSingleton<LoginService>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "KevinSite.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
        options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

if (args.Contains("--initialize-database"))
{
    await DatabaseInitializer.RunAsync(builder.Configuration,
        new PasswordHasher<AppUser>(Microsoft.Extensions.Options.Options.Create(
            new PasswordHasherOptions { IterationCount = 210_000 })));
    return;
}

var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'; base-uri 'self'; object-src 'none'";
    if (context.Request.Path.StartsWithSegments("/api"))
        context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api")
        && !HttpMethods.IsGet(context.Request.Method)
        && !HttpMethods.IsHead(context.Request.Method))
        await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
    await next();
});

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "ventas-movil-api" }));
app.MapGet("/api/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
    Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));
app.MapPost("/api/auth/login", async (LoginInput input, HttpContext context, LoginService login, CancellationToken ct) =>
{
    var user = await login.AuthenticateAsync(input, ct);
    if (user is null) return Results.Problem(statusCode: 401, detail: "Usuario o contraseña incorrectos.");
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Username),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.Role, user.Role)
    };
    await context.SignInAsync(new ClaimsPrincipal(
        new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok(new { username = user.Username, role = user.Role });
}).RequireRateLimiting("login");
app.MapGet("/api/auth/me", (ClaimsPrincipal user) =>
    Results.Ok(new { username = user.Identity!.Name, role = user.FindFirstValue(ClaimTypes.Role) }))
    .RequireAuthorization();
app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync();
    return Results.NoContent();
}).RequireAuthorization();

var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/state", async (IAppStore store, CancellationToken ct) => Results.Ok(await store.ReadStateAsync(ct)));
api.MapPost("/products", async (ProductInput input, IAppStore store, CancellationToken ct) =>
    Results.Ok(await store.AddProductAsync(Validation.Product(input), ct)))
    .RequireAuthorization(policy => policy.RequireRole("Admin"));
api.MapDelete("/products/{id}", async (string id, IAppStore store, CancellationToken ct) =>
{
    await store.DeleteProductAsync(id, ct);
    return Results.NoContent();
}).RequireAuthorization(policy => policy.RequireRole("Admin"));
api.MapPost("/products/import", async (ProductInput[] inputs, IAppStore store, CancellationToken ct) =>
{
    var products = Validation.Import(inputs);
    await store.ReplaceInventoryAsync(products, ct);
    return Results.Ok(new { count = products.Length });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));
api.MapPost("/sales", async (SaleInput input, ClaimsPrincipal user, IAppStore store, CancellationToken ct) =>
{
    Validation.Sale(input);
    return Results.Ok(await store.AddSaleAsync(input, user.Identity!.Name!, ct));
});
api.MapPost("/requests", async (RequestInput input, ClaimsPrincipal user, IAppStore store, CancellationToken ct) =>
{
    Validation.Request(input);
    return Results.Ok(await store.AddRequestAsync(input, user.Identity!.Name!, ct));
});
app.Run();

public partial class Program;

sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, detail) = exception switch
        {
            AppException problem => (problem.StatusCode, problem.Message),
            AntiforgeryValidationException => (400, "La sesión de seguridad venció. Recarga la página e inténtalo de nuevo."),
            SqlException => (503, "La base de datos no está disponible o se está reactivando. Espera unos segundos e inténtalo de nuevo."),
            _ => (500, "No se pudo completar la operación.")
        };
        if (status >= 500) logger.LogError(exception, "API request failed: {Path}", context.Request.Path);
        else logger.LogWarning("API request rejected: {Path}, {Reason}", context.Request.Path, detail);
        await Results.Problem(statusCode: status, detail: detail).ExecuteAsync(context);
        return true;
    }
}
