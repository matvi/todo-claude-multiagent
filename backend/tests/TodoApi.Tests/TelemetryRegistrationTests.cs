using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using TodoApi.Observability;
using Xunit;

namespace TodoApi.Tests;

/// <summary>
/// Lightweight DI-level tests for TodoApi.Observability.TelemetryRegistration,
/// independent of a full WebApplicationFactory host. Complements
/// ObservabilityTests.cs (which proves in-request trace/log correlation).
/// </summary>
public class TelemetryRegistrationTests
{
    private static IConfiguration BuildConfig(string? connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [TelemetryRegistration.ConnectionStringKey] = connectionString,
            })
            .Build();

    [Fact]
    public void ConnectionStringKey_IsTheStandardAppInsightsEnvVarName()
    {
        // Per spec §12.5: the SDK-recognized flat name, no "__" section syntax.
        Assert.Equal("APPLICATIONINSIGHTS_CONNECTION_STRING", TelemetryRegistration.ConnectionStringKey);
    }

    [Fact]
    public void AddTodoTelemetry_WithNoConnectionString_DoesNotThrow_AndRegistersTracerProvider()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfig(connectionString: null);

        var registrationException = Record.Exception(() => services.AddTodoTelemetry(configuration));
        Assert.Null(registrationException);

        using var provider = services.BuildServiceProvider();
        var buildException = Record.Exception(() => provider.GetService<TracerProvider>());
        Assert.Null(buildException);

        var tracerProvider = provider.GetService<TracerProvider>();
        Assert.NotNull(tracerProvider);
    }

    [Fact]
    public void AddTodoTelemetry_WithConnectionStringConfigured_DoesNotThrow_AndRegistersTracerProvider()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfig(
            "InstrumentationKey=00000000-0000-0000-0000-000000000000;" +
            "IngestionEndpoint=https://eastus-0.in.applicationinsights.azure.com/;" +
            "LiveEndpoint=https://eastus.livediagnostics.monitor.azure.com/");

        var registrationException = Record.Exception(() => services.AddTodoTelemetry(configuration));
        Assert.Null(registrationException);

        using var provider = services.BuildServiceProvider();
        var buildException = Record.Exception(() => provider.GetService<TracerProvider>());
        Assert.Null(buildException);

        var tracerProvider = provider.GetService<TracerProvider>();
        Assert.NotNull(tracerProvider);
    }

    [Fact]
    public void AddTodoTelemetry_WithBlankConnectionString_TreatedAsUnset()
    {
        // Whitespace-only value must behave identically to "unset" (IsNullOrWhiteSpace
        // guard in TelemetryRegistration), not attempt to configure Azure Monitor with
        // an empty string.
        var services = new ServiceCollection();
        var configuration = BuildConfig(connectionString: "   ");

        var exception = Record.Exception(() => services.AddTodoTelemetry(configuration));
        Assert.Null(exception);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<TracerProvider>());
    }
}
