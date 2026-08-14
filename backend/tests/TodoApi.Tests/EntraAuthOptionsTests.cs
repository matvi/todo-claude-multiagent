using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using TodoApi.Auth;
using Xunit;

namespace TodoApi.Tests;

/// <summary>
/// Pure unit tests of the fail-fast startup guard (specs §16.7.1, §16.18 items 4 and 31).
/// No host, no network, no database — <see cref="EntraAuthOptions.FromConfiguration"/> is a
/// plain function of an <see cref="IConfiguration"/>.
/// </summary>
public class EntraAuthOptionsTests
{
    private static Dictionary<string, string?> ValidValues() => new()
    {
        ["AzureAd:Instance"] = "https://test-tenant.ciamlogin.com/",
        ["AzureAd:TenantId"] = "11111111-1111-1111-1111-111111111111",
        ["AzureAd:ClientId"] = "22222222-2222-2222-2222-222222222222",
        ["AzureAd:Audience"] = "api://22222222-2222-2222-2222-222222222222",
        ["AzureAd:Scopes"] = "Todos.ReadWrite",
    };

    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void FromConfiguration_WithAllFiveKeysPresent_Succeeds()
    {
        var options = EntraAuthOptions.FromConfiguration(BuildConfig(ValidValues()));

        Assert.Equal("https://test-tenant.ciamlogin.com/", options.Instance);
        Assert.Equal("11111111-1111-1111-1111-111111111111", options.TenantId);
        Assert.Equal("Todos.ReadWrite", options.Scopes);
    }

    [Theory]
    [InlineData("AzureAd:Instance")]
    [InlineData("AzureAd:TenantId")]
    [InlineData("AzureAd:ClientId")]
    [InlineData("AzureAd:Audience")]
    [InlineData("AzureAd:Scopes")]
    public void FromConfiguration_AnyOneOfTheFiveRequiredKeysBlank_ThrowsNamingIt(string keyToBlank)
    {
        var values = ValidValues();
        values[keyToBlank] = "";

        var ex = Assert.Throws<InvalidOperationException>(
            () => EntraAuthOptions.FromConfiguration(BuildConfig(values)));

        Assert.Contains(keyToBlank, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AzureAd:Instance")]
    [InlineData("AzureAd:TenantId")]
    [InlineData("AzureAd:ClientId")]
    [InlineData("AzureAd:Audience")]
    [InlineData("AzureAd:Scopes")]
    public void FromConfiguration_AnyOneOfTheFiveRequiredKeysMissingEntirely_ThrowsNamingIt(string keyToRemove)
    {
        var values = ValidValues();
        values.Remove(keyToRemove);

        var ex = Assert.Throws<InvalidOperationException>(
            () => EntraAuthOptions.FromConfiguration(BuildConfig(values)));

        Assert.Contains(keyToRemove, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromConfiguration_ScopesBlank_IsPartOfTheFailFastList()
    {
        // Explicit regression guard for the "Scopes is authoritative, not optional" note in
        // specs §16.7.1 — a blank Scopes value would silently disable [RequiredScope] on
        // every controller if it were not fail-fast.
        var values = ValidValues();
        values["AzureAd:Scopes"] = "   ";

        var ex = Assert.Throws<InvalidOperationException>(
            () => EntraAuthOptions.FromConfiguration(BuildConfig(values)));

        Assert.Contains("AzureAd:Scopes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromConfiguration_AllFiveKeysMissing_NamesEveryOneInTheSameException()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => EntraAuthOptions.FromConfiguration(BuildConfig(new Dictionary<string, string?>())));

        Assert.Contains("AzureAd:Instance", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AzureAd:TenantId", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AzureAd:ClientId", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AzureAd:Audience", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AzureAd:Scopes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromConfiguration_InstanceIsHttp_ThrowsBecauseTlsIsRequired()
    {
        var values = ValidValues();
        values["AzureAd:Instance"] = "http://test-tenant.ciamlogin.com/";

        var ex = Assert.Throws<InvalidOperationException>(
            () => EntraAuthOptions.FromConfiguration(BuildConfig(values)));

        Assert.Contains("https", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromConfiguration_InstanceIsNotAnAbsoluteUri_Throws()
    {
        var values = ValidValues();
        values["AzureAd:Instance"] = "not-a-uri";

        Assert.Throws<InvalidOperationException>(
            () => EntraAuthOptions.FromConfiguration(BuildConfig(values)));
    }

    [Fact]
    public void FromConfiguration_TenantIdIsNotAGuid_Throws()
    {
        var values = ValidValues();
        values["AzureAd:TenantId"] = "not-a-guid";

        var ex = Assert.Throws<InvalidOperationException>(
            () => EntraAuthOptions.FromConfiguration(BuildConfig(values)));

        Assert.Contains("TenantId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromConfiguration_UsersPhoneClaimNameAbsent_IsNeverPartOfFailFast()
    {
        // specs §16.7.1: "Users:PhoneClaimName is optional and must NEVER be part of the
        // fail-fast list." Its value cannot be known until the external tenant exists.
        var options = EntraAuthOptions.FromConfiguration(BuildConfig(ValidValues()));

        Assert.NotNull(options);
    }
}
