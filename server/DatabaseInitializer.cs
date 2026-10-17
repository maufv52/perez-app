using System.Data;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;

namespace KevinSite;

public static class DatabaseInitializer
{
    public static async Task RunAsync(IConfiguration configuration, IPasswordHasher<AppUser> hasher)
    {
        var connectionString = configuration.GetConnectionString("AppDb")
            ?? throw new InvalidOperationException("ConnectionStrings:AppDb is required.");
        var seedsJson = Environment.GetEnvironmentVariable("KEVIN_SEED_JSON")
            ?? throw new InvalidOperationException("KEVIN_SEED_JSON must supply the explicitly approved initial accounts.");
        var seeds = JsonSerializer.Deserialize<SeedUser[]>(seedsJson, SqlAppStore.JsonOptions)
            ?? throw new InvalidOperationException("Invalid seed account configuration.");
        if (seeds.Length == 0 || seeds.Any(seed => string.IsNullOrWhiteSpace(seed.Username)
                || seed.Username.Length > 50 || string.IsNullOrEmpty(seed.Password) || seed.Role is not ("Admin" or "Seller")))
            throw new InvalidOperationException("Invalid seed account.");
        await using var connection = new SqlConnection(connectionString);
        for (var attempt = 0; ; attempt++)
        {
            try { await connection.OpenAsync(); break; }
            catch (SqlException error) when (error.Number == 42118 && attempt < 12)
            {
                Console.WriteLine("Waiting for the newly created perimeter IP rule to propagate; retrying in 10 seconds.");
                SqlConnection.ClearPool(connection);
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KevinSite.Database.schema.sql")
            ?? throw new InvalidOperationException("Missing database schema.");
        using var reader = new StreamReader(stream);
        await using (var command = new SqlCommand(await reader.ReadToEndAsync(), connection))
            await command.ExecuteNonQueryAsync();
        foreach (var seed in seeds)
        {
            var user = new AppUser(seed.Username, "", seed.Role);
            await using var command = new SqlCommand("""
                IF NOT EXISTS (SELECT 1 FROM dbo.AppUsers WHERE Username = @username)
                    INSERT dbo.AppUsers (Username, PasswordHash, Role) VALUES (@username, @hash, @role)
                """, connection);
            command.Parameters.Add("@username", SqlDbType.NVarChar, 50).Value = user.Username;
            command.Parameters.Add("@hash", SqlDbType.NVarChar, 500).Value = hasher.HashPassword(user, seed.Password);
            command.Parameters.Add("@role", SqlDbType.VarChar, 20).Value = seed.Role;
            await command.ExecuteNonQueryAsync();
        }
        var principalId = configuration["AppPrincipalId"];
        if (!Guid.TryParse(principalId, out var objectId))
            throw new InvalidOperationException("AppPrincipalId must be the deployed application's managed identity GUID.");
        var sid = Convert.ToHexString(objectId.ToByteArray());
        // The authorized human administrator resolves the exact identity; no directory role is granted to the server.
        await using (var grant = new SqlCommand($"""
            SET XACT_ABORT ON;
            BEGIN TRY
                BEGIN TRANSACTION;
                IF DATABASE_PRINCIPAL_ID(N'app-kevin-site-dev-b931') IS NULL
                BEGIN
                    IF DATABASE_PRINCIPAL_ID(N'kevin-site-app') IS NOT NULL
                    BEGIN
                        IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'kevin-site-app' AND sid = 0x{sid})
                            THROW 50001, 'The existing database principal does not match the application identity.', 1;
                        DROP USER [kevin-site-app];
                    END;
                    CREATE USER [app-kevin-site-dev-b931] FROM EXTERNAL PROVIDER WITH OBJECT_ID = '{objectId:D}';
                END;
                GRANT CONNECT TO [app-kevin-site-dev-b931];
                GRANT SELECT ON dbo.AppUsers TO [app-kevin-site-dev-b931];
                GRANT UPDATE (FailedAttempts, LockedUntil) ON dbo.AppUsers TO [app-kevin-site-dev-b931];
                GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.Products TO [app-kevin-site-dev-b931];
                GRANT SELECT, INSERT, UPDATE ON dbo.Sales TO [app-kevin-site-dev-b931];
                GRANT SELECT, INSERT ON dbo.CustomerRequests TO [app-kevin-site-dev-b931];
                COMMIT;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK;
                THROW;
            END CATCH;
            """, connection))
            await grant.ExecuteNonQueryAsync();
        Console.WriteLine("Database schema, initial account hashes, and application table permissions initialized.");
    }
}
