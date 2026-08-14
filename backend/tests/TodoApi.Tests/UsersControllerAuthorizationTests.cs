using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TodoApi.Models;
using Xunit;

namespace TodoApi.Tests;

/// <summary>
/// Wire-level shape used only by tests to deserialize <c>UserResponse</c> JSON (camelCase,
/// specs §16.8).
/// </summary>
public record UserResponseDto(
    Guid Id,
    Guid EntraObjectId,
    string? Email,
    string? DisplayName,
    string? PhoneNumber,
    string Role,
    DateTimeOffset CreatedAt);

/// <summary>
/// Authorization tests for <c>UsersController</c> (specs §16.18 items 14–19): admin-only
/// endpoints actually enforce the <c>Admin</c> role re-derived from the database, not just
/// authentication — and the mass-assignment guard on <c>PUT /api/users/me</c> holds.
/// </summary>
public class UsersControllerAuthorizationTests : IAsyncLifetime
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

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    // -----------------------------------------------------------------
    // Item 14 — GET /api/users
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetUsers_AsUser_Returns403()
    {
        using var client = _factory.CreateAuthenticatedClient(TestToken.Valid());

        var response = await client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetUsers_AsAdmin_Returns200()
    {
        var oid = Guid.NewGuid();
        await _factory.SeedUserAsync(oid, UserRole.Admin);
        using var client = _factory.CreateAuthenticatedClient(TestToken.Valid(oid: oid));

        var response = await client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -----------------------------------------------------------------
    // Item 15 — PATCH /api/users/{id}/role
    // -----------------------------------------------------------------

    [Fact]
    public async Task PatchRole_AsUser_Returns403()
    {
        var target = await _factory.SeedUserAsync(Guid.NewGuid(), UserRole.User);
        using var client = _factory.CreateAuthenticatedClient(TestToken.Valid());

        var response = await client.PatchAsync($"/api/users/{target.Id}/role", Json(new { role = "Admin" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PatchRole_AsAdmin_PromotesTarget_Returns200()
    {
        var adminOid = Guid.NewGuid();
        await _factory.SeedUserAsync(adminOid, UserRole.Admin);
        var target = await _factory.SeedUserAsync(Guid.NewGuid(), UserRole.User);
        using var client = _factory.CreateAuthenticatedClient(TestToken.Valid(oid: adminOid));

        var response = await client.PatchAsync($"/api/users/{target.Id}/role", Json(new { role = "Admin" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserResponseDto>();
        Assert.Equal("Admin", body!.Role);
    }

    [Fact]
    public async Task PatchRole_PromotedUser_IsAdminAuthorizedOnTheirVeryNextRequest_NoReLogin()
    {
        // Acceptance criterion 6 (specs §16.17): role changes take effect on the next
        // request with no token round trip, because the role is re-derived from Postgres,
        // not carried in the token.
        var adminOid = Guid.NewGuid();
        await _factory.SeedUserAsync(adminOid, UserRole.Admin);
        var targetOid = Guid.NewGuid();
        var target = await _factory.SeedUserAsync(targetOid, UserRole.User);

        using var adminClient = _factory.CreateAuthenticatedClient(TestToken.Valid(oid: adminOid));
        var promote = await adminClient.PatchAsync($"/api/users/{target.Id}/role", Json(new { role = "Admin" }));
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);

        // Same "token" (same oid, same claims) as before the promotion — nothing re-issued.
        using var promotedClient = _factory.CreateAuthenticatedClient(TestToken.Valid(oid: targetOid));
        var response = await promotedClient.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -----------------------------------------------------------------
    // Item 16 — self-demotion / last-admin guards
    // -----------------------------------------------------------------

    [Fact]
    public async Task PatchRole_SelfDemotion_Returns409()
    {
        var oid = Guid.NewGuid();
        var self = await _factory.SeedUserAsync(oid, UserRole.Admin);
        using var client = _factory.CreateAuthenticatedClient(TestToken.Valid(oid: oid));

        var response = await client.PatchAsync($"/api/users/{self.Id}/role", Json(new { role = "User" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // And the guard actually held: the row is still Admin.
        using var verifyClient = _factory.CreateAuthenticatedClient(TestToken.Valid(oid: oid));
        var meResponse = await verifyClient.GetAsync("/api/users/me");
        var me = await meResponse.Content.ReadFromJsonAsync<UserResponseDto>();
        Assert.Equal("Admin", me!.Role);
    }

    [Fact]
    public async Task PatchRole_DemotingACoAdminWhenTwoAdminsExist_Succeeds_OneAdminRemains()
    {
        // Documents the boundary of the "never zero Admins" guard: demoting a DIFFERENT
        // admin is allowed as long as the acting admin's own row still counts, so this must
        // NOT 409. (See tests.md for why the literal "0 remaining admins for a non-self
        // target" branch is unreachable via the public API: the AdminOnly policy guarantees
        // the actor's own row is Admin and therefore always counts as one remaining admin
        // whenever actor != target.)
        var actorOid = Guid.NewGuid();
        await _factory.SeedUserAsync(actorOid, UserRole.Admin);
        var coAdmin = await _factory.SeedUserAsync(Guid.NewGuid(), UserRole.Admin);
        using var client = _factory.CreateAuthenticatedClient(TestToken.Valid(oid: actorOid));

        var response = await client.PatchAsync($"/api/users/{coAdmin.Id}/role", Json(new { role = "User" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -----------------------------------------------------------------
    // Item 17 — invalid role value
    // -----------------------------------------------------------------

    [Fact]
    public async Task PatchRole_InvalidRoleValue_Returns400()
    {
        var adminOid = Guid.NewGuid();
        await _factory.SeedUserAsync(adminOid, UserRole.Admin);
        var target = await _factory.SeedUserAsync(Guid.NewGuid(), UserRole.User);
        using var client = _factory.CreateAuthenticatedClient(TestToken.Valid(oid: adminOid));

        var response = await client.PatchAsync($"/api/users/{target.Id}/role", Json(new { role = "SuperAdmin" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchRole_LowercaseRoleValue_Returns400_CaseSensitive()
    {
        var adminOid = Guid.NewGuid();
        await _factory.SeedUserAsync(adminOid, UserRole.Admin);
        var target = await _factory.SeedUserAsync(Guid.NewGuid(), UserRole.User);
        using var client = _factory.CreateAuthenticatedClient(TestToken.Valid(oid: adminOid));

        var response = await client.PatchAsync($"/api/users/{target.Id}/role", Json(new { role = "admin" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchRole_UnknownTargetId_Returns404()
    {
        var adminOid = Guid.NewGuid();
        await _factory.SeedUserAsync(adminOid, UserRole.Admin);
        using var client = _factory.CreateAuthenticatedClient(TestToken.Valid(oid: adminOid));

        var response = await client.PatchAsync($"/api/users/{Guid.NewGuid()}/role", Json(new { role = "Admin" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -----------------------------------------------------------------
    // Item 18 — mass-assignment guard on PUT /api/users/me
    // -----------------------------------------------------------------

    [Fact]
    public async Task PutMe_BodyContainingRoleEmailAndEntraObjectId_IgnoresThoseFields()
    {
        var oid = Guid.NewGuid();
        await _factory.SeedUserAsync(oid, UserRole.User, email: "original@example.com");
        using var client = _factory.CreateAuthenticatedClient(TestToken.Valid(oid: oid));

        var payload = new
        {
            displayName = "New Name",
            phoneNumber = "+15550000000",
            role = "Admin",
            email = "hacked@example.com",
            entraObjectId = Guid.NewGuid().ToString(),
            id = Guid.NewGuid().ToString(),
        };

        var response = await client.PutAsync("/api/users/me", Json(payload));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserResponseDto>();

        Assert.Equal("New Name", body!.DisplayName);
        Assert.Equal("+15550000000", body.PhoneNumber);
        Assert.Equal("User", body.Role); // unchanged — role has no bindable property on the DTO
        Assert.Equal("original@example.com", body.Email); // unchanged
        Assert.Equal(oid, body.EntraObjectId); // unchanged
    }

    [Fact]
    public async Task PutMe_SentByAUser_CannotSelfPromoteToAdmin_VerifiedViaSubsequentAdminCheck()
    {
        var oid = Guid.NewGuid();
        await _factory.SeedUserAsync(oid, UserRole.User);
        using var client = _factory.CreateAuthenticatedClient(TestToken.Valid(oid: oid));

        await client.PutAsync("/api/users/me", Json(new { displayName = "Me", role = "Admin" }));

        var adminOnlyResponse = await client.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Forbidden, adminOnlyResponse.StatusCode);
    }

    // -----------------------------------------------------------------
    // Item 19 — GET /api/users/me returns the caller's row, never another's
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetMe_ReturnsCallersOwnRow_NeverAnotherUsers()
    {
        var oidA = Guid.NewGuid();
        var userA = await _factory.SeedUserAsync(oidA, UserRole.User, email: "a@example.com");
        var oidB = Guid.NewGuid();
        await _factory.SeedUserAsync(oidB, UserRole.User, email: "b@example.com");

        using var client = _factory.CreateAuthenticatedClient(TestToken.Valid(oid: oidA));
        var response = await client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserResponseDto>();
        Assert.Equal(userA.Id, body!.Id);
        Assert.Equal("a@example.com", body.Email);
    }

    [Fact]
    public async Task GetMe_FirstEverSignIn_LazilyProvisionsRowWithUserRole()
    {
        // No SeedUserAsync call: this oid has never been seen before.
        using var client = _factory.CreateAuthenticatedClient(TestToken.Valid());

        var response = await client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserResponseDto>();
        Assert.Equal("User", body!.Role);
    }
}
