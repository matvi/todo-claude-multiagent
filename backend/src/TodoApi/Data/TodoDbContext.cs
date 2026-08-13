using Microsoft.EntityFrameworkCore;
using TodoApi.Models;

namespace TodoApi.Data;

public class TodoDbContext : DbContext
{
    public TodoDbContext(DbContextOptions<TodoDbContext> options) : base(options)
    {
    }

    public DbSet<Todo> Todos => Set<Todo>();

    /// <summary>
    /// Application users (specs §16.4). Reached over the <b>same single</b> Entra-authenticated
    /// <c>NpgsqlDataSource</c> as <see cref="Todos"/> (§14) — there is no second connection
    /// string and no second DB auth path.
    /// </summary>
    public DbSet<AppUser> Users => Set<AppUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Todo>(entity =>
        {
            entity.ToTable("todos");

            entity.HasKey(t => t.Id);

            entity.Property(t => t.Id)
                .HasColumnName("id");

            entity.Property(t => t.Title)
                .HasColumnName("title")
                .HasColumnType("varchar(200)")
                .IsRequired();

            entity.Property(t => t.Description)
                .HasColumnName("description")
                .HasColumnType("varchar(2000)");

            entity.Property(t => t.IsCompleted)
                .HasColumnName("is_completed")
                .IsRequired()
                .HasDefaultValue(false);

            entity.Property(t => t.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz")
                .IsRequired()
                .HasDefaultValueSql("now()");

            entity.Property(t => t.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz")
                .IsRequired()
                .HasDefaultValueSql("now()");

            entity.HasIndex(t => t.CreatedAt)
                .HasDatabaseName("ix_todos_created_at")
                .IsDescending();
        });

        // users (specs §16.4). Additive only — the todos mapping above is untouched, and
        // todos deliberately gets NO owner_id/FK: todos stay a shared list (§16.4.3).
        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.ToTable(
                "users",
                t => t.HasCheckConstraint("ck_users_role", "role IN ('User','Admin')"));

            entity.HasKey(u => u.Id);

            entity.Property(u => u.Id)
                .HasColumnName("id");

            entity.Property(u => u.EntraObjectId)
                .HasColumnName("entra_object_id")
                .IsRequired();

            entity.Property(u => u.Email)
                .HasColumnName("email")
                .HasColumnType("text");

            entity.Property(u => u.DisplayName)
                .HasColumnName("display_name")
                .HasColumnType("text");

            entity.Property(u => u.PhoneNumber)
                .HasColumnName("phone_number")
                .HasColumnType("text");

            // Stored as text ('User'/'Admin') so the one-time bootstrap promotion in psql
            // (§16.11 step 13) is human-readable, and so the CHECK constraint above is
            // meaningful. Never a Postgres ENUM type (§16.4.2).
            entity.Property(u => u.Role)
                .HasColumnName("role")
                .HasColumnType("text")
                .HasConversion<string>()
                .IsRequired()
                .HasDefaultValue(UserRole.User);

            entity.Property(u => u.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz")
                .IsRequired()
                .HasDefaultValueSql("now()");

            entity.Property(u => u.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz")
                .IsRequired()
                .HasDefaultValueSql("now()");

            // Both the per-request lookup path AND the race guard that makes concurrent
            // first-login provisioning across replicas safe (§16.4.1, §16.7.5).
            entity.HasIndex(u => u.EntraObjectId)
                .HasDatabaseName("ix_users_entra_object_id")
                .IsUnique();
        });
    }
}
