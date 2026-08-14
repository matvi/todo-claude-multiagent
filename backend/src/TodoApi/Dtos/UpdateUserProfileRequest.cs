using System.ComponentModel.DataAnnotations;

namespace TodoApi.Dtos;

/// <summary>
/// Body of <c>PUT /api/users/me</c> (specs §16.8).
///
/// <para>
/// <b>This DTO exists precisely because the entity must never be model-bound.</b> It
/// carries only the two self-service-mutable fields; <c>role</c>, <c>email</c>, <c>id</c>
/// and <c>entraObjectId</c> have no properties here, so a client that sends them cannot
/// change them (mass-assignment / privilege escalation guard). Do not add a Role property
/// to this type — promotion is <c>PATCH /api/users/{id}/role</c>, Admin-only.
/// </para>
/// </summary>
public sealed class UpdateUserProfileRequest
{
    [MaxLength(256)]
    public string? DisplayName { get; init; }

    [MaxLength(64)]
    public string? PhoneNumber { get; init; }
}
