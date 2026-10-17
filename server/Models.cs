using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;

namespace KevinSite;

public sealed record AppUser(string Username, string PasswordHash, string Role, int FailedAttempts = 0, DateTimeOffset? LockedUntil = null);
public sealed record LoginInput(string Username, string Password);
public sealed record SeedUser(string Username, string Password, string Role);
public sealed record ProductInput(string? Name, string? Details, decimal? Cost, int Stock, string? Photo, string? Id = null,
    decimal? SalePrice = null);
public sealed record Product(string Id, string Name, string Details, decimal? Cost, int Stock, string Photo, decimal? SalePrice = null);
public sealed record SaleInput(Guid RequestId, string ProductId, string Name, string LastName, string CustomerId,
    string Phone, string Address, decimal Price, string Plan, string Method, string[] InstallmentDates);
public sealed record Sale(string Invoice, string Name, string LastName, string CustomerId, string Phone, string Address,
    string Product, decimal Price, string Plan, string Method, string[] InstallmentDates, string Seller, DateTimeOffset CreatedAt);
public sealed record RequestInput(string Name, string LastName, string RequestDate, string DeliveryDate,
    string Description, string EstimatedPrice, string Plan);
public sealed record CustomerRequest(string Ticket, string Name, string LastName, string RequestDate, string DeliveryDate,
    string Description, string EstimatedPrice, string Plan, string CreatedBy);
public sealed record AppState(Product[] Inventory, Sale[] Orders, CustomerRequest[] Requests);
public sealed class AppException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public interface IAppStore
{
    Task<AppUser?> FindUserAsync(string username, CancellationToken ct);
    Task RecordLoginAsync(string username, bool success, CancellationToken ct);
    Task<AppState> ReadStateAsync(CancellationToken ct);
    Task<Product> AddProductAsync(Product product, CancellationToken ct);
    Task DeleteProductAsync(string id, CancellationToken ct);
    Task ReplaceInventoryAsync(Product[] products, CancellationToken ct);
    Task<Sale> AddSaleAsync(SaleInput input, string seller, CancellationToken ct);
    Task<CustomerRequest> AddRequestAsync(RequestInput input, string user, CancellationToken ct);
}

public sealed class LoginService(IAppStore store, IPasswordHasher<AppUser> hasher)
{
    private readonly AppUser dummy = new("unknown", "", "Seller");
    private readonly string dummyHash = hasher.HashPassword(new AppUser("unknown", "", "Seller"), Guid.NewGuid().ToString());

    public async Task<AppUser?> AuthenticateAsync(LoginInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Username) || input.Username.Length > 50
            || string.IsNullOrEmpty(input.Password) || input.Password.Length > 256)
            throw new AppException(400, "Usuario o contraseña inválidos.");
        var user = await store.FindUserAsync(input.Username.Trim(), ct);
        var verification = hasher.VerifyHashedPassword(user ?? dummy, user?.PasswordHash ?? dummyHash, input.Password);
        if (user?.LockedUntil > DateTimeOffset.UtcNow) return null;
        var success = user is not null && verification != PasswordVerificationResult.Failed;
        if (user is not null) await store.RecordLoginAsync(user.Username, success, ct);
        return success ? user : null;
    }
}

public static partial class Validation
{
    public static Product Product(ProductInput input)
    {
        var name = Text(input.Name, 120, "Nombre del artículo");
        var details = OptionalText(input.Details, 2000, "Detalles");
        if (input.Cost is < 0 or > 999999999 || input.SalePrice is < 0 or > 999999999 || input.Stock is < 0 or > 1000000)
            throw new AppException(400, "Costo, valor de venta o unidades inválidos.");
        var photo = input.Photo ?? "";
        if (photo.Length > 1_400_000) throw new AppException(400, "La foto no puede superar 1 MB.");
        if (photo.Length > 0)
        {
            var match = PhotoPattern().Match(photo);
            if (!match.Success) throw new AppException(400, "La foto debe ser JPEG o PNG.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(match.Groups[2].Value); }
            catch (FormatException) { throw new AppException(400, "La foto no tiene un formato válido."); }
            var jpeg = bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
            var png = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            if (bytes.Length > 1_048_576 || (match.Groups[1].Value == "jpeg" ? !jpeg : !png))
                throw new AppException(400, "La foto no tiene un formato o tamaño válido.");
        }
        var id = string.IsNullOrWhiteSpace(input.Id) ? Guid.NewGuid().ToString("N") : Text(input.Id, 100, "Identificador");
        return new Product(id, name, details, input.Cost, input.Stock, photo, input.SalePrice);
    }

    public static Product[] Import(ProductInput[] inputs)
    {
        if (inputs is null || inputs.Length > 1000)
            throw new AppException(400, "Importa como máximo 1000 artículos por archivo.");
        var products = inputs.Select(Product).ToArray();
        if (products.Select(product => product.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != products.Length)
            throw new AppException(400, "El archivo contiene identificadores duplicados.");
        return products;
    }

    public static void Sale(SaleInput input)
    {
        if (input.RequestId == Guid.Empty) throw new AppException(400, "Falta el identificador de la venta.");
        Text(input.ProductId, 100, "Artículo");
        Text(input.Name, 100, "Nombre"); Text(input.LastName, 150, "Apellidos");
        Text(input.CustomerId, 50, "Cédula"); Text(input.Phone, 50, "Teléfono"); Text(input.Address, 1000, "Dirección");
        Plan(input.Plan);
        if (input.Method is not ("Efectivo" or "Sinpe") || input.Price is < 0 or > 999999999)
            throw new AppException(400, "Precio o método de pago inválidos.");
        if (input.InstallmentDates is null || input.InstallmentDates.Length != (input.Plan == "3 cuotas" ? 3 : 0)
            || input.InstallmentDates.Any(date => !IsDate(date)))
            throw new AppException(400, "Indica las fechas de las cuotas.");
    }

    public static void Request(RequestInput input)
    {
        Text(input.Name, 100, "Nombre"); Text(input.LastName, 150, "Apellidos"); Text(input.Description, 2000, "Descripción");
        Plan(input.Plan);
        if (!IsDate(input.RequestDate) || !IsDate(input.DeliveryDate))
            throw new AppException(400, "Fechas del pedido inválidas.");
        if (input.EstimatedPrice != "" && (!decimal.TryParse(input.EstimatedPrice, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var price) || price is < 0 or > 999999999))
            throw new AppException(400, "Precio aproximado inválido.");
    }

    private static void Plan(string plan)
    {
        if (plan is not ("contado" or "3 cuotas")) throw new AppException(400, "Forma de pago inválida.");
    }
    private static bool IsDate(string value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    private static string Text(string? value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max) throw new AppException(400, $"{field}: valor inválido.");
        return value.Trim();
    }
    private static string OptionalText(string? value, int max, string field) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value.Trim() : throw new AppException(400, $"{field}: texto demasiado largo.");
    [GeneratedRegex(@"\Adata:image/(jpeg|png);base64,([A-Za-z0-9+/=\r\n]+)\z")]
    private static partial Regex PhotoPattern();
}
