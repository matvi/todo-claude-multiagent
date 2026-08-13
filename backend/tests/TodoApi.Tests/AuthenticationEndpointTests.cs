using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using TodoApi.Auth;
using TodoApi.Models;
using Xunit;

namespace TodoApi.Tests;

/// <summary>
/// End-to-end tests of the authentication/authorization pipeline through the real,
/// unmodified ASP.NET Core pipeline (CORS → Authentication → AuthOutcome → Authorization →
/// endpoints), using <see cref="TodoApiFactory"/>'s test authentication scheme in place of a
/// live Entra tenant (specs §16.18 items 1–3, 30, 32, 33, plus the wrong-tenant/missing-oid
/// and anti-escalation paths).
/// </summary>
public class AuthenticationEndpointTests : IAsyncLifetime
{
    private TodoApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new TodoApiFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------
    // Item 1 — unauthenticated request to a protected endpoint
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetTodos_NoAuthorizationHeader_Returns401()
    {
        using var client = _factory.CreateUnauthenticatedClient();

        var response = await client.GetAsync("/api/todos");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetUsersMe_NoAuthorizationHeader_Returns401()
    {
        using var client = _factory.CreateUnauthenticatedClient();

        var response = await client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostTodos_NoAuthorizationHeader_Returns401()
    {
        using var client = _factory.CreateUnauthenticatedClient();

        var response = await client.PostAsync(
            "/api/todos",
            new StringContent("{\"title\":\"x\"}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -----------------------------------------------------------------
    // Item 2 / 33 — /health stays reachable regardless of the caller's token
    // -----------------------------------------------------------------

    [Fact]
    public async Task Health_NoAuthorizationHeader_Returns200()
    {
        using var client = _factory.CreateUnauthenticatedClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_GarbageBearerToken_StillReturns200()
    {
        using var client = _factory.CreateClientWithRawBearerToken("not-a-real-token-at-all");

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// specs §16.18 item 3, the other half: proves the test host <b>swapped</b> the default
    /// scheme rather than removing the real one — the production <c>Bearer</c>
    /// (<c>JwtBearer</c>) scheme this app boots with in Program.cs is still registered and
    /// backed by the real <see cref="JwtBearerHandler"/>; it is simply no longer the
    /// <i>default</i> scheme a bare <c>[Authorize]</c> resolves to in this test host.
    /// </summary>
    [Fact]
    public async Task RealJwtBearerScheme_IsStillRegistered_TestSchemeIsOnlyTheDefault()
    {
        using var scope = _factory.Services.CreateScope();
        var schemeProvider = scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>();

        var bearerScheme = await schemeProvider.GetSchemeAsync(JwtBearerDefaults.AuthenticationScheme);
        Assert.NotNull(bearerScheme);
        Assert.Equal(typeof(JwtBearerHandler), bearerScheme!.HandlerType);

        var defaultScheme = await schemeProvider.GetDefaultAuthenticateSchemeAsync();
        Assert.Equal(TestAuthHandler.SchemeName, defaultScheme!.Name);
    }

    [Fact]
    public async Task Health_TokenMarkedAsInvalidPrincipal_StillReturns200()
    {
        // Proves the AllowAnonymous skip in AuthOutcomeMiddleware, not just the absence of
        // a challenge from authorization — a caller presenting a token that WOULD be
        // rejected everywhere else must never be able to take the probe down.
        var token = new TestToken()
            .WithClaim("tid", TodoApiFactory.DefaultTenantId)
            .WithClaim("oid", Guid.NewGuid().ToString())
            .WithClaim("scp", TodoApiFactory.DefaultScope)
            .WithClaim(TodoClaimTypes.AuthFailure, TodoClaimTypes.FailureInvalidPrincipal);
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -----------------------------------------------------------------
    // Item 30 — missing/insufficient scope -> 403 on BOTH controllers
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetTodos_TokenWithoutScopeClaim_Returns403()
    {
        var token = TestToken.Valid(scope: null);
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.GetAsync("/api/todos");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetUsersMe_TokenWithoutScopeClaim_Returns403()
    {
        var token = TestToken.Valid(scope: null);
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetTodos_TokenWithWrongScope_Returns403()
    {
        var token = TestToken.Valid(scope: "SomeOther.Scope");
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.GetAsync("/api/todos");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PostTodos_TokenWithoutScopeClaim_Returns403()
    {
        var token = TestToken.Valid(scope: null);
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.PostAsync(
            "/api/todos",
            new StringContent("{\"title\":\"x\"}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetTodos_TokenWithCorrectScope_Returns200()
    {
        // Positive control for the two 403 tests above: proves the scope check is
        // discriminating (would reject the wrong thing) rather than always 403ing.
        var token = TestToken.Valid();
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.GetAsync("/api/todos");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -----------------------------------------------------------------
    // Wrong tenant / missing oid, exercised through the full pipeline
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetTodos_TokenFromWrongTenant_Returns401_WithWwwAuthenticateHeader()
    {
        var token = TestToken.Valid(tenantId: Guid.NewGuid().ToString());
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.GetAsync("/api/todos");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task GetTodos_TokenMissingOid_Returns401()
    {
        var token = TestToken.Valid().WithoutOid();
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.GetAsync("/api/todos");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -----------------------------------------------------------------
    // Item 32 — AuthOutcomeMiddleware's marker -> status mapping
    // -----------------------------------------------------------------

    [Fact]
    public async Task InvalidPrincipalMarker_Returns401_ProblemDetailsBody_NoClaimEchoed()
    {
        var oid = Guid.NewGuid();
        var token = new TestToken()
            .WithClaim("tid", TodoApiFactory.DefaultTenantId)
            .WithClaim("oid", oid.ToString())
            .WithClaim("scp", TodoApiFactory.DefaultScope)
            .WithClaim(TodoClaimTypes.AuthFailure, TodoClaimTypes.FailureInvalidPrincipal);
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.GetAsync("/api/todos");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer error=\"invalid_token\"", response.Headers.WwwAuthenticate.ToString());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(oid.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RoleSourceUnavailableMarker_Returns503_ProblemDetailsBody()
    {
        var token = new TestToken()
            .WithClaim("tid", TodoApiFactory.DefaultTenantId)
            .WithClaim("oid", Guid.NewGuid().ToString())
            .WithClaim("scp", TodoApiFactory.DefaultScope)
            .WithClaim(TodoClaimTypes.AuthFailure, TodoClaimTypes.FailureRoleSourceUnavailable);
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.GetAsync("/api/todos");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // -----------------------------------------------------------------
    // Role-source-unavailable via the REAL RoleClaimsTransformation + a throwing
    // provisioning service (fail-closed, not just the middleware mapping in isolation).
    // -----------------------------------------------------------------

    [Fact]
    public async Task RoleSourceUnavailable_ViaThrowingProvisioningService_Returns503()
    {
        using var throwingFactory = new TodoApiFactory(simulateRoleSourceUnavailable: true);
        using var client = throwingFactory.CreateClient(); // default valid token

        var response = await client.GetAsync("/api/todos");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task RoleSourceUnavailable_ViaThrowingProvisioningService_HealthStaysUp()
    {
        using var throwingFactory = new TodoApiFactory(simulateRoleSourceUnavailable: true);
        using var client = throwingFactory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RoleSourceUnavailable_ViaThrowingProvisioningService_DoesNotSilentlyGrantAdmin()
    {
        // Belt-and-braces: even an endpoint that ONLY an Admin may call must not be reached
        // when the role store is unavailable.
        using var throwingFactory = new TodoApiFactory(simulateRoleSourceUnavailable: true);
        using var client = throwingFactory.CreateClient();

        var response = await client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // -----------------------------------------------------------------
    // Anti-escalation through the full pipeline: a spoofed role claim in the token must
    // not grant Admin when the database says otherwise.
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetUsers_SpoofedAdminRoleClaimInToken_ButDbRowIsUser_Returns403()
    {
        var oid = Guid.NewGuid();
        var token = TestToken.Valid(oid: oid).WithSpoofedRole("Admin");
        using var client = _factory.CreateAuthenticatedClient(token);

        var response = await client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
