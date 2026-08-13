using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TodoApi.Auth;
using TodoApi.Models;
using Xunit;

namespace TodoApi.Tests;

/// <summary>
/// Pure unit tests of <see cref="RoleClaimsTransformation"/> against a faked
/// <see cref="IUserProvisioningService"/> — no host, no database, no HTTP (specs §16.18
/// items 6–13). This is the security-critical class: "Entra proves who the caller is;
/// PostgreSQL decides what they may do."
/// </summary>
public class RoleClaimsTransformationTests
{
    private const string DefaultTenantId = "11111111-1111-1111-1111-111111111111";

    private static RoleClaimsTransformation CreateSut(
        IUserProvisioningService provisioning,
        string? tenantId = null,
        string? phoneClaimName = null)
    {
        var authOptions = Options.Create(new EntraAuthOptions
        {
            Instance = "https://test-tenant.ciamlogin.com/",
            TenantId = tenantId ?? DefaultTenantId,
            ClientId = "22222222-2222-2222-2222-222222222222",
            Audience = "api://22222222-2222-2222-2222-222222222222",
            Scopes = "Todos.ReadWrite",
        });
        var profileOptions = Options.Create(new UserProfileOptions { PhoneClaimName = phoneClaimName });

        return new RoleClaimsTransformation(
            provisioning,
            authOptions,
            profileOptions,
            NullLogger<RoleClaimsTransformation>.Instance);
    }

    private static ClaimsPrincipal AuthenticatedPrincipal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test", nameType: "name", roleType: "roles"));

    private static IEnumerable<Claim> RoleClaims(ClaimsPrincipal principal) =>
        ((ClaimsIdentity)principal.Identity!).FindAll(c => c.Type == "roles");

    // -----------------------------------------------------------------
    // Not authenticated -> untouched (specs §16.7.3 step 1)
    // -----------------------------------------------------------------

    [Fact]
    public async Task TransformAsync_UnauthenticatedPrincipal_IsReturnedUntouched()
    {
        var provisioning = new FakeUserProvisioningService();
        var sut = CreateSut(provisioning);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await sut.TransformAsync(anonymous);

        Assert.Same(anonymous, result);
        Assert.Equal(0, provisioning.CallCount);
    }

    // -----------------------------------------------------------------
    // Item 6/7 — provisioning + role read
    // -----------------------------------------------------------------

    [Fact]
    public async Task TransformAsync_UnknownOid_CreatesRowWithUserRole_AndPrincipalHasUserRole()
    {
        var oid = Guid.NewGuid();
        var provisioning = new FakeUserProvisioningService();
        var sut = CreateSut(provisioning);

        var principal = AuthenticatedPrincipal(
            new Claim("tid", DefaultTenantId),
            new Claim("oid", oid.ToString()));

        var result = await sut.TransformAsync(principal);

        Assert.True(result.IsInRole("User"));
        Assert.Equal(1, provisioning.CreatedCount);
        Assert.Equal(oid, provisioning.LastIdentity!.EntraObjectId);
    }

    [Fact]
    public async Task TransformAsync_KnownOidWithAdminRole_PrincipalHasAdminRole_AndNoNewRowIsCreated()
    {
        var oid = Guid.NewGuid();
        var provisioning = new FakeUserProvisioningService().WithExistingUser(oid, UserRole.Admin);
        var sut = CreateSut(provisioning);

        var principal = AuthenticatedPrincipal(
            new Claim("tid", DefaultTenantId),
            new Claim("oid", oid.ToString()));

        var result = await sut.TransformAsync(principal);

        Assert.True(result.IsInRole("Admin"));
        Assert.False(result.IsInRole("User"));
        Assert.Equal(0, provisioning.CreatedCount);
    }

    // -----------------------------------------------------------------
    // Item 8 — anti-escalation
    // -----------------------------------------------------------------

    [Fact]
    public async Task TransformAsync_InboundRolesClaimClaimsAdmin_ButDbRowIsUser_ResultIsUser_WithExactlyOneRoleClaim()
    {
        var oid = Guid.NewGuid();
        var provisioning = new FakeUserProvisioningService().WithExistingUser(oid, UserRole.User);
        var sut = CreateSut(provisioning);

        var principal = AuthenticatedPrincipal(
            new Claim("tid", DefaultTenantId),
            new Claim("oid", oid.ToString()),
            new Claim("roles", "Admin"),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("role", "Admin"));

        var result = await sut.TransformAsync(principal);

        Assert.True(result.IsInRole("User"));
        Assert.False(result.IsInRole("Admin"));

        var roleClaims = RoleClaims(result).ToList();
        Assert.Single(roleClaims);
        Assert.Equal("User", roleClaims[0].Value);
    }

    // -----------------------------------------------------------------
    // Item 9 — wrong tenant
    // -----------------------------------------------------------------

    [Fact]
    public async Task TransformAsync_WrongTenant_RejectsAsInvalidPrincipal_NoRoleClaim_NoProvisioningCall()
    {
        var provisioning = new FakeUserProvisioningService();
        var sut = CreateSut(provisioning);

        var principal = AuthenticatedPrincipal(
            new Claim("tid", Guid.NewGuid().ToString()),
            new Claim("oid", Guid.NewGuid().ToString()));

        var result = await sut.TransformAsync(principal);

        Assert.Equal(0, provisioning.CallCount);
        Assert.Equal(TodoClaimTypes.FailureInvalidPrincipal, result.FindFirst(TodoClaimTypes.AuthFailure)?.Value);
        Assert.False(result.HasDatabaseDerivedRole());
        Assert.Empty(RoleClaims(result));
    }

    [Fact]
    public async Task TransformAsync_MissingTenantClaim_RejectsAsInvalidPrincipal()
    {
        var provisioning = new FakeUserProvisioningService();
        var sut = CreateSut(provisioning);

        var principal = AuthenticatedPrincipal(new Claim("oid", Guid.NewGuid().ToString()));

        var result = await sut.TransformAsync(principal);

        Assert.Equal(0, provisioning.CallCount);
        Assert.Equal(TodoClaimTypes.FailureInvalidPrincipal, result.FindFirst(TodoClaimTypes.AuthFailure)?.Value);
    }

    // -----------------------------------------------------------------
    // Item 10 — missing/unparseable oid, no fallback to sub/email
    // -----------------------------------------------------------------

    [Fact]
    public async Task TransformAsync_MissingOid_RejectsAsInvalidPrincipal_NeverFallsBackToSubOrEmail()
    {
        var provisioning = new FakeUserProvisioningService();
        var sut = CreateSut(provisioning);

        var principal = AuthenticatedPrincipal(
            new Claim("tid", DefaultTenantId),
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("email", "someone@example.com"),
            new Claim("preferred_username", "someone@example.com"));

        var result = await sut.TransformAsync(principal);

        Assert.Equal(0, provisioning.CallCount);
        Assert.Equal(TodoClaimTypes.FailureInvalidPrincipal, result.FindFirst(TodoClaimTypes.AuthFailure)?.Value);
    }

    [Fact]
    public async Task TransformAsync_UnparseableOid_RejectsAsInvalidPrincipal()
    {
        var provisioning = new FakeUserProvisioningService();
        var sut = CreateSut(provisioning);

        var principal = AuthenticatedPrincipal(
            new Claim("tid", DefaultTenantId),
            new Claim("oid", "not-a-guid"));

        var result = await sut.TransformAsync(principal);

        Assert.Equal(0, provisioning.CallCount);
        Assert.Equal(TodoClaimTypes.FailureInvalidPrincipal, result.FindFirst(TodoClaimTypes.AuthFailure)?.Value);
    }

    // -----------------------------------------------------------------
    // Item 11 — idempotence
    // -----------------------------------------------------------------

    [Fact]
    public async Task TransformAsync_CalledTwiceOnSamePrincipal_PerformsExactlyOneDbLookup_AndYieldsOneRoleClaim()
    {
        var oid = Guid.NewGuid();
        var provisioning = new FakeUserProvisioningService().WithExistingUser(oid, UserRole.User);
        var sut = CreateSut(provisioning);

        var principal = AuthenticatedPrincipal(
            new Claim("tid", DefaultTenantId),
            new Claim("oid", oid.ToString()));

        var first = await sut.TransformAsync(principal);
        var second = await sut.TransformAsync(first);

        Assert.Equal(1, provisioning.CallCount);
        Assert.Single(RoleClaims(second));
    }

    [Fact]
    public async Task TransformAsync_CalledTwiceOnARejectedPrincipal_DoesNotRetryProvisioning()
    {
        var provisioning = new FakeUserProvisioningService();
        var sut = CreateSut(provisioning);

        var principal = AuthenticatedPrincipal(new Claim("tid", "wrong-tenant"));

        var first = await sut.TransformAsync(principal);
        var second = await sut.TransformAsync(first);

        Assert.Equal(0, provisioning.CallCount);
        Assert.Equal(TodoClaimTypes.FailureInvalidPrincipal, second.FindFirst(TodoClaimTypes.AuthFailure)?.Value);
    }

    // -----------------------------------------------------------------
    // Item 13 — fail closed on a role-store outage
    // -----------------------------------------------------------------

    [Fact]
    public async Task TransformAsync_ProvisioningThrows_FailsClosed_NoRoleClaim_NeverDefaultsToUserOrAdmin()
    {
        var provisioning = new FakeUserProvisioningService
        {
            ThrowOnCall = new InvalidOperationException("simulated Postgres outage"),
        };
        var sut = CreateSut(provisioning);

        var principal = AuthenticatedPrincipal(
            new Claim("tid", DefaultTenantId),
            new Claim("oid", Guid.NewGuid().ToString()));

        var result = await sut.TransformAsync(principal);

        Assert.Equal(TodoClaimTypes.FailureRoleSourceUnavailable, result.FindFirst(TodoClaimTypes.AuthFailure)?.Value);
        Assert.Empty(RoleClaims(result));
        Assert.False(result.IsInRole("Admin"));
        Assert.False(result.IsInRole("User"));
    }

    // -----------------------------------------------------------------
    // Item 20 (phone) is exercised end to end here too, via a real principal.
    // -----------------------------------------------------------------

    [Fact]
    public async Task TransformAsync_PassesConfiguredPhoneClaimNameThroughToProvisioning()
    {
        var provisioning = new FakeUserProvisioningService();
        var sut = CreateSut(provisioning, phoneClaimName: "extension_abc123_phoneNumber");

        var principal = AuthenticatedPrincipal(
            new Claim("tid", DefaultTenantId),
            new Claim("oid", Guid.NewGuid().ToString()),
            new Claim("extension_abc123_phoneNumber", "+15551234567"));

        await sut.TransformAsync(principal);

        Assert.Equal("+15551234567", provisioning.LastIdentity!.PhoneNumber);
    }
}

/// <summary>
/// Hand-rolled fake (no mocking library is referenced in this project) implementing the
/// exact contract <see cref="RoleClaimsTransformation"/> depends on, so tests can assert
/// call counts and simulate an outage without a database.
/// </summary>
internal sealed class FakeUserProvisioningService : IUserProvisioningService
{
    private readonly Dictionary<Guid, AppUser> _usersByOid = new();

    public int CallCount { get; private set; }

    public int CreatedCount { get; private set; }

    public ProvisioningIdentity? LastIdentity { get; private set; }

    public Exception? ThrowOnCall { get; set; }

    public FakeUserProvisioningService WithExistingUser(Guid oid, UserRole role)
    {
        _usersByOid[oid] = new AppUser
        {
            Id = Guid.NewGuid(),
            EntraObjectId = oid,
            Role = role,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        return this;
    }

    public Task<AppUser> GetOrCreateAsync(ProvisioningIdentity identity, CancellationToken cancellationToken)
    {
        CallCount++;
        LastIdentity = identity;

        if (ThrowOnCall is not null)
        {
            throw ThrowOnCall;
        }

        if (_usersByOid.TryGetValue(identity.EntraObjectId, out var existing))
        {
            return Task.FromResult(existing);
        }

        CreatedCount++;

        var created = new AppUser
        {
            Id = Guid.NewGuid(),
            EntraObjectId = identity.EntraObjectId,
            Role = UserRole.User, // always the server-side literal, per §16.7.5
            Email = identity.Email,
            DisplayName = identity.DisplayName,
            PhoneNumber = identity.PhoneNumber,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _usersByOid[identity.EntraObjectId] = created;
        return Task.FromResult(created);
    }
}
