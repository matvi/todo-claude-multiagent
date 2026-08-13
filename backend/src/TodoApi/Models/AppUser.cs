namespace TodoApi.Models;

/// <summary>
/// A person who has signed in at least once, mapped to table <c>users</c> (specs §16.4.1).
///
/// <para>
/// Named <c>AppUser</c> rather than <c>User</c> so it never collides with
/// <c>ControllerBase.User</c> / <c>System.Security.Claims</c> in controllers.
/// </para>
///
/// <para>
/// <b>This row is the single source of truth for authorization.</b> Entra External ID
/// proves <i>who</i> the caller is (the <c>oid</c> claim); <see cref="Role"/> decides
/// <i>what they may do</i>. Nothing in a token ever sets it.
/// </para>
///
/// <para>
/// <b>PII warning (§16.10):</b> <see cref="Email"/>, <see cref="DisplayName"/> and
/// <see cref="PhoneNumber"/> are personal data. They are informational only — never an
/// authorization input — and must never be written to logs or telemetry dimensions.
/// Log <see cref="EntraObjectId"/> (a pseudonymous GUID) when a user must be identified.
/// </para>
/// </summary>
public class AppUser
{
    /// <summary>Internal surrogate key, generated in C#. Never exposed to Entra.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The token's <c>oid</c> claim — the Entra directory object id. Unique.
    ///
    /// <para>
    /// Deliberately <b>not</b> <c>sub</c>: <c>sub</c> is pairwise (a different value per
    /// application) so the same human would end up with several rows (§16.4.1).
    /// </para>
    /// </summary>
    public Guid EntraObjectId { get; set; }

    /// <summary>From the <c>email</c>/<c>preferred_username</c> claim. Informational only.</summary>
    public string? Email { get; set; }

    /// <summary>From <c>name</c>, else <c>given_name family_name</c>. Informational only.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// From the tenant-specific phone extension claim (§16.5.4) when present, otherwise
    /// user-supplied via <c>PUT /api/users/me</c>. Free text; <b>not</b> a verified number
    /// and <b>not</b> an MFA factor.
    /// </summary>
    public string? PhoneNumber { get; set; }

    /// <summary>The authorization role. Server-assigned only; always <c>User</c> on insert.</summary>
    public UserRole Role { get; set; } = UserRole.User;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
