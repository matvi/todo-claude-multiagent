using Microsoft.Extensions.Configuration;

namespace TodoApi.Auth;

/// <summary>
/// The <c>AzureAd</c> configuration section (specs §16.7.1). Every value here is a
/// <b>public identifier</b> — tenant id, client id, authority URL, scope name — never a
/// credential, so all of them are plain environment variables. This application has
/// <b>zero</b> runtime secrets (§16.6(3)); if anyone is about to move one of these into a
/// Container Apps secret or Key Vault, the design has been misunderstood.
/// </summary>
public sealed class EntraAuthOptions
{
    public const string SectionName = "AzureAd";

    /// <summary>Authority host, e.g. <c>https://&lt;tenant-subdomain&gt;.ciamlogin.com/</c>.</summary>
    public string Instance { get; set; } = string.Empty;

    /// <summary>The external (CIAM) tenant's GUID. Also asserted per request as <c>tid</c>.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The <c>todo-api-app</c> registration's client id.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Expected token audience, e.g. <c>api://&lt;api-client-id&gt;</c>.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Delegated scope required on every non-anonymous endpoint, e.g. <c>Todos.ReadWrite</c>.</summary>
    public string Scopes { get; set; } = string.Empty;

    /// <summary>
    /// Binds and <b>validates</b> the section, throwing an <see cref="InvalidOperationException"/>
    /// that names every missing key.
    ///
    /// <para>
    /// This is deliberate fail-fast startup behaviour (§16.7.1): an API that boots without
    /// auth configuration is an API that serves unauthenticated traffic, which is exactly
    /// the failure mode this cycle exists to remove. It is the same philosophy as §14.5's
    /// connection-string guard.
    /// </para>
    /// </summary>
    public static EntraAuthOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var options = new EntraAuthOptions();
        section.Bind(options);

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Instance)) missing.Add($"{SectionName}:Instance");
        if (string.IsNullOrWhiteSpace(options.TenantId)) missing.Add($"{SectionName}:TenantId");
        if (string.IsNullOrWhiteSpace(options.ClientId)) missing.Add($"{SectionName}:ClientId");
        if (string.IsNullOrWhiteSpace(options.Audience)) missing.Add($"{SectionName}:Audience");

        // Scopes is part of the authoritative five-key fail-fast list (§16.7.1). It is not a
        // token-validation parameter, but it is the value [RequiredScope] binds to, so a
        // blank value would silently disable the scope check on every controller — an
        // invisible security downgrade (§16.7.2a).
        if (string.IsNullOrWhiteSpace(options.Scopes)) missing.Add($"{SectionName}:Scopes");

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Entra External ID authentication is not configured. Missing or blank " +
                $"configuration key(s): {string.Join(", ", missing)}. Set them as environment " +
                "variables (e.g. AzureAd__TenantId). These are public identifiers, not secrets " +
                "(specs §16.7.1).");
        }

        if (!Uri.TryCreate(options.Instance, UriKind.Absolute, out var instanceUri) ||
            !string.Equals(instanceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Instance must be an absolute https URL (e.g. " +
                "https://<tenant-subdomain>.ciamlogin.com/). Token metadata is always fetched " +
                "over TLS (specs §16.10).");
        }

        if (!Guid.TryParse(options.TenantId, out _))
        {
            throw new InvalidOperationException(
                $"{SectionName}:TenantId must be the external tenant's GUID.");
        }

        return options;
    }

    /// <summary>
    /// The host of <see cref="Instance"/>, e.g. <c>&lt;subdomain&gt;.ciamlogin.com</c>.
    /// Used to build the concrete issuer strings in <see cref="AuthenticationRegistration"/>.
    /// </summary>
    public string InstanceHost => new Uri(Instance, UriKind.Absolute).Host;
}
