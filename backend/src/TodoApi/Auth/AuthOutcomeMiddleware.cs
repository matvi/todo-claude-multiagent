using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace TodoApi.Auth;

/// <summary>
/// Turns a <see cref="RoleClaimsTransformation"/> failure into an HTTP status <b>before</b>
/// authorization and before any endpoint runs (specs §16.7.3 steps 3–4, §16.8, §16.10).
///
/// <para>
/// Without this, a token that passed signature validation but carries the wrong
/// <c>tid</c>, or no usable <c>oid</c>, would still satisfy
/// <c>RequireAuthenticatedUser()</c> — it would simply have no role. That must be a
/// <c>401</c>, not "an authenticated user with no role". Likewise an unreachable role
/// store must be a <c>503</c>, never a silently role-less (or defaulted) principal.
/// </para>
///
/// <para>
/// Endpoints marked <c>[AllowAnonymous]</c> (only <c>/health</c>) are skipped so a probe —
/// or a caller who happens to present a broken token — can never take the health endpoint
/// down.
/// </para>
/// </summary>
public sealed class AuthOutcomeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AuthOutcomeMiddleware> _logger;

    public AuthOutcomeMiddleware(RequestDelegate next, ILogger<AuthOutcomeMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var failure = context.User?.FindFirst(TodoClaimTypes.AuthFailure)?.Value;

        if (failure is null ||
            context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await _next(context);
            return;
        }

        if (failure == TodoClaimTypes.FailureRoleSourceUnavailable)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "Service Unavailable",
                "Authorization data is temporarily unavailable. Please retry.");
            return;
        }

        // Security event: an authenticated caller was refused. Path and method only — never
        // the token, the Authorization header, or any claim value (§16.10).
        _logger.LogWarning(
            "Rejected a request with 401: the presented token did not identify a valid user " +
            "({Method} {Path}).",
            context.Request.Method,
            context.Request.Path);

        context.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";

        await WriteProblemAsync(
            context,
            StatusCodes.Status401Unauthorized,
            "Unauthorized",
            "The access token did not identify a valid user for this application.");
    }

    /// <summary>
    /// Writes a <see cref="ProblemDetails"/> body matching the API's existing error
    /// convention. Deliberately generic: it never reveals which check failed, whether a
    /// resource exists, or anything about the token.
    /// </summary>
    private static Task WriteProblemAsync(
        HttpContext context,
        int statusCode,
        string title,
        string detail)
    {
        // Results.Problem writes an RFC 7807 body with content type application/problem+json
        // and sets the status code, using the framework's own serializer — no hand-built
        // JSON, and consistent with the ProblemDetails the controllers return.
        return Results.Problem(detail: detail, statusCode: statusCode, title: title)
            .ExecuteAsync(context);
    }
}

public static class AuthOutcomeMiddlewareExtensions
{
    /// <summary>
    /// Must be registered between <c>UseAuthentication()</c> and <c>UseAuthorization()</c>:
    /// after the principal (and therefore the claims transformation) exists, before any
    /// authorization decision is made.
    /// </summary>
    public static IApplicationBuilder UseAuthOutcome(this IApplicationBuilder app) =>
        app.UseMiddleware<AuthOutcomeMiddleware>();
}
