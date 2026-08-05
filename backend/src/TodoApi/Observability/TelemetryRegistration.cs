using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Npgsql;
using OpenTelemetry.Trace;

namespace TodoApi.Observability;

/// <summary>
/// Wires distributed tracing (OpenTelemetry) for the backend and, when configured,
/// exports it to Azure Application Insights via the Azure Monitor OpenTelemetry
/// Distro. See specs.md §12 (observability) and §13.5 (identity policy).
///
/// Design:
///  - OpenTelemetry tracing is ALWAYS active in-process (ASP.NET Core request spans,
///    HttpClient spans, and Npgsql DB spans) so that the W3C <c>traceId</c>/<c>spanId</c>
///    flows onto every log line via <see cref="System.Diagnostics.Activity.Current"/>,
///    regardless of whether an exporter is configured. This is what makes the
///    in-process correlation tests (§12.8) work with no live Azure endpoint.
///  - The Azure Monitor exporter is only added when
///    <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is present. When it is unset
///    (local dev / CI) the pipeline cleanly no-ops instead of throwing (§12.5).
///  - When the exporter is added, telemetry ingestion authenticates with the
///    Container App's managed identity (Entra) rather than the connection string's
///    embedded key, per the managed-identity-first policy (§13.5). The credential is
///    only constructed when a connection string is present, so it is never invoked
///    during local development.
/// </summary>
public static class TelemetryRegistration
{
    /// <summary>
    /// Standard SDK-recognized environment variable / config key for the
    /// Application Insights connection string. Delivered as a plain (non-secret)
    /// env var per §13.5 — never hardcoded or committed.
    /// </summary>
    public const string ConnectionStringKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    public static IServiceCollection AddTodoTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var otel = services.AddOpenTelemetry();

        var connectionString = configuration[ConnectionStringKey];
        var appInsightsConfigured = !string.IsNullOrWhiteSpace(connectionString);

        if (appInsightsConfigured)
        {
            // Azure: the Azure Monitor distro registers ASP.NET Core + HttpClient
            // instrumentation, the OTel logging provider, and the exporter. Ingestion
            // is authenticated with the managed identity (Entra), so the connection
            // string is only a resource identifier, not a credential (§13.5).
            otel.UseAzureMonitor(options =>
            {
                // options.ConnectionString is read from APPLICATIONINSIGHTS_CONNECTION_STRING
                // automatically; we only override the authentication method.
                // DefaultAzureCredential resolves to the managed identity inside Azure
                // Container Apps (§13.5 permits it as an alternative to
                // ManagedIdentityCredential); it is only constructed here when a
                // connection string is present, so it is never invoked locally.
                options.Credential = new DefaultAzureCredential();
            });
        }
        else
        {
            // Local dev / CI: no App Insights endpoint. Keep in-process tracing active
            // (same instrumentation the distro would have added) so traceId/spanId
            // correlation still works and tests can observe spans — but export nothing.
            otel.WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());
        }

        // Npgsql database spans are not added by the Azure Monitor distro, so enable
        // them explicitly in both modes. This makes each DB call a child span of the
        // request under the same traceId (§12.2). Added once, so no duplicate source.
        otel.WithTracing(tracing => tracing.AddNpgsql());

        return services;
    }
}
