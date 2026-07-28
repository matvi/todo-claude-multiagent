using Azure.Core;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace TodoApi.Data;

/// <summary>
/// Assembles the <see cref="TodoDbContext"/> registration, choosing the PostgreSQL
/// authentication mode from configuration. See specs.md §13.4 (managed-identity Postgres).
///
/// Two mutually exclusive paths, selected by the <c>Postgres:UseEntraAuth</c> flag
/// (env var <c>Postgres__UseEntraAuth</c>):
///  - <b>Password auth (default, flag false/unset):</b> the connection string carries
///    the credential. Used for the local Docker Postgres (§6) and any environment that
///    has not cut over to managed identity. Existing behavior — unchanged.
///  - <b>Managed-identity (Entra) auth (flag true):</b> the connection string is
///    passwordless (Username = the managed identity's Postgres role name). An Entra
///    access token is acquired via the Container App's managed identity and handed to
///    Npgsql as a rotating password through the periodic password provider, so the
///    ~60-minute token is refreshed automatically (not fetched once at startup).
/// </summary>
public static class TodoDbContextRegistration
{
    /// <summary>Config flag that switches on Entra/managed-identity Postgres auth (§13.4).</summary>
    public const string UseEntraAuthKey = "Postgres:UseEntraAuth";

    /// <summary>
    /// Entra token scope for Azure Database for PostgreSQL Flexible Server (§13.4).
    /// </summary>
    private const string PostgresEntraTokenScope =
        "https://ossrdbms-aad.database.windows.net/.default";

    // Tokens live ~60 minutes; refresh comfortably ahead of expiry, and retry quickly
    // on a transient acquisition failure (spec §13.4 example values).
    private static readonly TimeSpan TokenRefreshPeriod = TimeSpan.FromMinutes(50);
    private static readonly TimeSpan TokenRefreshFailureRetry = TimeSpan.FromSeconds(5);

    public static IServiceCollection AddTodoDbContext(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("TodoDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Connection string 'TodoDb' is not configured. Set ConnectionStrings:TodoDb " +
                "(or the ConnectionStrings__TodoDb environment variable).");
        }

        var useEntraAuth = configuration.GetValue<bool>(UseEntraAuthKey);

        if (useEntraAuth)
        {
            services.AddSingleton(_ => BuildEntraAuthenticatedDataSource(connectionString));
            services.AddDbContext<TodoDbContext>((sp, options) =>
                options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        }
        else
        {
            // Local dev / password auth: the credential is in the connection string.
            services.AddDbContext<TodoDbContext>(options =>
                options.UseNpgsql(connectionString));
        }

        return services;
    }

    /// <summary>
    /// Builds an <see cref="NpgsqlDataSource"/> whose password is supplied by an Entra
    /// access token obtained via the app's managed identity, refreshed periodically.
    /// The <paramref name="baseConnectionString"/> must NOT contain a <c>Password=</c>.
    /// Registered as a singleton so it is reused across requests and disposed by DI.
    /// </summary>
    private static NpgsqlDataSource BuildEntraAuthenticatedDataSource(string baseConnectionString)
    {
        // DefaultAzureCredential resolves to ManagedIdentityCredential inside Azure
        // Container Apps; it requires no secret. (spec §13.4)
        var credential = new DefaultAzureCredential();

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(baseConnectionString);
        dataSourceBuilder.UsePeriodicPasswordProvider(
            async (_, cancellationToken) =>
            {
                var token = await credential.GetTokenAsync(
                    new TokenRequestContext(new[] { PostgresEntraTokenScope }),
                    cancellationToken).ConfigureAwait(false);
                return token.Token;
            },
            TokenRefreshPeriod,
            TokenRefreshFailureRetry);

        return dataSourceBuilder.Build();
    }
}
