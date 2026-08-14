using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using TodoApi.Models;

namespace TodoApi.Auth;

/// <summary>
/// Wires end-user authentication (Entra External ID access tokens) and authorization
/// (roles from PostgreSQL) — specs §16.7.
///
/// <para>
/// JWT validation is delegated entirely to <c>Microsoft.Identity.Web</c> /
/// <c>JwtBearer</c>: signature checking, JWKS retrieval and caching, issuer/audience/
/// lifetime validation. Nothing about token validation is hand-rolled here, and no
/// validation is ever turned off.
/// </para>
/// </summary>
public static class AuthenticationRegistration
{
    /// <summary>Policy name for endpoints only an <see cref="UserRole.Admin"/> may call.</summary>
    public const string AdminOnlyPolicy = "AdminOnly";

    public static IServiceCollection AddTodoAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Fail fast: throws naming every missing key (§16.7.1).
        var authOptions = EntraAuthOptions.FromConfiguration(configuration);

        services.AddSingleton(Options.Create(authOptions));
        services.Configure<UserProfileOptions>(configuration.GetSection(UserProfileOptions.SectionName));

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(configuration.GetSection(EntraAuthOptions.SectionName));

        services.Configure<JwtBearerOptions>(
            JwtBearerDefaults.AuthenticationScheme,
            options => HardenTokenValidation(options, authOptions));

        return services;
    }

    public static IServiceCollection AddTodoAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization(options =>
        {
            // Secure by default: every endpoint requires an authenticated caller unless it
            // explicitly opts out with [AllowAnonymous]. The alternative — remembering to add
            // [Authorize] to each new controller — is the classic way auth silently regresses
            // (§16.7.3).
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            // RequireRole matches against the principal's RoleClaimType, which is exactly the
            // claim RoleClaimsTransformation writes from the database row.
            options.AddPolicy(AdminOnlyPolicy, policy => policy.RequireRole(nameof(UserRole.Admin)));
        });

        // Scoped: both depend on the request-scoped TodoDbContext.
        services.AddScoped<IUserProvisioningService, UserProvisioningService>();
        services.AddScoped<IClaimsTransformation, RoleClaimsTransformation>();

        return services;
    }

    /// <summary>
    /// Asserts the validation posture the spec requires (§16.7.2) instead of trusting
    /// defaults, so that "fixing" a local problem by disabling a check fails a test rather
    /// than shipping.
    ///
    /// <para>
    /// <b>TODO — requires live verification once the external tenant exists
    /// (spec §16.7.2 "Known External ID issuer gotcha", assumption A5).</b> External ID's
    /// configured authority uses the tenant <i>subdomain</i> host
    /// (<c>https://&lt;sub&gt;.ciamlogin.com/&lt;tenantId&gt;/v2.0</c>) while the token's
    /// <c>iss</c> uses the tenant <i>GUID</i> host
    /// (<c>https://&lt;tenantId&gt;.ciamlogin.com/&lt;tenantId&gt;/v2.0</c>). Both concrete
    /// issuer strings are added to <c>ValidIssuers</c> below — the spec-sanctioned remedy
    /// for <c>IDX10205</c>, applied pre-emptively because no real token can be decoded until
    /// a human bootstraps the tenant (§16.11). <c>ValidIssuers</c> is an <i>allow-list</i>:
    /// adding entries never weakens validation, and <c>ValidateIssuer</c> stays <c>true</c>.
    /// Disabling issuer validation is never an acceptable fix.
    /// </para>
    /// </summary>
    internal static void HardenTokenValidation(JwtBearerOptions options, EntraAuthOptions authOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authOptions);

        // OIDC metadata (and therefore the signing keys) is only ever fetched over TLS.
        options.RequireHttpsMetadata = true;

        // Keep the raw claim types (`oid`, `tid`, `scp`, `roles`) instead of the legacy
        // WS-Federation URIs, so behaviour does not depend on a global static.
        options.MapInboundClaims = false;

        var parameters = options.TokenValidationParameters;

        parameters.ValidateIssuer = true;
        parameters.ValidateAudience = true;
        parameters.ValidateLifetime = true;
        parameters.ValidateIssuerSigningKey = true;

        // Leave ClockSkew at its 5-minute default (§16.7.2).

        // Audience must be the API's own identifier. This is what makes an ID token — whose
        // audience is the SPA's client id — unusable against this API.
        // Only ever ADD to these allow-lists; anything the framework already derived from the
        // configured authority is left in place (removing an entry could only weaken or break
        // validation).
        parameters.ValidAudiences = Union(
            parameters.ValidAudiences,
            authOptions.Audience,
            authOptions.ClientId);

        parameters.ValidIssuers = Union(
            parameters.ValidIssuers,
            $"https://{authOptions.InstanceHost}/{authOptions.TenantId}/v2.0",
            $"https://{authOptions.TenantId}.ciamlogin.com/{authOptions.TenantId}/v2.0");
    }

    private static string[] Union(IEnumerable<string>? existing, params string?[] additional) =>
        (existing ?? [])
        .Concat(additional.Where(v => !string.IsNullOrWhiteSpace(v))!)
        .Select(v => v!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
