using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web.Resource;
using TodoApi.Auth;
using TodoApi.Data;
using TodoApi.Dtos;
using TodoApi.Models;

namespace TodoApi.Controllers;

/// <summary>
/// Profile and role administration (specs §16.8).
///
/// <para>
/// Every action is authenticated (the fallback policy would cover it anyway; the explicit
/// <c>[Authorize]</c> keeps the intent local and readable) and requires the delegated scope
/// configured in <c>AzureAd:Scopes</c>. The two admin actions additionally require the
/// <see cref="AuthenticationRegistration.AdminOnlyPolicy"/> policy, which matches the role
/// claim that <see cref="RoleClaimsTransformation"/> re-derived from PostgreSQL on this
/// very request — never a role asserted by the client or carried in the token.
/// </para>
/// </summary>
[ApiController]
[Route("api/users")]
[Produces("application/json")]
[Authorize]
[RequiredScope(RequiredScopesConfigurationKey = "AzureAd:Scopes")]
public class UsersController : ControllerBase
{
    private readonly TodoDbContext _db;
    private readonly ILogger<UsersController> _logger;

    public UsersController(TodoDbContext db, ILogger<UsersController> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The caller's own provisioned profile and role.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserResponse>> GetMe(CancellationToken ct)
    {
        var (user, failure) = await LoadCallerAsync(ct);
        if (failure is not null)
        {
            return failure;
        }

        return Ok(UserResponse.FromEntity(user!));
    }

    /// <summary>
    /// Self-service profile update. Only <c>displayName</c> and <c>phoneNumber</c> are
    /// mutable — see <see cref="UpdateUserProfileRequest"/> for why the entity is never
    /// bound directly.
    /// </summary>
    [HttpPut("me")]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserResponse>> UpdateMe(
        UpdateUserProfileRequest request,
        CancellationToken ct)
    {
        var (user, failure) = await LoadCallerAsync(ct);
        if (failure is not null)
        {
            return failure;
        }

        user!.DisplayName = Normalize(request.DisplayName);
        user.PhoneNumber = Normalize(request.PhoneNumber);
        user.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        return Ok(UserResponse.FromEntity(user));
    }

    /// <summary>All users, oldest first. Admin-only (demo scale: unpaged).</summary>
    [HttpGet]
    [Authorize(Policy = AuthenticationRegistration.AdminOnlyPolicy)]
    [ProducesResponseType(typeof(IEnumerable<UserResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IEnumerable<UserResponse>>> GetUsers(CancellationToken ct)
    {
        var users = await _db.Users
            .OrderBy(u => u.CreatedAt)
            .ToListAsync(ct);

        return Ok(users.Select(UserResponse.FromEntity));
    }

    /// <summary>Promote or demote a user. Admin-only.</summary>
    [HttpPatch("{id}/role")]
    [Authorize(Policy = AuthenticationRegistration.AdminOnlyPolicy)]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserResponse>> UpdateRole(
        string id,
        UpdateUserRoleRequest request,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var userId))
        {
            return MalformedId(id);
        }

        // Exactly "User" or "Admin" — case-sensitive, so the stored value always satisfies
        // the ck_users_role CHECK constraint.
        if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: false, out var newRole) ||
            !Enum.IsDefined(newRole))
        {
            ModelState.AddModelError(
                nameof(request.Role),
                "Role must be exactly 'User' or 'Admin'.");
            return BadRequest(new ValidationProblemDetails(ModelState));
        }

        var target = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (target is null)
        {
            return NotFound();
        }

        var actorObjectId = User.GetEntraObjectId();

        // Self-demotion would instantly lock the only admin out of the admin UI.
        if (newRole == UserRole.User && target.EntraObjectId == actorObjectId)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Conflict",
                Detail = "You cannot remove your own Admin role.",
            });
        }

        // Never leave the system with zero admins: recovery would mean hand-editing the
        // database again (§16.11 step 13).
        if (newRole == UserRole.User && target.Role == UserRole.Admin)
        {
            var remainingAdmins = await _db.Users
                .CountAsync(u => u.Role == UserRole.Admin && u.Id != target.Id, ct);

            if (remainingAdmins == 0)
            {
                return Conflict(new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Conflict",
                    Detail = "There must be at least one Admin.",
                });
            }
        }

        var previousRole = target.Role;
        if (previousRole != newRole)
        {
            target.Role = newRole;
            target.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            // Audit trail for a security-relevant change. Object ids only — no PII (§16.8).
            _logger.LogInformation(
                "Role change: {ActorOid} set user {TargetId} from {OldRole} to {NewRole}.",
                actorObjectId,
                target.Id,
                previousRole,
                newRole);
        }

        return Ok(UserResponse.FromEntity(target));
    }

    /// <summary>
    /// Loads the row belonging to the caller. The row is normally created by
    /// <see cref="RoleClaimsTransformation"/> before the request reaches here; a missing
    /// row or a principal without a usable <c>oid</c> therefore means the request must not
    /// be served, so it fails closed with 401 rather than inventing a profile.
    /// </summary>
    private async Task<(AppUser? User, ActionResult? Failure)> LoadCallerAsync(CancellationToken ct)
    {
        var objectId = User.GetEntraObjectId();
        if (objectId is null)
        {
            return (null, UnauthorizedProblem());
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.EntraObjectId == objectId, ct);

        return user is null
            ? (null, UnauthorizedProblem())
            : (user, null);
    }

    private ObjectResult UnauthorizedProblem()
    {
        Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";

        return StatusCode(
            StatusCodes.Status401Unauthorized,
            new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Unauthorized",
                Detail = "The access token did not identify a valid user for this application.",
            });
    }

    /// <summary>
    /// 400 for a route id segment that is not a Guid, matching the existing
    /// <c>TodosController.MalformedId</c> convention so the frontend error extractor keeps
    /// working. The <c>:guid</c> route constraint is deliberately omitted (see §3.5).
    /// </summary>
    private BadRequestObjectResult MalformedId(string id)
    {
        ModelState.AddModelError(nameof(id), $"'{id}' is not a valid id.");
        return BadRequest(new ValidationProblemDetails(ModelState));
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
