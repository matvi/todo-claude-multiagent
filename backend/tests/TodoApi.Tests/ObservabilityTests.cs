using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using TodoApi.Data;
using TodoApi.Observability;
using Xunit;

// System.Diagnostics.ActivitySource listeners registered by OpenTelemetry are
// process-wide (static), not scoped per WebApplicationFactory/TestServer instance.
// Multiple test classes each booting their own host (all wired via the same
// TelemetryRegistration.AddTodoTelemetry) would otherwise observe each other's
// request Activities when xUnit runs different test classes/collections in
// parallel, making trace-correlation assertions flaky/cross-contaminated.
// Disabling parallelization keeps only one host's requests in flight at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TodoApi.Tests;

/// <summary>
/// Verifies specs.md §12.8 in-process, without a live Application Insights endpoint:
///  - The app's own OpenTelemetry wiring (TelemetryRegistration.AddTodoTelemetry)
///    actually produces a request span with a valid, non-default W3C TraceId.
///  - An ILogger record emitted while handling that request carries the SAME
///    TraceId (and SpanId) as the request's Activity, proving log/trace correlation
///    happens automatically (no manual enrichment).
///  - The app starts and serves requests normally with
///    APPLICATIONINSIGHTS_CONNECTION_STRING unset (the §12.5 no-op requirement).
///
/// These tests hook a small in-process Activity-capturing processor into the SAME
/// TracerProviderBuilder the app registers via AddTodoTelemetry, using
/// IServiceCollection.ConfigureOpenTelemetryTracerProvider — OpenTelemetry's hosting
/// extensions are designed to accumulate multiple such configuration calls against
/// one provider (TelemetryRegistration itself relies on this to add Npgsql
/// instrumentation on top of the ASP.NET Core/HttpClient instrumentation added
/// elsewhere). If TelemetryRegistration stopped calling
/// AddAspNetCoreInstrumentation()/wiring OpenTelemetry at all, ASP.NET Core's
/// hosting ActivitySource would have no listener, Activity.Current would stay
/// null for the request, and these assertions would fail — so this is a real
/// test of the app's wiring, not a tautology.
/// </summary>
public record CapturedLogEntry(string Category, LogLevel Level, string Message, string? TraceId, string? SpanId);

/// <summary>
/// Minimal thread-safe processor that appends every ended Activity to a shared list.
/// Used instead of relying on a packaged in-memory exporter so the test has full
/// visibility/control over the export path.
/// </summary>
file sealed class ListCapturingProcessor(List<Activity> sink) : OpenTelemetry.BaseProcessor<Activity>
{
    private readonly object _lock = new();

    public override void OnEnd(Activity data)
    {
        lock (_lock)
        {
            sink.Add(data);
        }
    }
}

public class ObservabilityTestFactory : WebApplicationFactory<Program>
{
    public readonly string DatabaseName = $"ObservabilityTests-{Guid.NewGuid()}";
    public readonly List<Activity> ExportedActivities = new();
    public readonly ConcurrentQueue<CapturedLogEntry> CapturedLogs = new();

    /// <summary>
    /// When set, becomes the APPLICATIONINSIGHTS_CONNECTION_STRING configuration
    /// value seen by the app (simulating the Container Apps env var). Left null by
    /// default to exercise the §12.5 "unset" / no-op path.
    /// </summary>
    public string? AppInsightsConnectionString { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Passwordless by contract (specs §14); never dialled.
                ["ConnectionStrings:TodoDb"] =
                    "Host=localhost;Port=5432;Database=unused;Username=unused",
                ["Cors:AllowedOrigins:0"] = "http://localhost:5173",
                // Explicitly control this key (rather than leaving it to ambient
                // process env vars) so the test is deterministic either way.
                [TelemetryRegistration.ConnectionStringKey] = AppInsightsConnectionString,
            });
        });

        builder.ConfigureServices(services =>
        {
            // Swap the Npgsql-backed TodoDbContext for EF Core InMemory, same
            // technique as TodoApiFactory — no real Postgres needed.
            var descriptorsToRemove = services
                .Where(d =>
                    d.ServiceType == typeof(TodoDbContext) ||
                    (d.ServiceType.IsGenericType &&
                     d.ServiceType.GetGenericArguments().Contains(typeof(TodoDbContext))))
                .ToList();
            foreach (var descriptor in descriptorsToRemove)
            {
                services.Remove(descriptor);
            }
            services.AddDbContext<TodoDbContext>(options =>
                options.UseInMemoryDatabase(DatabaseName));

            // Inject a marker log line into the middleware pipeline (without
            // touching Program.cs) so we have a deterministic, in-request
            // ILogger call to check for trace correlation.
            services.AddSingleton<IStartupFilter>(new MarkerLoggingStartupFilter());

            // Capture every log record's ambient Activity TraceId/SpanId at the
            // moment it is logged — this is exactly the mechanism the requirement
            // depends on (Activity.Current flowing to ILogger call sites).
            services.AddLogging(logging =>
                logging.AddProvider(new TraceCapturingLoggerProvider(CapturedLogs)));

            // Hook an in-process Activity-capturing processor into the app's own
            // TracerProviderBuilder (the one AddTodoTelemetry registered) via the
            // lower-level hosting configuration hook, so it accumulates onto the
            // SAME provider rather than risking a second independent registration.
            services.ConfigureOpenTelemetryTracerProvider((_, tracing) =>
                tracing.AddProcessor(new ListCapturingProcessor(ExportedActivities)));
        });
    }

    private sealed class MarkerLoggingStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    var logger = context.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("TodoApi.Tests.RequestMarker");
                    logger.LogInformation("observability-test-marker {Path}", context.Request.Path);
                    await nextMiddleware();
                });
                next(app);
            };
        }
    }

    private sealed class TraceCapturingLoggerProvider(ConcurrentQueue<CapturedLogEntry> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new TraceCapturingLogger(categoryName, sink);

        public void Dispose() { }

        private sealed class TraceCapturingLogger(string category, ConcurrentQueue<CapturedLogEntry> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var activity = Activity.Current;
                sink.Enqueue(new CapturedLogEntry(
                    category,
                    logLevel,
                    formatter(state, exception),
                    activity?.TraceId.ToHexString(),
                    activity?.SpanId.ToHexString()));
            }
        }
    }
}

public class ObservabilityTests : IDisposable
{
    private readonly ObservabilityTestFactory _factory;

    public ObservabilityTests()
    {
        _factory = new ObservabilityTestFactory();
    }

    public void Dispose() => _factory.Dispose();

    // -----------------------------------------------------------------
    // Wiring / DI smoke test (§12.8 bullet 1)
    // -----------------------------------------------------------------

    [Fact]
    public void TracerProvider_IsRegisteredInServiceCollection()
    {
        // Force host creation.
        using var client = _factory.CreateClient();

        var tracerProvider = _factory.Services.GetService<TracerProvider>();

        Assert.NotNull(tracerProvider);
    }

    // -----------------------------------------------------------------
    // Trace-context flow + log correlation (§12.8 bullet 2, the core assertion)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Request_ProducesValidTraceId_AndCorrelatesLogRecordToSameTrace()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/todos");
        response.EnsureSuccessStatusCode();

        // Force the in-memory exporter to flush any batched activities.
        var tracerProvider = _factory.Services.GetRequiredService<TracerProvider>();
        tracerProvider.ForceFlush();

        // (b) a log record emitted during the request carries a valid TraceId —
        // this comes from a logger provider registered only on THIS host, so it
        // cannot be contaminated by other hosts running concurrently elsewhere in
        // the process.
        var markerLog = _factory.CapturedLogs
            .FirstOrDefault(l => l.Message.Contains("observability-test-marker"));

        Assert.NotNull(markerLog);
        Assert.False(string.IsNullOrEmpty(markerLog!.TraceId));
        var traceId = markerLog.TraceId!;
        Assert.Equal(32, traceId.Length);
        Assert.Matches("^[0-9a-f]{32}$", traceId);
        Assert.NotEqual(new string('0', 32), traceId);

        // (a) that SAME TraceId is also carried by a server Activity exported from
        // this host's tracer provider — proving log-to-trace correlation with no
        // manual enrichment. Matched by the known-correct TraceId (rather than
        // "first Server activity") because System.Diagnostics.ActivitySource
        // listeners are process-wide: other WebApplicationFactory-hosted tests
        // running concurrently in this assembly register their own instrumentation
        // against the SAME static "Microsoft.AspNetCore.Hosting" ActivitySource, so
        // this host's in-memory processor can also observe unrelated hosts'
        // activities when tests run in parallel.
        var serverActivity = _factory.ExportedActivities
            .FirstOrDefault(a => a.Kind == ActivityKind.Server && a.TraceId.ToHexString() == traceId);

        Assert.NotNull(serverActivity);
    }

    [Fact]
    public async Task MultipleRequests_ProduceDifferentTraceIds()
    {
        // Each inbound request must get its own trace, not a shared/static one.
        using var client = _factory.CreateClient();

        await (await client.GetAsync("/health")).Content.ReadAsStringAsync();
        await (await client.GetAsync("/health")).Content.ReadAsStringAsync();

        _factory.Services.GetRequiredService<TracerProvider>().ForceFlush();

        var traceIds = _factory.ExportedActivities
            .Where(a => a.Kind == ActivityKind.Server)
            .Select(a => a.TraceId.ToHexString())
            .Distinct()
            .ToList();

        Assert.True(traceIds.Count >= 2, "Expected at least two distinct trace ids for two separate requests.");
    }

    // -----------------------------------------------------------------
    // Env-var absence: app must start and serve requests with no
    // APPLICATIONINSIGHTS_CONNECTION_STRING configured (§12.5 / §12.8 bullet 3).
    // -----------------------------------------------------------------

    [Fact]
    public async Task AppStartsAndServesRequests_WhenAppInsightsConnectionStringUnset()
    {
        _factory.AppInsightsConnectionString = null;

        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"ok\"", body);
    }

    [Fact]
    public async Task AppStartsAndServesRequests_WhenAppInsightsConnectionStringIsSet()
    {
        // A syntactically valid (but fake/non-routable) connection string exercises
        // the UseAzureMonitor() + managed-identity-credential branch without any
        // network egress being required for the app to start and answer /health.
        _factory.AppInsightsConnectionString =
            "InstrumentationKey=00000000-0000-0000-0000-000000000000;" +
            "IngestionEndpoint=https://eastus-0.in.applicationinsights.azure.com/;" +
            "LiveEndpoint=https://eastus.livediagnostics.monitor.azure.com/";

        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
