namespace TodoApi.Auth;

/// <summary>
/// Claim types this application adds itself. They are namespaced with a <c>todo:</c>
/// prefix so they can never collide with (or be spoofed by) a claim minted by the
/// identity provider — <see cref="RoleClaimsTransformation"/> rebuilds the identity from
/// the validated token's claims and is the only writer of these types.
/// </summary>
public static class TodoClaimTypes
{
    /// <summary>
    /// Marker proving the role claim on the principal was read from PostgreSQL by
    /// <see cref="RoleClaimsTransformation"/> (value <see cref="RoleSourceDatabase"/>).
    /// Doubles as the idempotence guard: <c>IClaimsTransformation.TransformAsync</c> may be
    /// invoked more than once per request (specs §16.7.3 step 2).
    /// </summary>
    public const string RoleSource = "todo:role_source";

    /// <summary>The only accepted value of <see cref="RoleSource"/>.</summary>
    public const string RoleSourceDatabase = "db";

    /// <summary>
    /// Present when the principal was authenticated by the JWT handler but must NOT be
    /// treated as a user of this application. <see cref="AuthOutcomeMiddleware"/> turns it
    /// into a 401/503 before authorization runs — the request never reaches an endpoint.
    /// Values: <see cref="FailureInvalidPrincipal"/>, <see cref="FailureRoleSourceUnavailable"/>.
    /// </summary>
    public const string AuthFailure = "todo:auth_failure";

    /// <summary>
    /// Wrong tenant (<c>tid</c>), or no usable <c>oid</c>. Maps to <c>401</c> (§16.8).
    /// </summary>
    public const string FailureInvalidPrincipal = "invalid_principal";

    /// <summary>
    /// The role could not be read from PostgreSQL. Maps to <c>503</c> — the request fails
    /// <b>closed</b>; it never degrades to "assume User" and categorically never to
    /// "assume Admin" (§16.10, availability/failure modes).
    /// </summary>
    public const string FailureRoleSourceUnavailable = "role_source_unavailable";
}
