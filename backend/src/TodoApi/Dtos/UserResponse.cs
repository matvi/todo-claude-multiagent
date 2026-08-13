using TodoApi.Models;

namespace TodoApi.Dtos;

/// <summary>
/// A user profile as returned by the API (specs §16.8). Serialized camelCase, consistent
/// with the existing todo DTOs.
///
/// <para>
/// Contains PII (email, display name, phone). It is only ever returned to the owner of the
/// row or to an <c>Admin</c>, and must never be written to logs or telemetry (§16.10).
/// </para>
/// </summary>
public sealed record UserResponse(
    Guid Id,
    Guid EntraObjectId,
    string? Email,
    string? DisplayName,
    string? PhoneNumber,
    string Role,
    DateTimeOffset CreatedAt)
{
    public static UserResponse FromEntity(AppUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new UserResponse(
            user.Id,
            user.EntraObjectId,
            user.Email,
            user.DisplayName,
            user.PhoneNumber,
            user.Role.ToString(),
            user.CreatedAt);
    }
}
