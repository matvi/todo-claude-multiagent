using Azure.Core;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TodoApi.Data;

/// <summary>
/// Assembles the <see cref="TodoDbContext"/> registration. The application authenticates
/// to PostgreSQL with <b>Entra / managed identity ONLY</b>, unconditionally, in every
/// environment including local development (specs.md §14).
///
/// <para>
/// There is exactly one code path and exactly one configuration value:
/// <c>ConnectionStrings:TodoDb</c> (env var <c>ConnectionStrings__TodoDb</c>), which must
/// be <b>passwordless</b> and whose <c>Username=</c> must be the Postgres role that maps
/// to the caller's Entra identity (in Azure: the <c>todo-api</c> managed identity's
/// <c>pgaadauth</c> role; locally: the local role, see specs §14.6). The former
/// <c>Postgres:UseEntraAuth</c> flag and the password-auth branch are deleted — a single
/// connection-string shape can never be the wrong shape for the mode.
/// </para>
///
/// <para>
/// An Entra access token is acquired via <c>DefaultAzureCredential</c> and handed to Npgsql
/// as the password through <c>UsePasswordProvider</c>, which invokes the callback on the
/// connection-open path so the token is present before the very first connection's auth
/// handshake. The <see cref="TokenCredential"/> caches the token in-memory and refreshes it
/// near expiry, so the ~60-minute rotation is handled without a background timer.
/// </para>
///
/// <para>
/// NOTE: this deliberately does NOT use <c>UsePeriodicPasswordProvider</c>. That API
/// fetches the token on a background timer ("invoked in a timer, and not when opening
/// connections" per Npgsql's docs), which raced the app's startup <c>Migrate()</c>
/// connection and yielded an empty password ("No password has been provided ... in
/// cleartext") — a live outage documented in
/// <c>.pipeline/deployment-lessons-learned.md</c> §5a. Never reintroduce it.
/// </para>
/// </summary>
public static class TodoDbContextRegistration
{
    /// <summary>
    /// Logger category used for the startup Postgres-auth diagnostics (specs §14.5).
    /// </summary>
    internal const string LoggerCategory = "TodoApi.Data.TodoDbContextRegistration";

    /// <summary>
    /// Entra token scope for Azure Database for PostgreSQL Flexible Server (§13.4/§14).
    /// </summary>
    private const string PostgresEntraTokenScope =
        "https://ossrdbms-aad.database.windows.net/.default";

    /// <summary>
    /// Hosts for which TLS is NOT force-enabled: the local Docker Postgres (specs §14.6)
    /// serves no TLS, and nothing leaves the machine.
    /// </summary>
    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "::1"];

    public static IServiceCollection AddTodoDbContext(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("TodoDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Connection string 'TodoDb' is not configured. Set ConnectionStrings:TodoDb " +
                "(or the ConnectionStrings__TodoDb environment variable).");
        }

        // Entra / managed-identity auth is the ONLY supported mode (specs §14.3).
        // Registered as a lazy factory: merely calling AddTodoDbContext performs no
        // credential, normalization or network work.
        services.AddSingleton(sp => BuildEntraAuthenticatedDataSource(
            connectionString,
            new DefaultAzureCredential(),
            sp.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory)));

        services.AddDbContext<TodoDbContext>((sp, options) =>
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));

        return services;
    }

    /// <summary>
    /// Builds an <see cref="NpgsqlDataSource"/> whose password is supplied by an Entra
    /// access token. The <see cref="TokenCredential"/> is injected so the token-acquisition
    /// seam is testable without a live Entra endpoint or Postgres server; in the app it is
    /// <c>DefaultAzureCredential</c>, which resolves to <c>ManagedIdentityCredential</c>
    /// inside Azure Container Apps and to <c>AzureCliCredential</c> locally — no secret
    /// either way.
    ///
    /// <para>
    /// <paramref name="baseConnectionString"/> is normalized by
    /// <see cref="BuildEntraConnectionString"/> first, so any static credential it carries
    /// is stripped here rather than assumed absent by contract. Without that,
    /// <c>UsePasswordProvider</c> would throw
    /// <c>NotSupportedException: When registering a password provider, a password or
    /// password file may not be set</c> (the live failure in specs §14.1a).
    /// </para>
    ///
    /// <para>
    /// The data source is a singleton so it is reused across requests and disposed by DI.
    /// Normalization and logging therefore happen exactly once, at construction — not per
    /// connection and not per request.
    /// </para>
    /// </summary>
    /// <param name="logger">
    /// Optional; when supplied, one <c>Information</c> line describing the resolved
    /// principal is emitted (plus warnings for anything that had to be corrected). Never
    /// receives the token, the password, or the full connection string.
    /// </param>
    internal static NpgsqlDataSource BuildEntraAuthenticatedDataSource(
        string baseConnectionString,
        TokenCredential credential,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var normalized = BuildEntraConnectionString(baseConnectionString);
        LogStartupDiagnostics(logger, normalized);

        var passwordProvider = new EntraTokenPasswordProvider(credential, PostgresEntraTokenScope);

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(normalized.ConnectionString);
        dataSourceBuilder.UsePasswordProvider(
            passwordProvider.GetPassword,
            passwordProvider.GetPasswordAsync);

        return dataSourceBuilder.Build();
    }

    /// <summary>
    /// Pure, side-effect-free normalization of the configured connection string into the
    /// shape the Entra password provider requires (specs §14.5). Performs no I/O, acquires
    /// no token and touches no ambient state, so it is directly unit-testable.
    ///
    /// <para>Rules, in order:</para>
    /// <list type="number">
    ///   <item>Parse with <see cref="NpgsqlConnectionStringBuilder"/> (never hand-rolled —
    ///     keyword aliases such as <c>pwd</c> and quoting must be handled). A parse failure
    ///     is surfaced as an <see cref="InvalidOperationException"/> naming the config key.</item>
    ///   <item>Strip <c>Password</c> <b>and</b> <c>Passfile</c>. Both are required: Npgsql's
    ///     <c>PrepareConfiguration()</c> rejects a password provider when <i>either</i> is
    ///     set.</item>
    ///   <item>Fail fast if <c>Username</c> is missing — an opaque Postgres <c>28P01</c>
    ///     later is worse than a clear error now.</item>
    ///   <item>Force <c>SslMode=Require</c> for non-loopback hosts when SSL is
    ///     <c>Disable</c>/<c>Allow</c>: the Entra token travels in the cleartext-password
    ///     field. Loopback is exempt (the local dev container has no TLS, §14.6), and any
    ///     stricter mode already configured is left alone.</item>
    ///   <item>Everything else round-trips verbatim.</item>
    /// </list>
    /// </summary>
    internal static EntraConnectionString BuildEntraConnectionString(string baseConnectionString)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(baseConnectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidOperationException(
                "ConnectionStrings:TodoDb is not a valid PostgreSQL connection string and " +
                "could not be parsed.", ex);
        }

        // 2. Strip any static credential (defence-in-depth, §14.3).
        var staticCredentialRemoved =
            !string.IsNullOrEmpty(builder.Password) || !string.IsNullOrEmpty(builder.Passfile);
        builder.Password = null;
        builder.Passfile = null;

        // 3. Validate the principal.
        if (string.IsNullOrWhiteSpace(builder.Username))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:TodoDb must specify Username= — the Postgres role that " +
                "maps to this application's Entra identity (in Azure, the todo-api managed " +
                "identity's role). Postgres authentication for this application is " +
                "Entra-only (specs §14).");
        }

        // 4. Guarantee TLS for non-loopback hosts.
        SslMode? sslModeForcedFrom = null;
        if (!IsLoopbackHost(builder.Host) && builder.SslMode is SslMode.Disable or SslMode.Allow)
        {
            sslModeForcedFrom = builder.SslMode;
            builder.SslMode = SslMode.Require;
        }

        return new EntraConnectionString(
            builder.ConnectionString,
            builder.Host,
            builder.Database,
            builder.Username,
            staticCredentialRemoved,
            sslModeForcedFrom);
    }

    /// <summary>
    /// Emits the once-per-process Postgres auth diagnostics (specs §14.5). Null-safe so the
    /// data source can be built in tests without a logger. Never logs the token, the
    /// password, or the full connection string.
    /// </summary>
    private static void LogStartupDiagnostics(ILogger? logger, EntraConnectionString normalized)
    {
        if (logger is null)
        {
            return;
        }

        logger.LogInformation(
            "Postgres auth: Entra / managed identity. Host={Host} Database={Database} Username={Username}",
            normalized.Host,
            normalized.Database,
            normalized.Username);

        if (normalized.StaticCredentialRemoved)
        {
            logger.LogWarning(
                "A static Postgres password/passfile was present in ConnectionStrings:TodoDb " +
                "and has been ignored — this application authenticates with Entra only " +
                "(specs §14). Remove the credential from configuration.");
        }

        if (normalized.SslModeForcedFrom is { } originalSslMode)
        {
            logger.LogWarning(
                "Postgres SslMode was {Original}; forced to Require for host {Host} because " +
                "the Entra token is transmitted as a cleartext password.",
                originalSslMode,
                normalized.Host);
        }
    }

    /// <summary>
    /// True for the loopback hosts that are exempt from forced TLS (specs §14.5 step 4).
    /// Multi-host connection strings and unix-socket paths are deliberately treated as
    /// non-loopback, i.e. they get TLS forced — the safe default.
    /// </summary>
    private static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        // Tolerate the bracketed IPv6 literal form, e.g. "[::1]".
        var candidate = host.Trim().Trim('[', ']');

        return LoopbackHosts.Contains(candidate, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Result of <see cref="TodoDbContextRegistration.BuildEntraConnectionString"/>: the
/// normalized, passwordless connection string plus the non-sensitive facts needed for the
/// startup log. Contains no credential.
/// </summary>
/// <param name="ConnectionString">The normalized connection string to build the data source from.</param>
/// <param name="Host">Resolved host, for logging.</param>
/// <param name="Database">Resolved database name, for logging.</param>
/// <param name="Username">Resolved Postgres role (the Entra principal's role), for logging.</param>
/// <param name="StaticCredentialRemoved">True if a <c>Password</c> or <c>Passfile</c> was present and stripped.</param>
/// <param name="SslModeForcedFrom">The previous <c>SslMode</c> if TLS had to be forced; otherwise null.</param>
internal sealed record EntraConnectionString(
    string ConnectionString,
    string? Host,
    string? Database,
    string? Username,
    bool StaticCredentialRemoved,
    SslMode? SslModeForcedFrom);
