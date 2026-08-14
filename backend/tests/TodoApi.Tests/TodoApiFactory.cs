using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TodoApi.Auth;
using TodoApi.Data;
using TodoApi.Models;

namespace TodoApi.Tests;

/// <summary>
/// WebApplicationFactory that boots the real ASP.NET Core pipeline (routing,
/// model validation, controllers, CORS, authentication, authorization, /health) but:
///
/// <list type="bullet">
/// <item>swaps the Npgsql-backed <c>TodoDbContext</c> for a uniquely-named EF Core
/// InMemory database, since no real Postgres instance is available in this
/// environment;</item>
/// <item>supplies valid-shaped (but non-production) <c>AzureAd:*</c> configuration so the
/// app's fail-fast startup guard (specs §16.7.1) is satisfied without a real external
/// tenant;</item>
/// <item>adds a <b>test-only authentication scheme</b> (<see cref="TestAuthHandler"/>) and
/// makes it the <em>default</em> scheme, so requests are authenticated deterministically
/// from claims the test controls, instead of via a real signed JWT / live IdP. The real
/// <c>Microsoft.Identity.Web</c> JwtBearer registration from <c>Program.cs</c> is left
/// intact (proving the app still requires it to boot) — it is simply never the scheme any
/// test request actually uses, so no network call to a JWKS/OIDC endpoint ever happens
/// (specs §16.18: "no Azure, no external tenant, no real token").</item>
/// </list>
///
/// <para>
/// This is the "test authentication scheme + stubbed role source" extension called for by
/// specs §16.18 item 22: extending this shared test fixture is the one place where editing
/// existing test infrastructure is correct, because <c>Program.cs</c> now authenticates
/// every non-<c>/health</c> endpoint and the pre-existing tests (and every new one) need a
/// way to present a controlled principal without a live Entra tenant.
/// </para>
///
/// <para>
/// <see cref="ConfigureClient"/> attaches a default <em>valid</em> bearer token to every
/// client this factory creates via <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/>,
/// so the pre-existing <c>TodosControllerTests</c> (which call <c>_factory.CreateClient()</c>
/// with no knowledge of auth) keep passing unmodified once "the test host supplies an
/// authenticated principal" — exactly the wording of specs §16.18 item 21. Tests that need
/// to exercise unauthenticated / insufficient-scope / wrong-tenant / role-unavailable paths
/// explicitly override or clear that header via <see cref="CreateAuthenticatedClient"/> /
/// <see cref="CreateUnauthenticatedClient"/>.
/// </para>
/// </summary>
public class TodoApiFactory : WebApplicationFactory<Program>
{
    /// <summary>The external tenant GUID baked into this factory's <c>AzureAd:TenantId</c>.</summary>
    public const string DefaultTenantId = TestAzureAdEnvironment.DefaultTenantId;

    public const string DefaultClientId = TestAzureAdEnvironment.DefaultClientId;

    public const string DefaultAudience = TestAzureAdEnvironment.DefaultAudience;

    /// <summary>Matches the configured <c>AzureAd:Scopes</c> value (specs §16.7.2a).</summary>
    public const string DefaultScope = TestAzureAdEnvironment.DefaultScope;

    public readonly string DatabaseName = $"TodoApiTests-{Guid.NewGuid()}";

    /// <summary>Captures logged exceptions so test failures can report the real cause.</summary>
    public readonly ConcurrentQueue<string> LoggedErrors = new();

    private readonly bool _simulateRoleSourceUnavailable;

    public TodoApiFactory(bool simulateRoleSourceUnavailable = false)
    {
        // Must happen before this factory ever triggers host creation (CreateClient()/
        // Services) — see TestAzureAdEnvironment's doc comment for why this cannot be done
        // via ConfigureWebHost/ConfigureAppConfiguration instead.
        TestAzureAdEnvironment.EnsureConfigured();

        _simulateRoleSourceUnavailable = simulateRoleSourceUnavailable;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Passwordless by contract (specs §14): app config never carries a
                // credential. Never dialled — the DbContext registration below is
                // replaced with InMemory and the NpgsqlDataSource factory is never
                // resolved.
                ["ConnectionStrings:TodoDb"] =
                    "Host=localhost;Port=5432;Database=unused;Username=unused",
                ["Cors:AllowedOrigins:0"] = "http://localhost:5173",

                // AzureAd:* is deliberately NOT set here. AddTodoAuthentication reads it
                // eagerly in Program.cs's top-level statements, before this callback ever
                // runs — see TestAzureAdEnvironment, which supplies it via environment
                // variables instead (set from this factory's constructor).
            });
        });

        builder.ConfigureServices(services =>
        {
            // Purge every service descriptor related to TodoDbContext that the
            // app's own AddDbContext<TodoDbContext>(UseNpgsql) call registered.
            // Recent EF Core versions register more than just
            // DbContextOptions<TodoDbContext> (e.g. IDbContextOptionsConfiguration<TodoDbContext>),
            // so removing only the options descriptor leaves the Npgsql provider
            // configuration attached and causes a "two providers registered" error
            // when a second provider (InMemory) is added below.
            var descriptorsToRemove = services
                .Where(d =>
                    d.ServiceType == typeof(TodoDbContext) ||
                    (d.ServiceType.IsGenericType &&
                     d.ServiceType.GetGenericArguments().Contains(typeof(TodoDbContext))))
                .ToList();

            foreach (var descriptor in descriptorsToRemove)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<TodoDbContext>(options =>
                options.UseInMemoryDatabase(DatabaseName));

            // Test authentication scheme (specs §16.18 item 22). Registered AFTER
            // Program.cs's own AddAuthentication(JwtBearerDefaults.AuthenticationScheme),
            // so this call's AuthenticationOptions.DefaultScheme assignment wins: every
            // [Authorize]/fallback-policy check in the real app now authenticates via
            // TestAuthHandler instead of the real JwtBearer handler. The JwtBearer scheme
            // itself is left registered (and is still what boots/validates at startup) —
            // only the *default* scheme used by incoming requests changes.
            services
                .AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName,
                    _ => { });

            if (_simulateRoleSourceUnavailable)
            {
                // Simulates "Postgres unreachable during RoleClaimsTransformation"
                // (specs §16.18 item 13 / the tester's explicit role-source-unavailable
                // requirement) through the REAL middleware pipeline: the real
                // RoleClaimsTransformation still runs and must fail closed (503), not the
                // test double pretending to be the transformation.
                services.RemoveAll<IUserProvisioningService>();
                services.AddScoped<IUserProvisioningService, ThrowingUserProvisioningService>();
            }

            services.AddLogging(logging =>
                logging.AddProvider(new CapturingLoggerProvider(LoggedErrors)));
        });
    }

    /// <summary>
    /// Attaches a default <b>valid</b> test bearer token (matching tenant, a fresh <c>oid</c>,
    /// and <c>scp=Todos.ReadWrite</c>) to every client this factory creates. This is what lets
    /// the pre-existing <c>TodosControllerTests</c> — written before this cycle and calling
    /// only <c>_factory.CreateClient()</c> — keep passing unmodified now that every
    /// non-<c>/health</c> endpoint requires authentication (specs §16.18 item 21).
    /// </summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestToken.Valid().Encode());
    }

    /// <summary>A client presenting the given <paramref name="token"/> as its bearer token.</summary>
    public HttpClient CreateAuthenticatedClient(TestToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Encode());
        return client;
    }

    /// <summary>A client presenting the literal raw bearer value (e.g. a garbage/unparseable token).</summary>
    public HttpClient CreateClientWithRawBearerToken(string rawToken)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
        return client;
    }

    /// <summary>A client presenting no <c>Authorization</c> header at all.</summary>
    public HttpClient CreateUnauthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        return client;
    }

    /// <summary>
    /// Seeds a <c>users</c> row directly (bypassing HTTP/provisioning), so a test can assert
    /// role-based authorization (e.g. an <c>Admin</c> caller) without depending on the
    /// promotion endpoint or a real bootstrap.
    /// </summary>
    public async Task<AppUser> SeedUserAsync(
        Guid entraObjectId,
        UserRole role,
        string? email = null,
        string? displayName = null,
        CancellationToken ct = default)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TodoDbContext>();

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            EntraObjectId = entraObjectId,
            Email = email,
            DisplayName = displayName,
            Role = role,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }
}

/// <summary>Always throws, simulating an unreachable role store (specs §16.7.3/§16.10).</summary>
file sealed class ThrowingUserProvisioningService : IUserProvisioningService
{
    public Task<AppUser> GetOrCreateAsync(ProvisioningIdentity identity, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Simulated role-source (Postgres) outage for testing.");
}

file sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, sink);

    public void Dispose() { }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            sink.Enqueue($"[{logLevel}] {category}: {formatter(state, exception)}" +
                         (exception is null ? string.Empty : $"\n{exception}"));
        }
    }
}
