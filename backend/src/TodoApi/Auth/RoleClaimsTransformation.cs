using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using TodoApi.Models;

namespace TodoApi.Auth;

/// <summary>
/// <b>Entra proves who the caller is; PostgreSQL decides what they may do.</b>
///
/// <para>
/// Runs after token validation and before authorization, on every authenticated request
/// (specs §16.7.3). It re-derives the caller's role from the <c>users</c> table each time,
/// so a promotion or demotion takes effect on the <i>next request</i> — no re-login, no
/// waiting for a token to expire. The role is deliberately NOT carried in the token.
/// </para>
///
/// <para>
/// Registered <b>scoped</b> because it depends on the request-scoped
/// <see cref="IUserProvisioningService"/> (which holds the request-scoped DbContext).
/// </para>
///
/// <para>
/// Failure is always closed: a wrong-tenant or <c>oid</c>-less token, or an unreachable
/// database, yields a principal carrying <see cref="TodoClaimTypes.AuthFailure"/> and
/// <b>no</b> role claim; <see cref="AuthOutcomeMiddleware"/> converts that into 401/503
/// before any endpoint runs. There is no "assume User" path and categorically no
/// "assume Admin" path.
/// </para>
/// </summary>
public sealed class RoleClaimsTransformation : IClaimsTransformation
{
    private readonly IUserProvisioningService _provisioning;
    private readonly EntraAuthOptions _authOptions;
    private readonly UserProfileOptions _profileOptions;
    private readonly ILogger<RoleClaimsTransformation> _logger;

    public RoleClaimsTransformation(
        IUserProvisioningService provisioning,
        IOptions<EntraAuthOptions> authOptions,
        IOptions<UserProfileOptions> profileOptions,
        ILogger<RoleClaimsTransformation> logger)
    {
        _provisioning = provisioning ?? throw new ArgumentNullException(nameof(provisioning));
        _authOptions = (authOptions ?? throw new ArgumentNullException(nameof(authOptions))).Value;
        _profileOptions = (profileOptions ?? throw new ArgumentNullException(nameof(profileOptions))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // 1. Anonymous requests (e.g. /health) are left completely untouched.
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
        {
            return principal;
        }

        // 2. Idempotence guard: TransformAsync may be invoked more than once per request.
        //    Already-processed principals (success or failure) are returned as-is, so there
        //    is exactly one database round trip per request.
        if (principal.HasClaim(c => c.Type is TodoClaimTypes.RoleSource or TodoClaimTypes.AuthFailure))
        {
            return principal;
        }

        // 3. Defence in depth on top of the handler's issuer/audience validation: the token
        //    must come from the configured external tenant.
        var tenantId = ProfileClaimsReader.GetTenantId(principal);
        if (!string.Equals(tenantId, _authOptions.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Rejected an authenticated principal whose tid claim is missing or does not " +
                "match the configured external tenant.");

            return Reject(principal, identity, TodoClaimTypes.FailureInvalidPrincipal);
        }

        // 4. The identity key is `oid` and nothing else — never sub/email/preferred_username.
        if (!ProfileClaimsReader.TryGetObjectId(principal, out var objectId))
        {
            _logger.LogWarning(
                "Rejected an authenticated principal with a missing or unparseable oid claim.");

            return Reject(principal, identity, TodoClaimTypes.FailureInvalidPrincipal);
        }

        AppUser user;
        try
        {
            // 6. Get-or-create. Role on insert is always the server-side literal 'User'.
            user = await _provisioning.GetOrCreateAsync(
                new ProvisioningIdentity(
                    objectId,
                    ProfileClaimsReader.GetEmail(principal),
                    ProfileClaimsReader.GetDisplayName(principal),
                    ProfileClaimsReader.GetPhoneNumber(principal, _profileOptions.PhoneClaimName)),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail CLOSED. If the role store is unreachable we do not know what this caller
            // may do, so the request is refused (503) rather than defaulted.
            _logger.LogError(
                ex,
                "Could not resolve the role for user {Oid} from the database; failing closed.",
                objectId);

            return Reject(principal, identity, TodoClaimTypes.FailureRoleSourceUnavailable);
        }

        // 5 + 7 + 8. Clone without any inbound role claim, then add exactly one role claim
        //            whose value came from the database row.
        var transformed = CloneWithoutRoleClaims(principal, identity, out var primary);

        primary.AddClaim(new Claim(primary.RoleClaimType, user.Role.ToString()));
        primary.AddClaim(new Claim(TodoClaimTypes.RoleSource, TodoClaimTypes.RoleSourceDatabase));

        return transformed;
    }

    private static ClaimsPrincipal Reject(
        ClaimsPrincipal principal,
        ClaimsIdentity identity,
        string failureReason)
    {
        var rejected = CloneWithoutRoleClaims(principal, identity, out var primary);
        primary.AddClaim(new Claim(TodoClaimTypes.AuthFailure, failureReason));
        return rejected;
    }

    /// <summary>
    /// Builds a <b>new</b> principal (the validated identity is never mutated in place) in
    /// which every identity has been rebuilt without role claims.
    ///
    /// <para>
    /// This is the anti-escalation step (§16.7.3 step 5). Today the external tenant issues
    /// no role claims at all; stripping them anyway makes it structurally impossible for a
    /// future app-role assignment, a claims-mapping change, or a mis-scoped token to grant
    /// <c>Admin</c>. Every identity is cleaned, not just the primary one, because
    /// <c>IsInRole</c> consults them all.
    /// </para>
    /// </summary>
    private static ClaimsPrincipal CloneWithoutRoleClaims(
        ClaimsPrincipal principal,
        ClaimsIdentity primaryIdentity,
        out ClaimsIdentity clonedPrimary)
    {
        static ClaimsIdentity Clean(ClaimsIdentity source) => new(
            source.Claims.Where(c => !IsRoleClaim(c.Type, source.RoleClaimType)),
            source.AuthenticationType,
            source.NameClaimType,
            source.RoleClaimType);

        // The primary identity is added first so ClaimsPrincipal.Identity still resolves to
        // it (the default primary-identity selector takes the first identity).
        clonedPrimary = Clean(primaryIdentity);

        var result = new ClaimsPrincipal(clonedPrimary);

        foreach (var source in principal.Identities.Where(i => !ReferenceEquals(i, primaryIdentity)))
        {
            result.AddIdentity(Clean(source));
        }

        return result;
    }

    private static bool IsRoleClaim(string claimType, string identityRoleClaimType) =>
        string.Equals(claimType, "role", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(claimType, "roles", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(claimType, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(claimType, identityRoleClaimType, StringComparison.OrdinalIgnoreCase);
}
