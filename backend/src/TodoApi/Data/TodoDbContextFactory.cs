using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TodoApi.Data;

/// <summary>
/// Design-time factory used by the EF Core CLI (e.g. `dotnet ef migrations add`).
/// It does not connect to a database — only a valid provider/connection string
/// shape is required to scaffold migrations. The runtime uses the DI-registered
/// context in <c>Program.cs</c> instead.
///
/// <para>
/// The fallback string is passwordless, matching the §14 invariant that no
/// application configuration anywhere carries a database credential. It is also
/// deliberately NOT routed through the Entra data source: scaffolding must not need
/// a token, and `dotnet ef database update` against the local `trust` container
/// needs no credential either (specs §14.6).
/// </para>
/// </summary>
public class TodoDbContextFactory : IDesignTimeDbContextFactory<TodoDbContext>
{
    public TodoDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__TodoDb")
            ?? "Host=localhost;Port=5432;Database=tododb;Username=todo;Ssl Mode=Disable";

        var options = new DbContextOptionsBuilder<TodoDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new TodoDbContext(options);
    }
}
