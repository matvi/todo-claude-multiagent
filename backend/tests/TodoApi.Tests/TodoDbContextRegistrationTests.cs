using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TodoApi.Data;
using Xunit;

namespace TodoApi.Tests;

/// <summary>
/// Unit tests for TodoApi.Data.TodoDbContextRegistration (specs.md §14).
///
/// <para>
/// WHAT CHANGED AND WHY THESE TESTS WERE REWRITTEN: §14 removes the
/// <c>Postgres:UseEntraAuth</c> flag and the password-auth branch entirely — the app now
/// authenticates to PostgreSQL with Entra / managed identity unconditionally. The previous
/// tests asserted the deleted behaviour (that an unset/false flag produced a password-backed
/// DbContext whose connection string still contained <c>Password=</c>), so they encode a
/// contract that no longer exists and had to be replaced rather than extended.
/// </para>
///
/// <para>
/// REGRESSION UNDER TEST (§14.1a): handing the Entra builder a production-shaped SQL-auth
/// connection string used to throw
/// <c>NotSupportedException: When registering a password provider, a password or password
/// file may not be set</c>. Normalization now strips both <c>Password</c> and
/// <c>Passfile</c>, making that crash structurally impossible.
/// </para>
///
/// <para>
/// SCOPE LIMIT: everything here runs with no Azure, no token and no database. Real Entra
/// token acquisition, the Postgres AAD handshake, the in-DB grants and the local
/// <c>trust</c>-auth container are NOT verifiable here — see specs §14.6 / §14.9.
/// </para>
/// </summary>
public class TodoDbContextRegistrationTests
{
    /// <summary>The exact live production shape from specs §14.10 test 1 (password redacted).</summary>
    private const string ProductionPasswordConnectionString =
        "Host=pg-todo-demo-cus01.postgres.database.azure.com;Port=5432;Database=tododb;" +
        "Username=todoadmin;Password=REDACTED;Ssl Mode=Require;Trust Server Certificate=true";

    private const string PasswordlessEntraConnectionString =
        "Host=pg-todo-demo-cus01.postgres.database.azure.com;Port=5432;Database=tododb;" +
        "Username=todo-api;Ssl Mode=Require;Trust Server Certificate=true";

    private const string LocalDevConnectionString =
        "Host=localhost;Port=5432;Database=tododb;Username=todo;Ssl Mode=Disable";

    private static IConfiguration BuildConfig(
        string? connectionString,
        IDictionary<string, string?>? extraValues = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:TodoDb"] = connectionString,
        };

        if (extraValues is not null)
        {
            foreach (var (key, value) in extraValues)
            {
                values[key] = value;
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static NpgsqlConnectionStringBuilder Reparse(string connectionString)
        => new(connectionString);

    /// <summary>Fixed-token credential; proves no network/Entra dependency in these tests.</summary>
    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext ctx, CancellationToken ct)
            => new("stub-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext ctx, CancellationToken ct)
            => new(new AccessToken("stub-token", DateTimeOffset.UtcNow.AddHours(1)));
    }

    // =================================================================
    // 1-2. Regression for the live NotSupportedException (§14.1a / §14.10).
    // =================================================================

    [Fact]
    public void BuildEntraAuthenticatedDataSource_WithProductionPasswordConnectionString_DoesNotThrow()
    {
        NpgsqlDataSource? dataSource = null;

        var exception = Record.Exception(() =>
            dataSource = TodoDbContextRegistration.BuildEntraAuthenticatedDataSource(
                ProductionPasswordConnectionString, new StubTokenCredential()));

        // Before §14 this threw NotSupportedException from PrepareConfiguration().
        Assert.Null(exception);
        Assert.NotNull(dataSource);
        dataSource!.Dispose();
    }

    [Fact]
    public void AddTodoDbContext_WithPasswordBearingConnectionString_ResolvesDataSourceAndContext()
    {
        var services = new ServiceCollection();
        services.AddTodoDbContext(BuildConfig(ProductionPasswordConnectionString));

        using var provider = services.BuildServiceProvider();

        var dataSourceException = Record.Exception(() => provider.GetRequiredService<NpgsqlDataSource>());
        Assert.Null(dataSourceException);

        var contextException = Record.Exception(() => provider.GetRequiredService<TodoDbContext>());
        Assert.Null(contextException);
    }

    // =================================================================
    // 3-5. Static credential stripping (assert by re-parsing, never string-matching).
    // =================================================================

    [Theory]
    [InlineData("Host=db.example.com;Database=tododb;Username=todo-api;Password=hunter2")]
    [InlineData("Host=db.example.com;Database=tododb;Username=todo-api;pwd=hunter2")]
    [InlineData("Host=db.example.com;Database=tododb;Username=todo-api;PASSWORD=hunter2")]
    public void BuildEntraConnectionString_StripsPasswordAndItsAliases(string input)
    {
        var normalized = TodoDbContextRegistration.BuildEntraConnectionString(input);

        Assert.True(string.IsNullOrEmpty(Reparse(normalized.ConnectionString).Password));
        Assert.True(normalized.StaticCredentialRemoved);
    }

    [Fact]
    public void BuildEntraConnectionString_StripsPassfile()
    {
        var normalized = TodoDbContextRegistration.BuildEntraConnectionString(
            "Host=db.example.com;Database=tododb;Username=todo-api;Passfile=/etc/pgpass");

        Assert.True(string.IsNullOrEmpty(Reparse(normalized.ConnectionString).Passfile));
        Assert.True(normalized.StaticCredentialRemoved);
    }

    [Fact]
    public void BuildEntraConnectionString_PasswordlessInput_ReportsNoStaticCredential()
    {
        var normalized = TodoDbContextRegistration.BuildEntraConnectionString(
            PasswordlessEntraConnectionString);

        Assert.False(normalized.StaticCredentialRemoved);
    }

    // =================================================================
    // 6. Missing Username → fail fast.
    // =================================================================

    [Theory]
    [InlineData("Host=db.example.com;Database=tododb;Ssl Mode=Require")]
    [InlineData("Host=db.example.com;Database=tododb;Username=;Ssl Mode=Require")]
    [InlineData("Host=db.example.com;Database=tododb;Username=   ;Ssl Mode=Require")]
    public void BuildEntraConnectionString_MissingUsername_Throws(string input)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => TodoDbContextRegistration.BuildEntraConnectionString(input));

        Assert.Contains("ConnectionStrings:TodoDb", exception.Message);
        Assert.Contains("Username", exception.Message);
    }

    [Fact]
    public void BuildEntraConnectionString_UnparseableInput_ThrowsNamingTheConfigKey()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => TodoDbContextRegistration.BuildEntraConnectionString("Host=db;NotARealKeyword=1"));

        Assert.Contains("ConnectionStrings:TodoDb", exception.Message);
    }

    // =================================================================
    // 7. Everything else survives the round-trip verbatim.
    // =================================================================

    [Fact]
    public void BuildEntraConnectionString_PreservesAllOtherKeywords()
    {
        const string input =
            "Host=pg-todo-demo-cus01.postgres.database.azure.com;Port=6432;Database=tododb;" +
            "Username=todo-api;Password=REDACTED;Ssl Mode=Require;Trust Server Certificate=true;" +
            "Command Timeout=45;Maximum Pool Size=27";

        var normalized = TodoDbContextRegistration.BuildEntraConnectionString(input);
        var result = Reparse(normalized.ConnectionString);

        Assert.Equal("pg-todo-demo-cus01.postgres.database.azure.com", result.Host);
        Assert.Equal(6432, result.Port);
        Assert.Equal("tododb", result.Database);
        Assert.Equal("todo-api", result.Username);
        // Read via the indexer: the strongly-typed property is obsolete in Npgsql 10,
        // but the keyword must still survive the round-trip verbatim.
        Assert.True((bool)result["Trust Server Certificate"]!);
        Assert.Equal(45, result.CommandTimeout);
        Assert.Equal(27, result.MaxPoolSize);
    }

    // =================================================================
    // 8-10. SslMode: forced for remote hosts, untouched for loopback.
    // =================================================================

    [Theory]
    [InlineData("Disable")]
    [InlineData("Allow")]
    public void BuildEntraConnectionString_RemoteHostWithWeakSsl_ForcesRequire(string sslMode)
    {
        var normalized = TodoDbContextRegistration.BuildEntraConnectionString(
            $"Host=db.example.com;Database=tododb;Username=todo-api;Ssl Mode={sslMode}");

        Assert.Equal(SslMode.Require, Reparse(normalized.ConnectionString).SslMode);
        Assert.NotNull(normalized.SslModeForcedFrom);
    }

    [Theory]
    [InlineData("Ssl Mode=Require", SslMode.Require)]
    [InlineData("Ssl Mode=VerifyFull", SslMode.VerifyFull)]
    [InlineData("Ssl Mode=VerifyCA", SslMode.VerifyCA)]
    [InlineData("Ssl Mode=Prefer", SslMode.Prefer)]
    public void BuildEntraConnectionString_RemoteHostWithAdequateSsl_LeavesItAlone(
        string sslFragment, SslMode expected)
    {
        var normalized = TodoDbContextRegistration.BuildEntraConnectionString(
            $"Host=db.example.com;Database=tododb;Username=todo-api;{sslFragment}");

        Assert.Equal(expected, Reparse(normalized.ConnectionString).SslMode);
        Assert.Null(normalized.SslModeForcedFrom);
    }

    [Fact]
    public void BuildEntraConnectionString_RemoteHostWithOmittedSsl_LeavesTheNpgsqlDefault()
    {
        var normalized = TodoDbContextRegistration.BuildEntraConnectionString(
            "Host=db.example.com;Database=tododb;Username=todo-api");

        Assert.Null(normalized.SslModeForcedFrom);
        Assert.Equal(
            new NpgsqlConnectionStringBuilder().SslMode,
            Reparse(normalized.ConnectionString).SslMode);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void BuildEntraConnectionString_LoopbackHostWithSslDisabled_StaysDisabled(string host)
    {
        // The local-dev contract from §14.6: the Docker Postgres serves no TLS.
        var normalized = TodoDbContextRegistration.BuildEntraConnectionString(
            $"Host={host};Port=5432;Database=tododb;Username=todo;Ssl Mode=Disable");

        Assert.Equal(SslMode.Disable, Reparse(normalized.ConnectionString).SslMode);
        Assert.Null(normalized.SslModeForcedFrom);
    }

    // =================================================================
    // 11. Idempotence.
    // =================================================================

    [Theory]
    [InlineData(PasswordlessEntraConnectionString)]
    [InlineData(LocalDevConnectionString)]
    public void BuildEntraConnectionString_IsIdempotent(string alreadyNormalized)
    {
        var once = TodoDbContextRegistration.BuildEntraConnectionString(alreadyNormalized);
        var twice = TodoDbContextRegistration.BuildEntraConnectionString(once.ConnectionString);

        Assert.Equal(once.ConnectionString, twice.ConnectionString);
        Assert.False(twice.StaticCredentialRemoved);
        Assert.Null(twice.SslModeForcedFrom);
    }

    // =================================================================
    // 12-13. The password branch and its flag are gone.
    // =================================================================

    [Fact]
    public void AddTodoDbContext_RegistersExactlyOneDataSourceSingleton_BackingTheDbContext()
    {
        var services = new ServiceCollection();
        services.AddTodoDbContext(BuildConfig(PasswordlessEntraConnectionString));

        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(NpgsqlDataSource)));

        using var provider = services.BuildServiceProvider();

        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();
        Assert.NotNull(dataSource);
        // Singleton: the DbContext must be built over the same shared data source.
        Assert.Same(dataSource, provider.GetRequiredService<NpgsqlDataSource>());

        var context = provider.GetRequiredService<TodoDbContext>();
        Assert.Equal(dataSource.ConnectionString, context.Database.GetConnectionString());
    }

    [Fact]
    public void AddTodoDbContext_RegistrationIsLazy_NoDataSourceBuiltUntilResolved()
    {
        // TodoApiFactory relies on this: it injects a dummy connection string, then removes
        // every TodoDbContext descriptor, and the NpgsqlDataSource factory is never invoked.
        var services = new ServiceCollection();

        var exception = Record.Exception(
            () => services.AddTodoDbContext(BuildConfig(PasswordlessEntraConnectionString)));

        Assert.Null(exception);
        Assert.All(
            services.Where(d => d.ServiceType == typeof(NpgsqlDataSource)),
            d => Assert.Null(d.ImplementationInstance));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void AddTodoDbContext_LegacyUseEntraAuthFlag_HasNoEffect(string flagValue)
    {
        // The flag is dead config: present or absent, true or false, the Entra data source
        // is registered. Proves it was deleted, not merely defaulted the other way.
        var services = new ServiceCollection();
        services.AddTodoDbContext(BuildConfig(
            PasswordlessEntraConnectionString,
            new Dictionary<string, string?> { ["Postgres:UseEntraAuth"] = flagValue }));

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<NpgsqlDataSource>());
        Assert.NotNull(provider.GetRequiredService<TodoDbContext>());
    }

    // =================================================================
    // Startup logging (§14.5) — diagnosable, and never leaks a credential.
    // =================================================================

    [Fact]
    public void BuildEntraAuthenticatedDataSource_LogsPrincipalAndWarnings_WithoutLeakingSecrets()
    {
        var logger = new RecordingLogger();

        using var dataSource = TodoDbContextRegistration.BuildEntraAuthenticatedDataSource(
            "Host=db.example.com;Port=5432;Database=tododb;Username=todo-api;" +
            "Password=REDACTED;Ssl Mode=Disable",
            new StubTokenCredential(),
            logger);

        var info = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Contains("Postgres auth: Entra / managed identity", info.Message);
        Assert.Contains("db.example.com", info.Message);
        Assert.Contains("tododb", info.Message);
        Assert.Contains("todo-api", info.Message);

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains("password/passfile", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, w => w.Message.Contains("SslMode", StringComparison.OrdinalIgnoreCase));

        // Nothing logged may contain the credential.
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("REDACTED"));
    }

    [Fact]
    public void BuildEntraAuthenticatedDataSource_CleanConnectionString_LogsInformationOnly()
    {
        var logger = new RecordingLogger();

        using var dataSource = TodoDbContextRegistration.BuildEntraAuthenticatedDataSource(
            PasswordlessEntraConnectionString, new StubTokenCredential(), logger);

        Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, logger.Entries[0].Level);
    }

    [Fact]
    public void BuildEntraAuthenticatedDataSource_NullLogger_DoesNotThrow()
    {
        var exception = Record.Exception(() =>
            TodoDbContextRegistration
                .BuildEntraAuthenticatedDataSource(
                    PasswordlessEntraConnectionString, new StubTokenCredential(), logger: null)
                .Dispose());

        Assert.Null(exception);
    }

    // =================================================================
    // 14. Existing connection-string guard is unchanged.
    // =================================================================

    [Fact]
    public void AddTodoDbContext_MissingConnectionString_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddTodoDbContext(BuildConfig(connectionString: null)));

        Assert.Contains("TodoDb", exception.Message);
    }

    [Fact]
    public void AddTodoDbContext_BlankConnectionString_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(
            () => services.AddTodoDbContext(BuildConfig(connectionString: "   ")));
    }

    /// <summary>Minimal in-memory ILogger so log assertions need no framework plumbing.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
