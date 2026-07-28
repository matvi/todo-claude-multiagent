using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TodoApi.Data;
using Xunit;

namespace TodoApi.Tests;

/// <summary>
/// Unit tests for TodoApi.Data.TodoDbContextRegistration (specs.md §13.4).
///
/// IMPORTANT — scope of what these tests can and cannot prove, documented per the
/// tester's brief: there is no live Entra-auth-enabled Postgres server available in
/// this environment/CI. These tests verify the branching/config-wiring logic
/// (which auth path is selected, that neither path throws while merely being
/// *constructed*, and that the password path is the untouched regression case)
/// WITHOUT opening a real network connection or acquiring a real Entra token.
/// Token acquisition happens lazily inside Npgsql's periodic password provider only
/// when a connection is actually opened — which none of these tests do. End-to-end
/// verification against a real Entra-enabled Postgres Flexible Server can only be
/// done against the deployed environment by a human/devops, not in this test suite.
/// </summary>
public class TodoDbContextRegistrationTests
{
    private const string PasswordConnectionString =
        "Host=localhost;Port=5432;Database=tododb;Username=todo;Password=todo";

    private const string PasswordlessEntraConnectionString =
        "Host=pg-todo-demo.postgres.database.azure.com;Port=5432;Database=tododb;" +
        "Username=fake-managed-identity;Ssl Mode=Require;Trust Server Certificate=true";

    private static IConfiguration BuildConfig(string? connectionString, bool? useEntraAuth)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:TodoDb"] = connectionString,
        };
        if (useEntraAuth is not null)
        {
            values[TodoDbContextRegistration.UseEntraAuthKey] = useEntraAuth.Value.ToString();
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    // -----------------------------------------------------------------
    // Regression: default / false → original password-based connection path.
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(null)]   // flag entirely absent from config
    [InlineData(false)]  // flag explicitly false
    public void AddTodoDbContext_WithEntraAuthNotEnabled_UsesPasswordConnection_NoDataSourceRegistered(
        bool? useEntraAuth)
    {
        var services = new ServiceCollection();
        var configuration = BuildConfig(PasswordConnectionString, useEntraAuth);

        services.AddTodoDbContext(configuration);
        using var provider = services.BuildServiceProvider();

        // The Entra path registers an NpgsqlDataSource singleton; the password path
        // must NOT — this proves the branch actually taken, not just that "something"
        // resolves.
        var dataSource = provider.GetService<NpgsqlDataSource>();
        Assert.Null(dataSource);

        var context = provider.GetRequiredService<TodoDbContext>();
        Assert.Equal(PasswordConnectionString, context.Database.GetConnectionString());
    }

    // -----------------------------------------------------------------
    // Entra auth path: wiring/branching only (no live token/DB call).
    // -----------------------------------------------------------------

    [Fact]
    public void AddTodoDbContext_WithEntraAuthTrue_RegistersNpgsqlDataSourceSingleton_ConstructsWithoutThrowing()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfig(PasswordlessEntraConnectionString, useEntraAuth: true);

        var registrationException = Record.Exception(() => services.AddTodoDbContext(configuration));
        Assert.Null(registrationException);

        using var provider = services.BuildServiceProvider();

        // Resolving the NpgsqlDataSource exercises DefaultAzureCredential construction
        // + NpgsqlDataSourceBuilder.UsePeriodicPasswordProvider(...).Build() — none of
        // which perform network I/O or token acquisition until a connection is opened.
        var buildException = Record.Exception(() => provider.GetRequiredService<NpgsqlDataSource>());
        Assert.Null(buildException);

        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();
        Assert.NotNull(dataSource);

        // TodoDbContext must resolve over that data source without throwing (DbContext
        // construction does not open a connection).
        var contextException = Record.Exception(() => provider.GetRequiredService<TodoDbContext>());
        Assert.Null(contextException);
    }

    [Fact]
    public void AddTodoDbContext_EntraAuthTrue_DoesNotRegisterAPlainPasswordDbContext()
    {
        // Sanity check on the branching: when Entra auth is on, the DbContext must be
        // built over the shared NpgsqlDataSource singleton (not a second, independent
        // UseNpgsql(connectionString) registration) so there aren't two conflicting
        // provider configurations.
        var services = new ServiceCollection();
        var configuration = BuildConfig(PasswordlessEntraConnectionString, useEntraAuth: true);

        services.AddTodoDbContext(configuration);

        // Exactly one NpgsqlDataSource registration should exist.
        var dataSourceDescriptors = services.Count(d => d.ServiceType == typeof(NpgsqlDataSource));
        Assert.Equal(1, dataSourceDescriptors);
    }

    // -----------------------------------------------------------------
    // Existing guard behavior must survive the refactor into the extension method.
    // -----------------------------------------------------------------

    [Fact]
    public void AddTodoDbContext_MissingConnectionString_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfig(connectionString: null, useEntraAuth: null);

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddTodoDbContext(configuration));

        Assert.Contains("TodoDb", exception.Message);
    }

    [Fact]
    public void AddTodoDbContext_BlankConnectionString_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfig(connectionString: "   ", useEntraAuth: null);

        Assert.Throws<InvalidOperationException>(() => services.AddTodoDbContext(configuration));
    }
}
