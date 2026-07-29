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
///    Npgsql as the password through <c>UsePasswordProvider</c>, which invokes the
///    callback on the connection-open path so the token is present before the very
///    first connection's auth handshake. The <see cref="TokenCredential"/> caches the
///    token in-memory and refreshes it near expiry, so the ~60-minute rotation is
///    handled automatically without a background timer.
///
///    <para>
///    NOTE: this deliberately does NOT use <c>UsePeriodicPasswordProvider</c>. That
///    API fetches the token on a background timer ("invoked in a timer, and not when
///    opening connections" per Npgsql's docs), which raced the app's startup
///    <c>Migrate()</c> connection and yielded an empty password
///    ("No password has been provided ... in cleartext") — a live outage documented
///    in <c>.pipeline/deployment-lessons-learned.md</c> §5a.
///    </para>
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
    /// access token obtained via the app's managed identity. Uses the app's
    /// <c>DefaultAzureCredential</c>, which resolves to <c>ManagedIdentityCredential</c>
    /// inside Azure Container Apps and requires no secret (spec §13.4).
    /// The <paramref name="baseConnectionString"/> must NOT contain a <c>Password=</c>.
    /// </summary>
    private static NpgsqlDataSource BuildEntraAuthenticatedDataSource(string baseConnectionString)
        => BuildEntraAuthenticatedDataSource(baseConnectionString, new DefaultAzureCredential());

    /// <summary>
    /// Core builder with the <see cref="TokenCredential"/> injected, so the
    /// token-acquisition seam is testable without a live Entra endpoint or Postgres
    /// server. Registered as a singleton so it is reused across requests and disposed
    /// by DI.
    ///
    /// <para>
    /// The token is supplied via <see cref="NpgsqlDataSourceBuilder.UsePasswordProvider"/>,
    /// which invokes the provider on the connection-open path (both the synchronous
    /// <c>Open()</c> used by startup <c>Migrate()</c> and the async
    /// <c>OpenAsync()</c> used at request time). This guarantees the password is
    /// present before the first connection authenticates — unlike
    /// <c>UsePeriodicPasswordProvider</c>, whose background-timer fetch raced the first
    /// connection (deployment-lessons-learned §5a).
    /// </para>
    /// </summary>
    internal static NpgsqlDataSource BuildEntraAuthenticatedDataSource(
        string baseConnectionString,
        TokenCredential credential)
    {
        var passwordProvider = new EntraTokenPasswordProvider(credential, PostgresEntraTokenScope);

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(baseConnectionString);
        dataSourceBuilder.UsePasswordProvider(
            passwordProvider.GetPassword,
            passwordProvider.GetPasswordAsync);

        return dataSourceBuilder.Build();
    }
}
