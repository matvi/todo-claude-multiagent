using Microsoft.EntityFrameworkCore;
using TodoApi.Data;
using TodoApi.Observability;

var builder = WebApplication.CreateBuilder(args);

// ---- Services ----
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Distributed tracing → Application Insights (OpenTelemetry). No-ops cleanly when
// APPLICATIONINSIGHTS_CONNECTION_STRING is unset (local dev / CI). See specs §12/§13.5.
builder.Services.AddTodoTelemetry(builder.Configuration);

// PostgreSQL DbContext. Uses password auth by default; managed-identity (Entra) auth
// when Postgres__UseEntraAuth=true (Azure). See specs §13.4.
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

app.UseCors(CorsPolicyName);

// Health endpoint — not gated behind CORS/auth. Used by Container Apps probes.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
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
