using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web.Resource;
using TodoApi.Data;
using TodoApi.Dtos;
using TodoApi.Models;

namespace TodoApi.Controllers;

/// <summary>
/// Todo CRUD. Payload contracts are unchanged from specs §3.5 — this cycle changes
/// <b>attributes only</b> (§16.7.2a).
///
/// <para>
/// Todos remain a single shared list visible to every authenticated user: there is
/// deliberately no per-user ownership in this cycle (§16.4.3).
/// </para>
///
/// <para>
/// <c>[Authorize]</c> is redundant with the authorization fallback policy but states the
/// intent locally. <c>[RequiredScope]</c> is <b>not</b> redundant: audience validation only
/// proves the token was minted for this API, not that the user consented to the delegated
/// permission. Without it a token for this audience but without <c>Todos.ReadWrite</c>
/// would still reach these endpoints. The required scope is read from configuration
/// (<c>AzureAd:Scopes</c>) rather than hard-coded. Insufficient scope ⇒ 403.
/// </para>
/// </summary>
[ApiController]
[Route("api/todos")]
[Produces("application/json")]
[Authorize]
[RequiredScope(RequiredScopesConfigurationKey = "AzureAd:Scopes")]
public class TodosController : ControllerBase
{
    private readonly TodoDbContext _db;

    public TodosController(TodoDbContext db)
    {
        _db = db;
    }

    /// <summary>List all todos, newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<TodoResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<TodoResponse>>> GetTodos(CancellationToken ct)
    {
        var todos = await _db.Todos
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

        return Ok(todos.Select(TodoResponse.FromEntity));
    }

    /// <summary>Get a single todo by id.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(TodoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TodoResponse>> GetTodo(string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var todoId))
        {
            return MalformedId(id);
        }

        var todo = await _db.Todos.FindAsync([todoId], ct);
        if (todo is null)
        {
            return NotFound();
        }

        return Ok(TodoResponse.FromEntity(todo));
    }

    /// <summary>Create a new todo.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(TodoResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TodoResponse>> CreateTodo(
        CreateTodoRequest request,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var todo = new Todo
        {
            Id = Guid.NewGuid(),
            Title = request.Title.Trim(),
            Description = request.Description,
            IsCompleted = false,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Todos.Add(todo);
        await _db.SaveChangesAsync(ct);

        var response = TodoResponse.FromEntity(todo);
        return CreatedAtAction(nameof(GetTodo), new { id = todo.Id }, response);
    }

    /// <summary>Full update of a todo's mutable fields.</summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(TodoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TodoResponse>> UpdateTodo(
        string id,
        UpdateTodoRequest request,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var todoId))
        {
            return MalformedId(id);
        }

        var todo = await _db.Todos.FindAsync([todoId], ct);
        if (todo is null)
        {
            return NotFound();
        }

        todo.Title = request.Title.Trim();
        todo.Description = request.Description;
        todo.IsCompleted = request.IsCompleted!.Value;
        todo.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        return Ok(TodoResponse.FromEntity(todo));
    }

    /// <summary>Delete a todo.</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTodo(string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var todoId))
        {
            return MalformedId(id);
        }

        var todo = await _db.Todos.FindAsync([todoId], ct);
        if (todo is null)
        {
            return NotFound();
        }

        _db.Todos.Remove(todo);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    /// <summary>
    /// Builds a 400 ProblemDetails response for a route id segment that is not a
    /// valid Guid. Spec §3.5: "{id} is a Guid; malformed ids return 400." The
    /// :guid route constraint is intentionally omitted so the action is invoked
    /// (rather than routing returning a bare 404) and can emit this response.
    /// The ValidationProblemDetails shape (errors dictionary keyed by "id") lets
    /// the frontend's existing error extractor surface a useful message.
    /// </summary>
    private BadRequestObjectResult MalformedId(string id)
    {
        ModelState.AddModelError(nameof(id), $"'{id}' is not a valid id.");
        return BadRequest(new ValidationProblemDetails(ModelState));
    }
}
