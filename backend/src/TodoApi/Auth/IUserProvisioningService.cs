using TodoApi.Models;

namespace TodoApi.Auth;

/// <summary>
/// Profile data captured from a validated token on first sign-in (specs §16.7.4).
/// All fields except <see cref="EntraObjectId"/> are informational and may be null.
/// </summary>
/// <param name="EntraObjectId">The token's <c>oid</c> claim. The only identity key.</param>
public sealed record ProvisioningIdentity(
    Guid EntraObjectId,
    string? Email,
    string? DisplayName,
    string? PhoneNumber);

/// <summary>
/// Resolves the <c>users</c> row for an authenticated caller, creating it on first
/// sign-in (specs §16.7.5).
///
/// <para>
/// Abstracted so <see cref="RoleClaimsTransformation"/> — the security-critical part —
/// can be unit-tested against a stub without a database, and so the storage concern stays
/// out of the claims-transformation class (single responsibility).
/// </para>
/// </summary>
public interface IUserProvisioningService
{
    /// <summary>
    /// Returns the existing row for <paramref name="identity"/>, or creates one with role
    /// <see cref="UserRole.User"/>.
    ///
    /// <para>
    /// Implementations must never derive the role from the request, the token or
    /// configuration, and must throw (never return a default) when the store is
    /// unavailable — the caller fails closed.
    /// </para>
    /// </summary>
    Task<AppUser> GetOrCreateAsync(ProvisioningIdentity identity, CancellationToken cancellationToken);
}
