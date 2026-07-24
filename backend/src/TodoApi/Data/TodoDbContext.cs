using Microsoft.EntityFrameworkCore;
using TodoApi.Models;

namespace TodoApi.Data;

public class TodoDbContext : DbContext
{
    public TodoDbContext(DbContextOptions<TodoDbContext> options) : base(options)
    {
    }

    public DbSet<Todo> Todos => Set<Todo>();

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
    }
}
