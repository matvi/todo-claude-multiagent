using System.ComponentModel.DataAnnotations;

namespace TodoApi.Dtos;

/// <summary>
/// Request body for a full update of a todo's mutable fields (PUT /api/todos/{id}).
/// </summary>
public class UpdateTodoRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Title { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    // Nullable + [Required] so an omitted value is a validation error rather
    // than silently defaulting to false (value types can't distinguish the two).
    [Required]
    public bool? IsCompleted { get; set; }
}
