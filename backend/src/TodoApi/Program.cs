using Microsoft.EntityFrameworkCore;
using TodoApi.Data;

var builder = WebApplication.CreateBuilder(args);

// ---- Services ----
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var connectionString = builder.Configuration.GetConnectionString("TodoDb");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'TodoDb' is not configured. Set ConnectionStrings:TodoDb " +
        "(or the ConnectionStrings__TodoDb environment variable).");
}

builder.Services.AddDbContext<TodoDbContext>(options =>
    options.UseNpgsql(connectionString));

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
