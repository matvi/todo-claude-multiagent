namespace TodoApi.Auth;

/// <summary>
/// The <c>Users</c> configuration section (specs §16.7.1/§16.7.4).
/// </summary>
public sealed class UserProfileOptions
{
    public const string SectionName = "Users";

    /// <summary>
    /// Exact name of the token claim carrying the phone number, e.g.
    /// <c>extension_&lt;b2cExtensionsAppId-without-hyphens&gt;_phoneNumber</c>.
    ///
    /// <para>
    /// Optional, and deliberately configuration rather than a constant: External ID has no
    /// built-in phone attribute, so phone is a custom directory-extension attribute whose
    /// claim name embeds a <b>tenant-specific</b> app id that cannot exist until the tenant
    /// does (§16.5.4). When it is blank, <see cref="ProfileClaimsReader"/> falls back to the
    /// first claim whose type ends with <c>_phoneNumber</c>, so no GUID is ever hard-coded.
    /// </para>
    /// </summary>
    public string? PhoneClaimName { get; set; }
}
