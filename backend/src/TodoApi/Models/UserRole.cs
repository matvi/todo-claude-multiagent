namespace TodoApi.Models;

/// <summary>
/// The two — and only two — application roles (specs §16.4.2, §16.15: "Roles stay
/// exactly two; adding a third is a spec change, not an implementation detail").
///
/// <para>
/// Persisted as its <b>name</b> (<c>'User'</c>/<c>'Admin'</c>) via
/// <c>HasConversion&lt;string&gt;()</c>, not as an integer and not as a Postgres
/// <c>ENUM</c> type: the bootstrap admin promotion (§16.11 step 13) is a hand-written
/// <c>UPDATE</c> in psql, so the column has to be readable and writable as text.
/// </para>
/// </summary>
public enum UserRole
{
    User = 0,
    Admin = 1,
}
