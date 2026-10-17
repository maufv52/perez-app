using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace KevinSite;

public sealed class SqlAppStore(IConfiguration configuration) : IAppStore
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string connectionString = configuration.GetConnectionString("AppDb")
        ?? throw new InvalidOperationException("ConnectionStrings:AppDb is required.");

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<AppUser?> FindUserAsync(string username, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("SELECT Username, PasswordHash, Role, FailedAttempts, LockedUntil FROM dbo.AppUsers WHERE Username = @username", connection);
        command.Parameters.Add("@username", SqlDbType.NVarChar, 50).Value = username;
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new AppUser(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4))
            : null;
    }

    public async Task RecordLoginAsync(string username, bool success, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var sql = success
            ? "UPDATE dbo.AppUsers SET FailedAttempts = 0, LockedUntil = NULL WHERE Username = @username"
            : """
              UPDATE dbo.AppUsers
              SET FailedAttempts = CASE WHEN LockedUntil <= SYSDATETIMEOFFSET() THEN 1 ELSE FailedAttempts + 1 END,
                  LockedUntil = CASE WHEN LockedUntil <= SYSDATETIMEOFFSET() THEN NULL
                                     WHEN FailedAttempts + 1 >= 5 THEN DATEADD(minute, 15, SYSDATETIMEOFFSET()) ELSE NULL END
              WHERE Username = @username
              """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@username", SqlDbType.NVarChar, 50).Value = username;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<AppState> ReadStateAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var products = new List<Product>();
        await using (var command = new SqlCommand("SELECT Data, Stock FROM dbo.Products ORDER BY CreatedAt, Id", connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                products.Add(Read<Product>(reader.GetString(0)) with { Stock = reader.GetInt32(1) });
        var sales = await ReadRowsAsync<Sale>("SELECT Data FROM dbo.Sales ORDER BY Id", connection, ct);
        var requests = await ReadRowsAsync<CustomerRequest>("SELECT Data FROM dbo.CustomerRequests ORDER BY Id", connection, ct);
        return new AppState(products.ToArray(), sales, requests);
    }

    public async Task<Product> AddProductAsync(Product product, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await InsertProductAsync(product, connection, null, ct);
        return product;
    }

    public async Task DeleteProductAsync(string id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("DELETE FROM dbo.Products WHERE Id = @id", connection);
        command.Parameters.Add("@id", SqlDbType.NVarChar, 100).Value = id;
        if (await command.ExecuteNonQueryAsync(ct) == 0) throw new AppException(404, "El artículo ya no existe.");
    }

    public async Task ReplaceInventoryAsync(Product[] products, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using (var command = new SqlCommand("DELETE FROM dbo.Products", connection, transaction))
            await command.ExecuteNonQueryAsync(ct);
        foreach (var product in products) await InsertProductAsync(product, connection, transaction, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<Sale> AddSaleAsync(SaleInput input, string seller, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        // Lock the unique request key before touching stock, making retries idempotent.
        await using (var existing = new SqlCommand("SELECT Data FROM dbo.Sales WITH (UPDLOCK, HOLDLOCK) WHERE RequestId = @requestId", connection, transaction))
        {
            existing.Parameters.Add("@requestId", SqlDbType.UniqueIdentifier).Value = input.RequestId;
            var data = await existing.ExecuteScalarAsync(ct) as string;
            if (data is not null) { await transaction.CommitAsync(ct); return Read<Sale>(data); }
        }
        string? productJson;
        await using (var update = new SqlCommand("UPDATE dbo.Products SET Stock = Stock - 1 OUTPUT inserted.Data WHERE Id = @id AND Stock > 0", connection, transaction))
        {
            update.Parameters.Add("@id", SqlDbType.NVarChar, 100).Value = input.ProductId;
            productJson = await update.ExecuteScalarAsync(ct) as string;
        }
        if (productJson is null) throw new AppException(409, "El artículo ya no tiene unidades disponibles.");
        long id;
        await using (var insert = new SqlCommand("INSERT dbo.Sales (RequestId, Data) OUTPUT inserted.Id VALUES (@requestId, N'{}')", connection, transaction))
        {
            insert.Parameters.Add("@requestId", SqlDbType.UniqueIdentifier).Value = input.RequestId;
            id = (long)(await insert.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Missing invoice ID."));
        }
        var now = DateTimeOffset.UtcNow;
        var sale = new Sale($"FAC-{now:yyyyMMdd}-{id:D6}", input.Name.Trim(), input.LastName.Trim(),
            input.CustomerId.Trim(), input.Phone.Trim(), input.Address.Trim(), Read<Product>(productJson).Name,
            input.Price, input.Plan, input.Method, input.InstallmentDates, seller, now);
        await using (var update = new SqlCommand("UPDATE dbo.Sales SET Data = @data WHERE Id = @id", connection, transaction))
        {
            update.Parameters.Add("@data", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(sale, JsonOptions);
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return sale;
    }

    public async Task<CustomerRequest> AddRequestAsync(RequestInput input, string user, CancellationToken ct)
    {
        var request = new CustomerRequest($"PED-{Guid.NewGuid():N}", input.Name.Trim(), input.LastName.Trim(),
            input.RequestDate, input.DeliveryDate, input.Description.Trim(), input.EstimatedPrice, input.Plan, user);
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("INSERT dbo.CustomerRequests (Data) VALUES (@data)", connection);
        command.Parameters.Add("@data", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(request, JsonOptions);
        await command.ExecuteNonQueryAsync(ct);
        return request;
    }

    private static async Task InsertProductAsync(Product product, SqlConnection connection, SqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = new SqlCommand("INSERT dbo.Products (Id, Stock, Data) VALUES (@id, @stock, @data)", connection, transaction);
        command.Parameters.Add("@id", SqlDbType.NVarChar, 100).Value = product.Id;
        command.Parameters.Add("@stock", SqlDbType.Int).Value = product.Stock;
        command.Parameters.Add("@data", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(product, JsonOptions);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T[]> ReadRowsAsync<T>(string sql, SqlConnection connection, CancellationToken ct)
    {
        var rows = new List<T>();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(Read<T>(reader.GetString(0)));
        return rows.ToArray();
    }

    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException("Invalid persisted database record.");
}
