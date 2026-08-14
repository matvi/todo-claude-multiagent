using System;

namespace TodoApi.Tests;

/// <summary>
/// Ensures every <c>WebApplicationFactory&lt;Program&gt;</c>-based test host in this assembly
/// sees a valid-shaped <c>AzureAd:*</c> configuration from the moment <c>Program.cs</c>'s
/// top-level statements run (specs §16.7.1, §16.18 item 22).
///
/// <para>
/// <b>This has to be environment variables, not <c>WebApplicationFactory</c>'s
/// <c>ConfigureWebHost</c>/<c>ConfigureAppConfiguration</c>/<c>ConfigureServices</c> hooks.</b>
/// Those hooks are only applied at the point the test host's deferred builder intercepts
/// <c>WebApplicationBuilder.Build()</c> — but <c>AddTodoAuthentication</c> reads
/// <c>AzureAd:*</c> <i>eagerly and synchronously</i>, in <c>Program.cs</c>'s top-level
/// statements, well before <c>Build()</c> is ever called (verified empirically: attempting
/// the config-injection approach reproduces the exact "Entra External ID authentication is
/// not configured" fail-fast exception even though the values were supplied). Environment
/// variables, by contrast, are part of <c>WebApplication.CreateBuilder(args)</c>'s own
/// default configuration sources from the very first line of <c>Program.cs</c>, so calling
/// <see cref="EnsureConfigured"/> from a factory's own constructor — which always runs
/// before <c>StartServer()</c> is ever triggered — is early enough.
/// </para>
///
/// <para>
/// Safe to set once, process-wide, for the life of the test run: every host in this
/// assembly wants the same default <c>AzureAd:*</c> shape, and this assembly already
/// disables test parallelization (see the <c>[assembly: CollectionBehavior]</c> attribute in
/// <c>ObservabilityTests.cs</c>), so there is never a concurrently-building host that wants a
/// different value.
/// </para>
/// </summary>
internal static class TestAzureAdEnvironment
{
    public const string DefaultInstance = "https://test-tenant.ciamlogin.com/";
    public const string DefaultTenantId = "11111111-1111-1111-1111-111111111111";
    public const string DefaultClientId = "22222222-2222-2222-2222-222222222222";
    public const string DefaultAudience = "api://22222222-2222-2222-2222-222222222222";
    public const string DefaultScope = "Todos.ReadWrite";

    private static readonly object Gate = new();
    private static bool s_configured;

    public static void EnsureConfigured()
    {
        if (s_configured)
        {
            return;
        }

        lock (Gate)
        {
            if (s_configured)
            {
                return;
            }

            Environment.SetEnvironmentVariable("AzureAd__Instance", DefaultInstance);
            Environment.SetEnvironmentVariable("AzureAd__TenantId", DefaultTenantId);
            Environment.SetEnvironmentVariable("AzureAd__ClientId", DefaultClientId);
            Environment.SetEnvironmentVariable("AzureAd__Audience", DefaultAudience);
            Environment.SetEnvironmentVariable("AzureAd__Scopes", DefaultScope);

            s_configured = true;
        }
    }
}
