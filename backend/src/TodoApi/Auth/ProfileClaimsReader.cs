using System.Security.Claims;

namespace TodoApi.Auth;

/// <summary>
/// Pure, side-effect-free extraction of identity and profile data from a validated
/// principal (specs §16.7.4). No I/O, no ambient state — directly unit-testable.
///
/// <para>
/// Both the short JWT claim names (<c>oid</c>, <c>tid</c>, <c>email</c>) and the long
/// WS-Federation URIs are accepted, because whether the JWT handler maps inbound claims
/// depends on host configuration and must not silently change behaviour here.
/// </para>
///
/// <para>
/// <b>Nothing this class returns may influence authorization.</b> The identity key
/// (<c>oid</c>) selects the database row; the role comes from that row and from nowhere
/// else. Email/name/phone are informational profile data only.
/// </para>
/// </summary>
public static class ProfileClaimsReader
{
    public const string ObjectIdClaim = "oid";
    public const string ObjectIdUriClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    public const string TenantIdClaim = "tid";
    public const string TenantIdUriClaim = "http://schemas.microsoft.com/identity/claims/tenantid";

    /// <summary>Suffix of the tenant-specific phone extension claim (§16.5.4).</summary>
    public const string PhoneClaimSuffix = "_phoneNumber";

    /// <summary>
    /// The stable directory object id — the join key between Entra and the
    /// <c>users</c> table.
    ///
    /// <para>
    /// There is <b>no fallback</b> to <c>sub</c>, <c>email</c> or
    /// <c>preferred_username</c>: <c>sub</c> is pairwise (one human ⇒ several ids across
    /// clients) and the other two are mutable, user-controlled and re-assignable. A
    /// validated token without a usable <c>oid</c> is a misconfiguration and must be
    /// rejected (§16.4.1).
    /// </para>
    /// </summary>
    public static bool TryGetObjectId(ClaimsPrincipal principal, out Guid objectId)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var raw = FindFirst(principal, ObjectIdClaim, ObjectIdUriClaim);
        return Guid.TryParse(raw, out objectId);
    }

    /// <summary>The token's <c>tid</c> claim, or null when absent.</summary>
    public static string? GetTenantId(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return FindFirst(principal, TenantIdClaim, TenantIdUriClaim);
    }

    /// <summary><c>email</c>, else <c>preferred_username</c>/<c>upn</c> when it looks like an email.</summary>
    public static string? GetEmail(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var email = FindFirst(principal, "email", ClaimTypes.Email);
        if (!string.IsNullOrWhiteSpace(email))
        {
            return email.Trim();
        }

        var candidate = FindFirst(principal, "preferred_username", ClaimTypes.Upn);
        return LooksLikeEmail(candidate) ? candidate!.Trim() : null;
    }

    /// <summary><c>name</c>, else <c>"{given_name} {family_name}"</c>, else null.</summary>
    public static string? GetDisplayName(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var name = FindFirst(principal, "name", ClaimTypes.Name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        var given = FindFirst(principal, "given_name", ClaimTypes.GivenName);
        var family = FindFirst(principal, "family_name", ClaimTypes.Surname);
        var composed = $"{given} {family}".Trim();

        return string.IsNullOrWhiteSpace(composed) ? null : composed;
    }

    /// <summary>
    /// The phone number, resolved by <paramref name="configuredClaimName"/> when supplied,
    /// otherwise by the first claim whose type ends with <c>_phoneNumber</c> — which
    /// survives the tenant-specific <c>extension_&lt;appId&gt;_phoneNumber</c> naming
    /// without hard-coding a GUID (§16.5.4/§16.7.4).
    ///
    /// <para>Absent ⇒ null. That is normal, not an error, and must never fail a request.</para>
    /// </summary>
    public static string? GetPhoneNumber(ClaimsPrincipal principal, string? configuredClaimName)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (!string.IsNullOrWhiteSpace(configuredClaimName))
        {
            var configured = principal.FindFirst(c =>
                string.Equals(c.Type, configuredClaimName, StringComparison.Ordinal))?.Value;

            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }
        }

        var bySuffix = principal.FindFirst(c =>
            c.Type.EndsWith(PhoneClaimSuffix, StringComparison.OrdinalIgnoreCase))?.Value;

        return string.IsNullOrWhiteSpace(bySuffix) ? null : bySuffix.Trim();
    }

    private static string? FindFirst(ClaimsPrincipal principal, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = principal.FindFirst(c =>
                string.Equals(c.Type, claimType, StringComparison.OrdinalIgnoreCase))?.Value;

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool LooksLikeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var at = value.IndexOf('@');
        return at > 0 &&
               at < value.Length - 1 &&
               value.IndexOf('@', at + 1) < 0 &&
               !value.Any(char.IsWhiteSpace);
    }
}
