using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KevinSite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KevinSite.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task OriginalPageAndAssetsAreServedByTheBackend()
    {
        await using var factory = new TestApplication();
        using var client = factory.Client();
        Assert.Contains("Familia Perez", await client.GetStringAsync("/"));
        Assert.Contains("#176b57", await client.GetStringAsync("/styles.css"));
        var script = await client.GetStringAsync("/script.js");
        Assert.Contains("/api/auth/csrf", script);
        Assert.DoesNotContain("const USERS", script);
        Assert.DoesNotContain("localStorage", script);
        var page = await client.GetStringAsync("/");
        Assert.Contains("receiptOutputDialog", page);
        Assert.Contains("productSalePrice", page);
        Assert.DoesNotContain("src=\"logo.png\"", page);
        var logo = await client.GetByteArrayAsync("/Logo.png");
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, logo[..8]);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public void PasswordsAreSaltedAndNotStoredAsPlaintext()
    {
        var hasher = new PasswordHasher<AppUser>();
        var user = new AppUser("test", "", "Seller");
        var first = hasher.HashPassword(user, "test-only-password");
        var second = hasher.HashPassword(user, "test-only-password");
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("test-only-password", first);
        Assert.NotEqual(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(user, first, "test-only-password"));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(user, first, "wrong"));
    }

    [Fact]
    public async Task AnonymousCannotReadSharedData()
    {
        await using var factory = new TestApplication();
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task LoginRequiresCsrfAndDoesNotAcceptWrongPasswords()
    {
        await using var factory = new TestApplication();
        using var client = factory.Client();
        var body = new LoginInput("seller", "test-only-password");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/login", body)).StatusCode);
        await SetCsrfAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/login", body with { Password = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", body)).StatusCode);
        await SetCsrfAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task SellerCannotMutateInventoryButAdminCan()
    {
        await using var factory = new TestApplication();
        using var seller = factory.Client();
        using var admin = factory.Client();
        await LoginAsync(seller, "Heilyn");
        await LoginAsync(admin, "admin");
        var product = new ProductInput("Test product", "", 10, 1, "");
        Assert.Equal(HttpStatusCode.Forbidden, (await seller.PostAsJsonAsync("/api/products", product)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await seller.DeleteAsync("/api/products/test")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await seller.PostAsJsonAsync("/api/products/import", Array.Empty<ProductInput>())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/products", product)).StatusCode);
    }

    [Fact]
    public async Task MutationsRejectMissingCsrfAndNegativeStock()
    {
        await using var factory = new TestApplication();
        using var client = factory.Client();
        await LoginAsync(client, "admin");
        var product = new ProductInput("Test", "", 10, -1, "");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/products", product)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/products", product with { Stock = 1 })).StatusCode);
    }

    [Fact]
    public async Task LockedAccountsCannotSignIn()
    {
        await using var factory = new TestApplication();
        using var client = factory.Client();
        await SetCsrfAsync(client);
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.PostAsJsonAsync("/api/auth/login", new LoginInput("seller", "wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/login", new LoginInput("seller", "test-only-password"))).StatusCode);
    }

    [Fact]
    public void ImportAndPhotosAreValidatedBeforePersistence()
    {
        Assert.Throws<AppException>(() => Validation.Product(new ProductInput("Test", "", 10, 1, "data:image/svg+xml;base64,PHN2Zz4=")));
        Assert.Throws<AppException>(() => Validation.Product(new ProductInput("Test", "", 10, 1, "data:image/png;base64,YWJj")));
        Assert.Throws<AppException>(() => Validation.Import([
            new ProductInput("First", "", 1, 1, "", "same"),
            new ProductInput("Second", "", 1, 1, "", "SAME")
        ]));
        Assert.Throws<AppException>(() => Validation.Import(new ProductInput[1001]));
        var valid = Validation.Product(new ProductInput(" Test ", null, null, 0, null));
        Assert.Equal("Test", valid.Name);
        Assert.Equal("", valid.Photo);
    }

    [Fact]
    public void SalesRequireCompleteInstallmentsAndValidPaymentMethod()
    {
        var input = new SaleInput(Guid.NewGuid(), "p1", "Test", "Customer", "demo", "demo", "demo",
            100, "3 cuotas", "Efectivo", ["2026-11-01", "2026-12-01", "2027-01-01"]);
        Validation.Sale(input);
        Assert.Throws<AppException>(() => Validation.Sale(input with { InstallmentDates = [] }));
        Assert.Throws<AppException>(() => Validation.Sale(input with { Method = "unknown" }));
        Assert.Throws<AppException>(() => Validation.Sale(input with { Price = -1 }));
    }

    [Fact]
    public async Task ProductSalePricesAreReturnedAndValidated()
    {
        await using var factory = new TestApplication();
        using var client = factory.Client();
        await LoginAsync(client, "admin");
        var input = new ProductInput("Test", "", 10, 1, "", SalePrice: 25);
        var response = await client.PostAsJsonAsync("/api/products", input);
        response.EnsureSuccessStatusCode();
        Assert.Equal(25m, (await response.Content.ReadFromJsonAsync<Product>())!.SalePrice);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/products", input with { SalePrice = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/products", input with { SalePrice = 1_000_000_000 })).StatusCode);
    }

    [Fact]
    public void SalePriceRoundTripsAndLegacyProductsRemainCompatible()
    {
        const string legacy = """{"id":"old","name":"Legacy","details":"","cost":10,"stock":1,"photo":""}""";
        var oldProduct = JsonSerializer.Deserialize<Product>(legacy, SqlAppStore.JsonOptions)!;
        Assert.Null(oldProduct.SalePrice);
        var input = new ProductInput("New", "", 10, 1, "", SalePrice: 0);
        var product = Validation.Product(input);
        var json = JsonSerializer.Serialize(product, SqlAppStore.JsonOptions);
        Assert.Equal(0m, JsonSerializer.Deserialize<Product>(json, SqlAppStore.JsonOptions)!.SalePrice);
        Assert.Null(Validation.Product(input with { SalePrice = null }).SalePrice);
        Assert.Equal(25m, Validation.Import([input with { SalePrice = 25 }])[0].SalePrice);
        Assert.Throws<AppException>(() => Validation.Import([input with { SalePrice = -1 }]));
    }

    private static async Task LoginAsync(HttpClient client, string username)
    {
        await SetCsrfAsync(client);
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginInput(username, "test-only-password"));
        response.EnsureSuccessStatusCode();
        await SetCsrfAsync(client);
    }

    private static async Task SetCsrfAsync(HttpClient client)
    {
        var token = await client.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf");
        Assert.NotNull(token);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.Token);
    }
    private sealed record CsrfResponse(string Token);
}

sealed class TestApplication : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAppStore>();
            services.AddSingleton<IAppStore, MemoryStore>();
        });
    }
    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
}

sealed class MemoryStore : IAppStore
{
    private readonly Dictionary<string, AppUser> users;
    public MemoryStore()
    {
        var hasher = new PasswordHasher<AppUser>();
        users = new[] { "seller", "admin", "Heilyn" }.ToDictionary(name => name, name =>
        {
            var user = new AppUser(name, "", name == "admin" ? "Admin" : "Seller");
            return user with { PasswordHash = hasher.HashPassword(user, "test-only-password") };
        });
    }
    public Task<AppUser?> FindUserAsync(string username, CancellationToken ct) => Task.FromResult(users.GetValueOrDefault(username));
    public Task RecordLoginAsync(string username, bool success, CancellationToken ct)
    {
        var user = users[username];
        var attempts = success ? 0 : user.FailedAttempts + 1;
        users[username] = user with { FailedAttempts = attempts, LockedUntil = attempts >= 5 ? DateTimeOffset.UtcNow.AddMinutes(15) : null };
        return Task.CompletedTask;
    }
    public Task<AppState> ReadStateAsync(CancellationToken ct) => Task.FromResult(new AppState([], [], []));
    public Task<Product> AddProductAsync(Product product, CancellationToken ct) => Task.FromResult(product);
    public Task DeleteProductAsync(string id, CancellationToken ct) => Task.CompletedTask;
    public Task ReplaceInventoryAsync(Product[] products, CancellationToken ct) => Task.CompletedTask;
    public Task<Sale> AddSaleAsync(SaleInput input, string seller, CancellationToken ct) => throw new NotSupportedException();
    public Task<CustomerRequest> AddRequestAsync(RequestInput input, string user, CancellationToken ct) => throw new NotSupportedException();
}
