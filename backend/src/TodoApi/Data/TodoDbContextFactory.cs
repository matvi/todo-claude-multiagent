using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TodoApi.Data;

/// <summary>
/// Design-time factory used by the EF Core CLI (e.g. `dotnet ef migrations add`).
/// It does not connect to a database — only a valid provider/connection string
/// shape is required to scaffold migrations. The runtime uses the DI-registered
/// context in <c>Program.cs</c> instead.
/// </summary>
public class TodoDbContextFactory : IDesignTimeDbContextFactory<TodoDbContext>
{
    public TodoDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__TodoDb")
            ?? "Host=localhost;Port=5432;Database=tododb;Username=todo;Password=todo";

        var options = new DbContextOptionsBuilder<TodoDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new TodoDbContext(options);
    }
}
