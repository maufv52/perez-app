using System.Data;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using KevinSite;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace KevinSite.Tests;

public sealed class SqlIntegrationTests
{
    [LiveApiFact]
    [Trait("Category", "Sql")]
    public async Task LiveApiSharesPhotosSalesAndRequestsAcrossSessionsAndEnforcesRoles()
    {
        using var admin = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
            { BaseAddress = new Uri(Environment.GetEnvironmentVariable("KEVIN_TEST_BASEURL")!) };
        using var seller = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
            { BaseAddress = admin.BaseAddress };
        using var zip = ZipFile.OpenRead(Environment.GetEnvironmentVariable("KEVIN_TEST_DEMO_ZIP")!);
        using var reader = new StreamReader(zip.GetEntry("script.js")!.Open());
        var source = await reader.ReadToEndAsync();
        async Task LoginAsync(HttpClient client, string username)
        {
            var password = Regex.Match(source, $@"(?m)^\s*{Regex.Escape(username)}:\s*'([^']*)'").Groups[1].Value;
            async Task CsrfAsync()
            {
                var token = await client.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf");
                client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
                client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token!.Token);
            }
            await CsrfAsync();
            (await client.PostAsJsonAsync("/api/auth/login", new LoginInput(username, password))).EnsureSuccessStatusCode();
            await CsrfAsync();
        }
        var product = Product();
        var saleInput = Input(product.Id);
        CustomerRequest? request = null;
        try
        {
            await LoginAsync(admin, "Masteruser");
            await LoginAsync(seller, "Marcelina");
            const string photo = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";
            var input = new ProductInput(product.Name, "", product.Cost, 1, photo, product.Id);
            Assert.Equal(HttpStatusCode.Forbidden, (await seller.PostAsJsonAsync("/api/products", input)).StatusCode);
            (await admin.PostAsJsonAsync("/api/products", input)).EnsureSuccessStatusCode();
            var sellerState = await seller.GetFromJsonAsync<AppState>("/api/state");
            Assert.Equal(photo, sellerState!.Inventory.Single(item => item.Id == product.Id).Photo);
            var response = await seller.PostAsJsonAsync("/api/sales", saleInput);
            response.EnsureSuccessStatusCode();
            var sale = (await response.Content.ReadFromJsonAsync<Sale>())!;
            var retry = await seller.PostAsJsonAsync("/api/sales", saleInput);
            retry.EnsureSuccessStatusCode();
            Assert.Equal(sale.Invoice, (await retry.Content.ReadFromJsonAsync<Sale>())!.Invoice);
            var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
            var requestResponse = await seller.PostAsJsonAsync("/api/requests",
                new RequestInput("Demo", "Test", today, today, "__integration_test__", "", "contado"));
            requestResponse.EnsureSuccessStatusCode();
            request = await requestResponse.Content.ReadFromJsonAsync<CustomerRequest>();
            var adminState = (await admin.GetFromJsonAsync<AppState>("/api/state"))!;
            Assert.Equal(0, adminState.Inventory.Single(item => item.Id == product.Id).Stock);
            Assert.Contains(adminState.Orders, order => order.Invoice == sale.Invoice && order.Seller == "Marcelina");
            Assert.Contains(adminState.Requests, item => item.Ticket == request!.Ticket);
            (await admin.DeleteAsync($"/api/products/{product.Id}")).EnsureSuccessStatusCode();
        }
        finally
        {
            await CleanupAsync(product.Id, [saleInput.RequestId]);
            if (request is not null)
            {
                await using var connection = new SqlConnection(ConnectionString);
                await connection.OpenAsync();
                await using var delete = new SqlCommand("DELETE FROM dbo.CustomerRequests WHERE JSON_VALUE(Data, '$.ticket') = @ticket", connection);
                delete.Parameters.Add("@ticket", SqlDbType.NVarChar, 100).Value = request.Ticket;
                await delete.ExecuteNonQueryAsync();
            }
        }
    }

    private sealed record CsrfResponse(string Token);

    [SqlFact]
    [Trait("Category", "Sql")]
    public async Task SaleRetriesHaveOneInvoiceAndOneStockDecrement()
    {
        var product = Product();
        var input = Input(product.Id);
        var store = Store();
        try
        {
            await store.AddProductAsync(product, default);
            var first = await store.AddSaleAsync(input, "__integration_test__", default);
            var retry = await store.AddSaleAsync(input, "__integration_test__", default);
            Assert.Equal(first.Invoice, retry.Invoice);
            Assert.Equal(0, (await store.ReadStateAsync(default)).Inventory.Single(item => item.Id == product.Id).Stock);
        }
        finally { await CleanupAsync(product.Id, [input.RequestId]); }
    }

    [SqlFact]
    [Trait("Category", "Sql")]
    public async Task ConcurrentSalesCannotOversellTheLastUnit()
    {
        var product = Product();
        var first = Input(product.Id);
        var second = Input(product.Id);
        var store = Store();
        try
        {
            await store.AddProductAsync(product, default);
            async Task<int> AttemptAsync(SaleInput input)
            {
                try { await store.AddSaleAsync(input, "__integration_test__", default); return 200; }
                catch (AppException error) when (error.StatusCode == 409) { return 409; }
            }
            var results = await Task.WhenAll(AttemptAsync(first), AttemptAsync(second));
            Assert.Equal([200, 409], results.Order().ToArray());
            Assert.Equal(0, (await store.ReadStateAsync(default)).Inventory.Single(item => item.Id == product.Id).Stock);
        }
        finally { await CleanupAsync(product.Id, [first.RequestId, second.RequestId]); }
    }

    private static string ConnectionString => Environment.GetEnvironmentVariable("KEVIN_TEST_DB")
        ?? throw new InvalidOperationException("KEVIN_TEST_DB is required for live SQL tests.");
    private static SqlAppStore Store() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["ConnectionStrings:AppDb"] = ConnectionString }).Build());
    private static Product Product() => new($"test-{Guid.NewGuid():N}", "__integration_test__", "", 1, 1, "");
    private static SaleInput Input(string id) => new(Guid.NewGuid(), id, "Demo", "Test", "not-real", "not-real", "not-real",
        100, "contado", "Efectivo", []);

    private static async Task CleanupAsync(string productId, Guid[] requestIds)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        foreach (var requestId in requestIds)
        {
            await using var delete = new SqlCommand("DELETE FROM dbo.Sales WHERE RequestId = @id", connection);
            delete.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = requestId;
            await delete.ExecuteNonQueryAsync();
        }
        await using var product = new SqlCommand("DELETE FROM dbo.Products WHERE Id = @id", connection);
        product.Parameters.Add("@id", SqlDbType.NVarChar, 100).Value = productId;
        await product.ExecuteNonQueryAsync();
    }
}

sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KEVIN_TEST_DB")))
            Skip = "Set KEVIN_TEST_DB to explicitly enable live SQL tests.";
    }
}

sealed class LiveApiFactAttribute : FactAttribute
{
    public LiveApiFactAttribute()
    {
        if (new[] { "KEVIN_TEST_DB", "KEVIN_TEST_BASEURL", "KEVIN_TEST_DEMO_ZIP" }
            .Any(name => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))))
            Skip = "Explicit live SQL/API configuration is required.";
    }
}
