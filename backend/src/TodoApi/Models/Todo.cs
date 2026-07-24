namespace TodoApi.Models;

/// <summary>
/// EF Core entity mapped to the <c>todos</c> table.
/// </summary>
public class Todo
{
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsCompleted { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
