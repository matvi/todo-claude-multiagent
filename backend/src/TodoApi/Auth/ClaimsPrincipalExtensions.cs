using System.Security.Claims;

namespace TodoApi.Auth;

/// <summary>
/// Convenience accessors for the two facts controllers are allowed to read off the
/// principal: <i>who</i> the caller is, and <i>whether the role on the principal really
/// came from the database</i>.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// The caller's Entra object id, or null. Guaranteed non-null for any request that
    /// reaches an endpoint (<see cref="RoleClaimsTransformation"/> plus
    /// <see cref="AuthOutcomeMiddleware"/> reject the alternatives), but callers still check
    /// rather than assume.
    /// </summary>
    public static Guid? GetEntraObjectId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return ProfileClaimsReader.TryGetObjectId(principal, out var objectId) ? objectId : null;
    }

    /// <summary>
    /// True when the role claim on this principal was written by
    /// <see cref="RoleClaimsTransformation"/> from the <c>users</c> table.
    /// </summary>
    public static bool HasDatabaseDerivedRole(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return principal.HasClaim(
            TodoClaimTypes.RoleSource,
            TodoClaimTypes.RoleSourceDatabase);
    }
}
