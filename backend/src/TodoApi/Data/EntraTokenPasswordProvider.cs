using Azure.Core;
using Npgsql;

namespace TodoApi.Data;

/// <summary>
/// Supplies a fresh Entra (managed-identity) access token as the PostgreSQL password
/// at the exact moment Npgsql opens a physical connection. Wired via
/// <see cref="NpgsqlDataSourceBuilder.UsePasswordProvider"/> (spec §13.4).
///
/// <para>
/// Why this exists instead of <c>UsePeriodicPasswordProvider</c>: per Npgsql's own
/// documentation, the periodic provider's callback "is invoked in a timer, and not
/// when opening connections" — so the very first connection opened from a freshly
/// built data source (the app's startup <c>Migrate()</c> call) can race ahead of the
/// timer's first token fetch and find an empty password, producing
/// <c>"No password has been provided but the backend requires one (in cleartext)"</c>.
/// This was a live outage; see <c>.pipeline/deployment-lessons-learned.md</c> §5a.
/// <see cref="NpgsqlDataSourceBuilder.UsePasswordProvider"/> instead invokes the
/// callback synchronously on the connection-open path, so the token is guaranteed
/// present before the authentication handshake runs — closing the race.
/// </para>
///
/// <para>
/// <see cref="TokenCredential"/> caches tokens in-memory and returns the cached value
/// until it approaches expiry, so invoking this per physical connection open is cheap
/// and also handles the ~60-minute rotation without a separate background timer.
/// </para>
/// </summary>
internal sealed class EntraTokenPasswordProvider
{
    private readonly TokenCredential _credential;
    private readonly TokenRequestContext _tokenRequestContext;

    /// <param name="credential">
    /// The credential used to acquire the access token (e.g. <c>DefaultAzureCredential</c>
    /// in Azure; a fake in tests). Injected so token acquisition is testable without a
    /// live network call.
    /// </param>
    /// <param name="tokenScope">The Entra token scope for Azure Database for PostgreSQL.</param>
    public EntraTokenPasswordProvider(TokenCredential credential, string tokenScope)
    {
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        if (string.IsNullOrWhiteSpace(tokenScope))
        {
            throw new ArgumentException("Token scope must be provided.", nameof(tokenScope));
        }

        _tokenRequestContext = new TokenRequestContext(new[] { tokenScope });
    }

    /// <summary>
    /// Synchronous password callback for <see cref="NpgsqlConnection.Open()"/>.
    /// EF Core's synchronous <c>Migrate()</c> at startup takes this path, so it must
    /// return a real token — not defer to a background refresh.
    /// </summary>
    public string GetPassword(NpgsqlConnectionStringBuilder connectionStringBuilder)
        => _credential.GetToken(_tokenRequestContext, CancellationToken.None).Token;

    /// <summary>
    /// Asynchronous password callback for
    /// <see cref="NpgsqlConnection.OpenAsync(CancellationToken)"/> — used by
    /// request-time async EF Core operations.
    /// </summary>
    public async ValueTask<string> GetPasswordAsync(
        NpgsqlConnectionStringBuilder connectionStringBuilder,
        CancellationToken cancellationToken)
    {
        var token = await _credential
            .GetTokenAsync(_tokenRequestContext, cancellationToken)
            .ConfigureAwait(false);
        return token.Token;
    }
}
