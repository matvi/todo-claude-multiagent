using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TodoApi.Auth;
using Xunit;

namespace TodoApi.Tests;

/// <summary>
/// Pure unit tests of <see cref="AuthenticationRegistration.HardenTokenValidation"/> — the
/// regression guard against someone "fixing" a local problem by turning validation off
/// (specs §16.18 item 5), plus a direct proof that the resulting
/// <see cref="TokenValidationParameters"/> actually reject an unsigned token (item 3).
/// No host, no network — this calls the internal method directly (the test project has
/// <c>InternalsVisibleTo</c>).
/// </summary>
public class TokenValidationHardeningTests
{
    private static EntraAuthOptions ValidOptions()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureAd:Instance"] = "https://test-tenant.ciamlogin.com/",
                ["AzureAd:TenantId"] = "11111111-1111-1111-1111-111111111111",
                ["AzureAd:ClientId"] = "22222222-2222-2222-2222-222222222222",
                ["AzureAd:Audience"] = "api://22222222-2222-2222-2222-222222222222",
                ["AzureAd:Scopes"] = "Todos.ReadWrite",
            })
            .Build();

        return EntraAuthOptions.FromConfiguration(config);
    }

    [Fact]
    public void HardenTokenValidation_SetsEveryRequiredValidationFlagToTrue()
    {
        var options = new JwtBearerOptions();
        AuthenticationRegistration.HardenTokenValidation(options, ValidOptions());

        Assert.True(options.RequireHttpsMetadata);
        Assert.True(options.TokenValidationParameters.ValidateIssuer);
        Assert.True(options.TokenValidationParameters.ValidateAudience);
        Assert.True(options.TokenValidationParameters.ValidateLifetime);
        Assert.True(options.TokenValidationParameters.ValidateIssuerSigningKey);
        // Not explicitly set by HardenTokenValidation, but must never have been turned off —
        // this is the exact flag that would let an "alg: none" token through.
        Assert.True(options.TokenValidationParameters.RequireSignedTokens);
    }

    [Fact]
    public void HardenTokenValidation_AddsBothTheAudienceUriAndTheBareClientId()
    {
        var authOptions = ValidOptions();
        var options = new JwtBearerOptions();
        AuthenticationRegistration.HardenTokenValidation(options, authOptions);

        // This is what makes an ID token (audience = the SPA's client id) unusable against
        // the API: only the API's own audience/client id are ever added, never the SPA's.
        Assert.Contains(authOptions.Audience, options.TokenValidationParameters.ValidAudiences);
        Assert.Contains(authOptions.ClientId, options.TokenValidationParameters.ValidAudiences);
    }

    [Fact]
    public void HardenTokenValidation_AddsBothConcreteExternalIdIssuerHostForms()
    {
        var authOptions = ValidOptions();
        var options = new JwtBearerOptions();
        AuthenticationRegistration.HardenTokenValidation(options, authOptions);

        // The documented IDX10205 remedy (specs §16.7.2): subdomain-host AND tenant-GUID-host
        // issuer forms must both be present as an allow-list addition, never a validation
        // bypass.
        Assert.Contains(
            $"https://{authOptions.InstanceHost}/{authOptions.TenantId}/v2.0",
            options.TokenValidationParameters.ValidIssuers);
        Assert.Contains(
            $"https://{authOptions.TenantId}.ciamlogin.com/{authOptions.TenantId}/v2.0",
            options.TokenValidationParameters.ValidIssuers);
    }

    /// <summary>
    /// specs §16.18 item 5: "Configuration asserts: RequireHttpsMetadata, ValidateIssuer,
    /// ValidateAudience, ValidateLifetime, ValidateIssuerSigningKey are all true AFTER
    /// AddMicrosoftIdentityWebApi binding" — resolved through the real DI container via the
    /// production <see cref="AuthenticationRegistration.AddTodoAuthentication"/> entry point
    /// (not by calling <c>HardenTokenValidation</c> directly), so this proves the full
    /// registration pipeline, not just the helper in isolation.
    /// </summary>
    [Fact]
    public void AddTodoAuthentication_ConfiguresJwtBearerOptions_WithAllValidationFlagsTrue_ViaRealDiRegistration()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureAd:Instance"] = "https://test-tenant.ciamlogin.com/",
                ["AzureAd:TenantId"] = "11111111-1111-1111-1111-111111111111",
                ["AzureAd:ClientId"] = "22222222-2222-2222-2222-222222222222",
                ["AzureAd:Audience"] = "api://22222222-2222-2222-2222-222222222222",
                ["AzureAd:Scopes"] = "Todos.ReadWrite",
            })
            .Build();

        services.AddSingleton<IConfiguration>(config);
        services.AddTodoAuthentication(config);
        using var provider = services.BuildServiceProvider();

        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();
        var options = optionsMonitor.Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.True(options.RequireHttpsMetadata);
        Assert.True(options.TokenValidationParameters.ValidateIssuer);
        Assert.True(options.TokenValidationParameters.ValidateAudience);
        Assert.True(options.TokenValidationParameters.ValidateLifetime);
        Assert.True(options.TokenValidationParameters.ValidateIssuerSigningKey);
    }

    /// <summary>
    /// specs §16.18 item 3: "A request bearing a token signed by a test key is accepted only
    /// when the test host has deliberately swapped the JWT scheme for a test scheme — i.e.
    /// assert the production configuration does not accept unsigned/alg=none tokens."
    /// </summary>
    [Fact]
    public void ProductionValidationConfiguration_RejectsAnUnsignedAlgNoneToken()
    {
        var authOptions = ValidOptions();
        var options = new JwtBearerOptions();
        AuthenticationRegistration.HardenTokenValidation(options, authOptions);

        // A syntactically well-formed but entirely unsigned ("alg": "none") token, with an
        // issuer/audience/tid that would otherwise satisfy every other check — isolating the
        // assertion to signature validation specifically.
        var unsignedToken = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: $"https://{authOptions.TenantId}.ciamlogin.com/{authOptions.TenantId}/v2.0",
            audience: authOptions.Audience,
            claims: new[]
            {
                new Claim("oid", Guid.NewGuid().ToString()),
                new Claim("tid", authOptions.TenantId),
                new Claim("scp", "Todos.ReadWrite"),
            },
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.AddMinutes(30)));

        var handler = new JwtSecurityTokenHandler();

        Assert.ThrowsAny<SecurityTokenException>(
            () => handler.ValidateToken(unsignedToken, options.TokenValidationParameters, out _));
    }
}
