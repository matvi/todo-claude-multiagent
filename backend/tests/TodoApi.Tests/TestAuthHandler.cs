using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TodoApi.Tests;

/// <summary>
/// A test-only authentication handler that stands in for the real <c>JwtBearer</c> scheme
/// (specs §16.18 item 22). It never validates a signature and never talks to any IdP — a
/// test controls the exact claims a simulated token carries by encoding them into the
/// <c>Authorization</c> header, so the real, unmodified
/// <see cref="TodoApi.Auth.RoleClaimsTransformation"/>, <see cref="TodoApi.Auth.AuthOutcomeMiddleware"/>
/// and <c>[RequiredScope]</c> enforcement can be exercised end to end against a principal
/// this test double built.
///
/// <para>Behaviour mirrors the three cases the app's authentication pipeline must handle:</para>
/// <list type="bullet">
/// <item><b>No <c>Authorization</c> header</b> → <see cref="AuthenticateResult.NoResult"/>
/// (unauthenticated, exactly like a real bearer scheme with nothing to validate).</item>
/// <item><b>An <c>Authorization: Bearer &lt;value&gt;</c> header that is not a
/// <see cref="TestToken"/>-encoded claim set</b> (e.g. <c>"garbage-token"</c>) →
/// <see cref="AuthenticateResult.Fail(string)"/> — mirrors a real handler rejecting a
/// malformed/unsigned/garbage bearer value outright. It is never silently accepted.</item>
/// <item><b>A <see cref="TestToken"/>-encoded value</b> → a <see cref="ClaimsPrincipal"/>
/// carrying exactly the claims the test asked for, nothing more and nothing less.</item>
/// </list>
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var values) || values.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var header = values.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.Fail("Unsupported authentication scheme."));
        }

        var raw = header[prefix.Length..];

        List<TestClaim>? claims;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(raw));
            claims = JsonSerializer.Deserialize<List<TestClaim>>(json);
        }
        catch
        {
            // Mirrors a real bearer scheme rejecting a malformed/garbage token outright:
            // never authenticated, never silently accepted as "no header".
            return Task.FromResult(AuthenticateResult.Fail("Malformed test bearer token."));
        }

        if (claims is null || claims.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.Fail("Empty test bearer token."));
        }

        var identity = new ClaimsIdentity(
            claims.Select(c => new Claim(c.Type, c.Value)),
            authenticationType: SchemeName,
            nameType: "name",
            roleType: TestToken.RoleClaimType);

        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>Wire shape for a single claim in a <see cref="TestToken"/>-encoded header value.</summary>
public sealed record TestClaim(string Type, string Value);
