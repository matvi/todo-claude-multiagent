using System;
using System.Security.Claims;
using TodoApi.Auth;
using Xunit;

namespace TodoApi.Tests;

/// <summary>
/// Pure unit tests of <see cref="ProfileClaimsReader"/> (specs §16.7.4, §16.18 item 20).
/// </summary>
public class ProfileClaimsReaderTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test"));

    [Fact]
    public void GetPhoneNumber_UsesConfiguredClaimName_WhenPresent()
    {
        var principal = Principal(new Claim("extension_abc123_phoneNumber", "+15551234567"));

        var phone = ProfileClaimsReader.GetPhoneNumber(principal, "extension_abc123_phoneNumber");

        Assert.Equal("+15551234567", phone);
    }

    [Fact]
    public void GetPhoneNumber_FallsBackToSuffixMatch_WhenNotConfigured()
    {
        var principal = Principal(new Claim("extension_deadbeef_phoneNumber", "+15559876543"));

        var phone = ProfileClaimsReader.GetPhoneNumber(principal, configuredClaimName: null);

        Assert.Equal("+15559876543", phone);
    }

    [Fact]
    public void GetPhoneNumber_ConfiguredClaimNameAbsentFromToken_FallsBackToSuffixMatch()
    {
        // The configured name is tenant-specific and might not (yet) match what a given
        // token actually carries; the suffix fallback must still work.
        var principal = Principal(new Claim("extension_other_phoneNumber", "+19995551234"));

        var phone = ProfileClaimsReader.GetPhoneNumber(principal, "extension_configured_but_absent_phoneNumber");

        Assert.Equal("+19995551234", phone);
    }

    [Fact]
    public void GetPhoneNumber_AbsentEntirely_ReturnsNull_NoException()
    {
        var principal = Principal(new Claim("oid", Guid.NewGuid().ToString()));

        var phone = ProfileClaimsReader.GetPhoneNumber(principal, configuredClaimName: null);

        Assert.Null(phone);
    }

    [Fact]
    public void GetEmail_PrefersEmailClaim_OverPreferredUsername()
    {
        var principal = Principal(
            new Claim("email", "primary@example.com"),
            new Claim("preferred_username", "secondary@example.com"));

        Assert.Equal("primary@example.com", ProfileClaimsReader.GetEmail(principal));
    }

    [Fact]
    public void GetEmail_FallsBackToPreferredUsername_WhenItLooksLikeAnEmail()
    {
        var principal = Principal(new Claim("preferred_username", "someone@example.com"));

        Assert.Equal("someone@example.com", ProfileClaimsReader.GetEmail(principal));
    }

    [Fact]
    public void GetEmail_PreferredUsernameThatIsNotAnEmail_IsIgnored()
    {
        var principal = Principal(new Claim("preferred_username", "not-an-email"));

        Assert.Null(ProfileClaimsReader.GetEmail(principal));
    }

    [Fact]
    public void GetDisplayName_PrefersNameClaim_OverGivenAndFamilyName()
    {
        var principal = Principal(
            new Claim("name", "Full Name"),
            new Claim("given_name", "Given"),
            new Claim("family_name", "Family"));

        Assert.Equal("Full Name", ProfileClaimsReader.GetDisplayName(principal));
    }

    [Fact]
    public void GetDisplayName_ComposesGivenAndFamilyName_WhenNameClaimAbsent()
    {
        var principal = Principal(
            new Claim("given_name", "Given"),
            new Claim("family_name", "Family"));

        Assert.Equal("Given Family", ProfileClaimsReader.GetDisplayName(principal));
    }

    [Fact]
    public void GetDisplayName_AbsentEntirely_ReturnsNull()
    {
        var principal = Principal(new Claim("oid", Guid.NewGuid().ToString()));

        Assert.Null(ProfileClaimsReader.GetDisplayName(principal));
    }

    [Fact]
    public void TryGetObjectId_ValidGuidOidClaim_Succeeds()
    {
        var oid = Guid.NewGuid();
        var principal = Principal(new Claim("oid", oid.ToString()));

        Assert.True(ProfileClaimsReader.TryGetObjectId(principal, out var result));
        Assert.Equal(oid, result);
    }

    [Fact]
    public void TryGetObjectId_NeverFallsBackToSub()
    {
        var principal = Principal(new Claim("sub", Guid.NewGuid().ToString()));

        Assert.False(ProfileClaimsReader.TryGetObjectId(principal, out _));
    }
}
