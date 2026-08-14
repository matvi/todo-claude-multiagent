using System.ComponentModel.DataAnnotations;

namespace TodoApi.Dtos;

/// <summary>
/// Body of <c>PATCH /api/users/{id}/role</c> (specs §16.8). Admin-only endpoint.
///
/// <para>
/// The value must be exactly <c>"User"</c> or <c>"Admin"</c>; anything else is a
/// <c>400</c>. Validated case-sensitively against the enum names so the persisted value
/// always matches the database CHECK constraint.
/// </para>
/// </summary>
public sealed class UpdateUserRoleRequest
{
    [Required]
    public string? Role { get; init; }
}
