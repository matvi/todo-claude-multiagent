using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Npgsql;
using TodoApi.Data;
using Xunit;

namespace TodoApi.Tests;

/// <summary>
/// Regression tests for the Entra (managed-identity) Postgres auth token seam
/// (specs §13.4; deployment-lessons-learned §5a).
///
/// <para>
/// WHY THESE EXIST — the bug they guard against: the original implementation used
/// Npgsql's <c>UsePeriodicPasswordProvider</c>, whose callback (per Npgsql's own docs)
/// "is invoked in a timer, and not when opening connections." The app's startup
/// <c>Migrate()</c> opened the very first connection immediately after building the
/// data source, racing ahead of that background timer's first token fetch, so Npgsql
/// found an empty password and threw
/// <c>"No password has been provided but the backend requires one (in cleartext)"</c>
/// — a live outage. The previous unit tests only checked branching/construction and
/// never asserted that a token is actually produced on the connection-open path, which
/// is exactly why the bug slipped through.
/// </para>
///
/// <para>
/// The fix moves to <see cref="NpgsqlDataSourceBuilder.UsePasswordProvider"/>, which
/// invokes <see cref="EntraTokenPasswordProvider"/> synchronously/asynchronously ON
/// connection open. These tests use a fake <see cref="TokenCredential"/> to prove a
/// real, non-empty token is returned by that seam without a background timer and
/// without a live network call.
/// </para>
///
/// <para>
/// STILL REQUIRES LIVE VERIFICATION (cannot be done here): opening a real connection
/// to the actual Entra-enabled Postgres Flexible Server and confirming the first-ever
/// connection authenticates. That end-to-end check is deferred to a human against the
/// deployed server — these tests prove the token is available at the moment Npgsql
/// asks for it (open time), which is the property that was previously violated.
/// </para>
/// </summary>
public class EntraTokenPasswordProviderTests
{
    private const string PostgresScope = "https://ossrdbms-aad.database.windows.net/.default";

    private const string PasswordlessEntraConnectionString =
        "Host=pg-todo-demo.postgres.database.azure.com;Port=5432;Database=tododb;" +
        "Username=todo-api;Ssl Mode=Require;Trust Server Certificate=true";

    /// <summary>
    /// A fake credential that records how it was invoked and returns a fixed token,
    /// so a test can prove a token was actually fetched (not left empty) and that the
    /// correct Postgres AAD scope was requested — all without any network I/O.
    /// </summary>
    private sealed class RecordingTokenCredential : TokenCredential
    {
        private readonly string _token;
        private readonly DateTimeOffset _expiresOn;

        public int GetTokenCallCount;
        public int GetTokenAsyncCallCount;
        public TokenRequestContext? LastRequestContext;

        public RecordingTokenCredential(string token)
        {
            _token = token;
            _expiresOn = DateTimeOffset.UtcNow.AddHours(1);
        }

        public override AccessToken GetToken(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref GetTokenCallCount);
            LastRequestContext = requestContext;
            return new AccessToken(_token, _expiresOn);
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref GetTokenAsyncCallCount);
            LastRequestContext = requestContext;
            return new ValueTask<AccessToken>(new AccessToken(_token, _expiresOn));
        }
    }

    // -----------------------------------------------------------------
    // The core property the old design lacked: a token is produced ON DEMAND
    // (the connection-open path), synchronously, not via a background timer.
    // -----------------------------------------------------------------

    [Fact]
    public void GetPassword_ReturnsFetchedTokenSynchronously_AndActuallyInvokesCredential()
    {
        var credential = new RecordingTokenCredential("access-token-sync");
        var provider = new EntraTokenPasswordProvider(credential, PostgresScope);

        // This is exactly what Npgsql calls during a synchronous Open() — the path
        // taken by the startup Migrate() that originally failed.
        var password = provider.GetPassword(new NpgsqlConnectionStringBuilder());

        Assert.Equal("access-token-sync", password);
        // The token was actually fetched at call time — the empty-password failure
        // mode is impossible here.
        Assert.Equal(1, credential.GetTokenCallCount);
    }

    [Fact]
    public async Task GetPasswordAsync_ReturnsFetchedToken_AndActuallyInvokesCredential()
    {
        var credential = new RecordingTokenCredential("access-token-async");
        var provider = new EntraTokenPasswordProvider(credential, PostgresScope);

        var password = await provider.GetPasswordAsync(
            new NpgsqlConnectionStringBuilder(), CancellationToken.None);

        Assert.Equal("access-token-async", password);
        Assert.Equal(1, credential.GetTokenAsyncCallCount);
    }

    [Fact]
    public void GetPassword_NeverReturnsNullOrEmpty()
    {
        // Directly asserts against the exact live failure mode: an empty password on
        // the first connection ("No password has been provided ... in cleartext").
        var credential = new RecordingTokenCredential("non-empty-token");
        var provider = new EntraTokenPasswordProvider(credential, PostgresScope);

        var password = provider.GetPassword(new NpgsqlConnectionStringBuilder());

        Assert.False(string.IsNullOrEmpty(password));
    }

    [Fact]
    public void GetPassword_RequestsTheAzurePostgresAadScope()
    {
        var credential = new RecordingTokenCredential("scoped-token");
        var provider = new EntraTokenPasswordProvider(credential, PostgresScope);

        provider.GetPassword(new NpgsqlConnectionStringBuilder());

        Assert.NotNull(credential.LastRequestContext);
        Assert.Contains(PostgresScope, credential.LastRequestContext!.Value.Scopes);
    }

    [Fact]
    public void Constructor_NullCredential_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new EntraTokenPasswordProvider(null!, PostgresScope));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankScope_Throws(string scope)
    {
        var credential = new RecordingTokenCredential("t");
        Assert.Throws<ArgumentException>(
            () => new EntraTokenPasswordProvider(credential, scope));
    }

    // -----------------------------------------------------------------
    // Data-source assembly with an injected credential: builds cleanly, and the token
    // is fetched lazily on open (not eagerly during Build) — matching UsePasswordProvider
    // semantics ("invoked when opening connections").
    // -----------------------------------------------------------------

    [Fact]
    public void BuildEntraAuthenticatedDataSource_WithInjectedCredential_BuildsWithoutThrowing()
    {
        var credential = new RecordingTokenCredential("build-token");

        NpgsqlDataSource? dataSource = null;
        var exception = Record.Exception(() =>
            dataSource = TodoDbContextRegistration.BuildEntraAuthenticatedDataSource(
                PasswordlessEntraConnectionString, credential));

        Assert.Null(exception);
        Assert.NotNull(dataSource);
        dataSource!.Dispose();
    }

    [Fact]
    public void BuildEntraAuthenticatedDataSource_DoesNotAcquireTokenUntilAConnectionIsOpened()
    {
        // The provider is wired via UsePasswordProvider, which invokes it only on
        // connection open. Building the data source must not, by itself, trigger a
        // token fetch — proving acquisition is tied to the open path (where it is
        // guaranteed present), not to any background/eager mechanism.
        var credential = new RecordingTokenCredential("lazy-token");

        using var dataSource = TodoDbContextRegistration.BuildEntraAuthenticatedDataSource(
            PasswordlessEntraConnectionString, credential);

        Assert.Equal(0, credential.GetTokenCallCount);
        Assert.Equal(0, credential.GetTokenAsyncCallCount);
    }
}
