using Microsoft.EntityFrameworkCore;
using TodoApi.Data;
using TodoApi.Models;

namespace TodoApi.Auth;

/// <summary>
/// Lazy, server-side get-or-create of the caller's <c>users</c> row (specs §16.7.5).
///
/// <para>
/// Provisioning happens inside the authentication path rather than at a
/// <c>POST /api/auth/provision</c> endpoint the client could skip: any authenticated
/// caller (curl, a second client, a returning user whose SPA call failed) can reach
/// <c>/api/todos</c>, so every endpoint would need a "row missing" fallback anyway. One
/// code path, unbypassable.
/// </para>
///
/// <para>
/// The row is written over the <b>existing</b> Entra/managed-identity
/// <c>NpgsqlDataSource</c> (§14) via the request-scoped <see cref="TodoDbContext"/> —
/// there is no second connection string and no second DB auth path.
/// </para>
/// </summary>
public sealed class UserProvisioningService : IUserProvisioningService
{
    /// <summary>
    /// Defensive cap on token-supplied profile strings. The columns are unbounded
    /// <c>text</c>, and a hostile or malfunctioning IdP claim should not be able to write
    /// an arbitrarily large value. Chosen to comfortably exceed any real name/email/phone.
    /// </summary>
    internal const int MaxProfileFieldLength = 256;

    private readonly TodoDbContext _db;
    private readonly ILogger<UserProvisioningService> _logger;

    public UserProvisioningService(TodoDbContext db, ILogger<UserProvisioningService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AppUser> GetOrCreateAsync(
        ProvisioningIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var existing = await FindAsync(identity.EntraObjectId, cancellationToken);
        if (existing is not null)
        {
            // A returning user's changed name is deliberately NOT re-synced here: that would
            // turn every authenticated request into a write. PUT /api/users/me is the update
            // path (§16.7.5).
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            EntraObjectId = identity.EntraObjectId,
            Email = Truncate(identity.Email),
            DisplayName = Truncate(identity.DisplayName),
            PhoneNumber = Truncate(identity.PhoneNumber),

            // ALWAYS the server-side literal. Never read from the request, the token or
            // configuration, and there is deliberately no "first user becomes Admin" rule —
            // that is a land-grab race. The first Admin is a one-time human UPDATE (§16.11).
            Role = UserRole.User,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Users.Add(user);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);

            // oid only — never the token, the email or the phone number (§16.7.5/§16.10).
            _logger.LogInformation(
                "Provisioned new user {Oid} with role User.",
                identity.EntraObjectId);

            return user;
        }
        catch (DbUpdateException ex)
        {
            // Concurrent first request on another replica won the unique index on
            // entra_object_id (Postgres 23505). Detach our losing entity, re-read, and use
            // the winner's row. Any other write failure has no row to find and rethrows, so
            // the caller still fails closed.
            _db.Entry(user).State = EntityState.Detached;

            var winner = await FindAsync(identity.EntraObjectId, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            _logger.LogInformation(
                ex,
                "Concurrent provisioning race for user {Oid}; using the row created by the " +
                "other request.",
                identity.EntraObjectId);

            return winner;
        }
    }

    private Task<AppUser?> FindAsync(Guid entraObjectId, CancellationToken cancellationToken) =>
        _db.Users.FirstOrDefaultAsync(u => u.EntraObjectId == entraObjectId, cancellationToken);

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= MaxProfileFieldLength
            ? trimmed
            : trimmed[..MaxProfileFieldLength];
    }
}
