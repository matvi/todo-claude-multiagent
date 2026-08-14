using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace TodoApi.Tests;

/// <summary>
/// Fluent builder for a simulated access token's claim set, encoded the way
/// <see cref="TestAuthHandler"/> expects (specs §16.18). Defaults to a fully valid token —
/// matching <see cref="TodoApiFactory.DefaultTenantId"/>, a fresh <c>oid</c>, and
/// <c>scp=Todos.ReadWrite</c> — so a test only has to override the one thing it is
/// specifically exercising (missing scope, wrong tenant, missing <c>oid</c>, a spoofed role
/// claim, ...).
/// </summary>
public sealed class TestToken
{
    /// <summary>
    /// The role claim type this test identity uses. Arbitrary but must be internally
    /// consistent with what <see cref="TestAuthHandler"/> builds the identity's
    /// <c>RoleClaimType</c> from — <see cref="TodoApi.Auth.RoleClaimsTransformation"/> reads
    /// <c>identity.RoleClaimType</c> dynamically, so it does not need to match production's
    /// exact mapping to prove the transformation's own logic.
    /// </summary>
    public const string RoleClaimType = "roles";

    private readonly List<TestClaim> _claims = new();

    /// <summary>
    /// A token that passes every check: correct <c>tid</c>, a fresh <c>oid</c>, and
    /// <paramref name="scope"/> (pass <c>null</c> to omit the <c>scp</c> claim entirely).
    /// </summary>
    public static TestToken Valid(
        Guid? oid = null,
        string? tenantId = null,
        string? scope = TodoApiFactory.DefaultScope)
    {
        var token = new TestToken()
            .WithClaim("tid", tenantId ?? TodoApiFactory.DefaultTenantId)
            .WithClaim("oid", (oid ?? Guid.NewGuid()).ToString());

        if (!string.IsNullOrEmpty(scope))
        {
            token.WithClaim("scp", scope);
        }

        return token;
    }

    public TestToken WithClaim(string type, string value)
    {
        _claims.Add(new TestClaim(type, value));
        return this;
    }

    public TestToken WithoutClaimType(string type)
    {
        _claims.RemoveAll(c => string.Equals(c.Type, type, StringComparison.Ordinal));
        return this;
    }

    public TestToken WithoutScope() => WithoutClaimType("scp");

    public TestToken WithTenant(string tenantId) => WithoutClaimType("tid").WithClaim("tid", tenantId);

    public TestToken WithoutOid() => WithoutClaimType("oid");

    public TestToken WithOid(Guid oid) => WithoutClaimType("oid").WithClaim("oid", oid.ToString());

    public TestToken WithEmail(string email) => WithClaim("email", email);

    public TestToken WithName(string name) => WithClaim("name", name);

    /// <summary>
    /// Adds an inbound role claim, simulating a token minted by a misconfigured/compromised
    /// IdP trying to assert a role directly (specs §16.7.3 step 5, anti-escalation).
    /// <see cref="TodoApi.Auth.RoleClaimsTransformation"/> must strip this before it ever
    /// reaches an authorization check.
    /// </summary>
    public TestToken WithSpoofedRole(string role) => WithClaim(RoleClaimType, role);

    public string Encode()
    {
        var json = JsonSerializer.Serialize(_claims);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }
}
