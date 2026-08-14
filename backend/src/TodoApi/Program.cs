using Microsoft.EntityFrameworkCore;
using TodoApi.Auth;
using TodoApi.Data;
using TodoApi.Observability;

var builder = WebApplication.CreateBuilder(args);

// ---- Services ----
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// End-user authentication (Entra External ID access tokens) and authorization
// (roles re-derived from PostgreSQL on every request). Specs §16.7.
// AddTodoAuthentication fails fast when any AzureAd:* key is missing/blank (§16.7.1):
// an API that boots without auth configuration is an API that serves unauthenticated
// traffic, which is exactly the failure mode this cycle exists to remove.
builder.Services.AddTodoAuthentication(builder.Configuration);
builder.Services.AddTodoAuthorization();

// Distributed tracing → Application Insights (OpenTelemetry). No-ops cleanly when
// APPLICATIONINSIGHTS_CONNECTION_STRING is unset (local dev / CI). See specs §12/§13.5.
builder.Services.AddTodoTelemetry(builder.Configuration);

// PostgreSQL DbContext. Entra / managed-identity auth only (specs §14).
builder.Services.AddTodoDbContext(builder.Configuration);

// CORS: restrict to the configured frontend origin(s).
const string CorsPolicyName = "frontend";
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [];

builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicyName, policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

// ---- Middleware pipeline ----
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Middleware order is load-bearing (specs §16.7.6).
// UseCors MUST stay first: it answers preflights before authentication runs, and puts
// CORS headers on 401/403 responses so the browser surfaces a real status instead of an
// opaque failure.
app.UseCors(CorsPolicyName);

app.UseAuthentication();

// Between authentication and authorization: turns a RoleClaimsTransformation failure
// (wrong tenant / no usable oid ⇒ 401, role store unreachable ⇒ 503) into a response
// before any authorization decision is made. Endpoints marked [AllowAnonymous] are
// skipped, so a caller with a broken token can never take /health down.
app.UseAuthOutcome();

app.UseAuthorization();

// Health endpoint — no token required. Container Apps probes cannot present one, and
// gating it would take the app down. The explicit AllowAnonymous is required because the
// authorization fallback policy makes everything else authenticated by default (§16.7.7).
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous()
    .WithName("Health");

app.MapControllers();

// Apply EF Core migrations at startup (demo convenience; see specs §3.4).
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
        db.Database.Migrate();
        logger.LogInformation("Database migrations applied successfully.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to apply database migrations at startup.");
        // Do not crash the app on migration failure so /health can still report;
        // the failure is logged for diagnosis.
    }
}

app.Run();

// Exposed so integration tests (WebApplicationFactory) can reference the entry point.
public partial class Program { }
