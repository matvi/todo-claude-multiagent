using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TodoApi.Data;

namespace TodoApi.Tests;

/// <summary>
/// WebApplicationFactory that boots the real ASP.NET Core pipeline (routing,
/// model validation, controllers, CORS, /health) but swaps the Npgsql-backed
/// TodoDbContext for a uniquely-named EF Core InMemory database, since no
/// real Postgres instance is available in this environment.
///
/// A dummy (never-dialled) Postgres connection string is also injected via
/// configuration purely so Program.cs's startup guard
/// ("connection string must be configured") does not throw before the
/// DbContext registration is replaced below.
/// </summary>
public class TodoApiFactory : WebApplicationFactory<Program>
{
    public readonly string DatabaseName = $"TodoApiTests-{Guid.NewGuid()}";

    /// <summary>Captures logged exceptions so test failures can report the real cause.</summary>
    public readonly ConcurrentQueue<string> LoggedErrors = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Passwordless by contract (specs §14): app config never carries a
                // credential. Never dialled — the DbContext registration below is
                // replaced with InMemory and the NpgsqlDataSource factory is never
                // resolved.
                ["ConnectionStrings:TodoDb"] =
                    "Host=localhost;Port=5432;Database=unused;Username=unused",
                ["Cors:AllowedOrigins:0"] = "http://localhost:5173",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Purge every service descriptor related to TodoDbContext that the
            // app's own AddDbContext<TodoDbContext>(UseNpgsql) call registered.
            // Recent EF Core versions register more than just
            // DbContextOptions<TodoDbContext> (e.g. IDbContextOptionsConfiguration<TodoDbContext>),
            // so removing only the options descriptor leaves the Npgsql provider
            // configuration attached and causes a "two providers registered" error
            // when a second provider (InMemory) is added below.
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

            services.AddLogging(logging =>
                logging.AddProvider(new CapturingLoggerProvider(LoggedErrors)));
        });
    }
}

file sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, sink);

    public void Dispose() { }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            sink.Enqueue($"[{logLevel}] {category}: {formatter(state, exception)}" +
                         (exception is null ? string.Empty : $"\n{exception}"));
        }
    }
}
