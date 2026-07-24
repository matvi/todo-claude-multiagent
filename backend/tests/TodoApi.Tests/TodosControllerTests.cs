using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace TodoApi.Tests;

/// <summary>
/// Wire-level shape used only by tests to deserialize TodoResponse JSON
/// (camelCase, per spec §3.5).
/// </summary>
public record TodoResponseDto(
    Guid Id,
    string Title,
    string? Description,
    bool IsCompleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public class TodosControllerTests : IDisposable
{
    private readonly TodoApiFactory _factory;
    private readonly HttpClient _client;

    public TodosControllerTests()
    {
        _factory = new TodoApiFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private async Task<TodoResponseDto> CreateTodoAsync(string title = "Buy milk", string? description = "2%")
    {
        var response = await _client.PostAsync("/api/todos", Json(new { title, description }));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TodoResponseDto>())!;
    }

    // ---------------------------------------------------------------
    // GET /health
    // ---------------------------------------------------------------

    [Fact]
    public async Task Health_ReturnsOkWithStatusOk()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    // ---------------------------------------------------------------
    // POST /api/todos (create)
    // ---------------------------------------------------------------

    [Fact]
    public async Task CreateTodo_WithValidBody_Returns201WithLocationAndBody()
    {
        var response = await _client.PostAsync(
            "/api/todos",
            Json(new { title = "Buy milk", description = "2%, from the corner store" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var body = await response.Content.ReadFromJsonAsync<TodoResponseDto>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.Id);
        Assert.Equal("Buy milk", body.Title);
        Assert.Equal("2%, from the corner store", body.Description);
        Assert.False(body.IsCompleted);
        Assert.True(body.CreatedAt <= DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal(body.CreatedAt, body.UpdatedAt);
    }

    [Fact]
    public async Task CreateTodo_WithoutDescription_SucceedsWithNullDescription()
    {
        var response = await _client.PostAsync("/api/todos", Json(new { title = "No description todo" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TodoResponseDto>();
        Assert.Null(body!.Description);
    }

    [Fact]
    public async Task CreateTodo_WithEmptyTitle_Returns400ValidationProblem()
    {
        var response = await _client.PostAsync("/api/todos", Json(new { title = "" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Title", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateTodo_WithMissingTitle_Returns400ValidationProblem()
    {
        var response = await _client.PostAsync("/api/todos", Json(new { description = "no title supplied" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateTodo_WithTitleOver200Chars_Returns400()
    {
        var response = await _client.PostAsync("/api/todos", Json(new { title = new string('a', 201) }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateTodo_WithTitleExactly200Chars_Succeeds()
    {
        var response = await _client.PostAsync("/api/todos", Json(new { title = new string('a', 200) }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateTodo_WithDescriptionOver2000Chars_Returns400()
    {
        var response = await _client.PostAsync(
            "/api/todos",
            Json(new { title = "Valid title", description = new string('b', 2001) }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateTodo_WithMalformedJsonBody_Returns400()
    {
        var response = await _client.PostAsync(
            "/api/todos",
            new StringContent("{ not valid json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Spec §3.5: "title: required, 1–200 chars after trim." A whitespace-only
    /// title correctly triggers a 400: ASP.NET Core's [Required] attribute
    /// trims strings internally before checking length (when AllowEmptyStrings
    /// is false, the default), so this is NOT a bug — verified here as a
    /// positive regression guard.
    /// </summary>
    [Fact]
    public async Task CreateTodo_WithWhitespaceOnlyTitle_Returns400()
    {
        var response = await _client.PostAsync("/api/todos", Json(new { title = "   " }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------
    // GET /api/todos (list)
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetTodos_WhenEmpty_ReturnsEmptyArray()
    {
        var response = await _client.GetAsync("/api/todos");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<TodoResponseDto>>();
        Assert.NotNull(body);
        Assert.Empty(body!);
    }

    [Fact]
    public async Task GetTodos_ReturnsAllCreatedTodos_NewestFirst()
    {
        var first = await CreateTodoAsync("First todo");
        await Task.Delay(15); // ensure distinguishable CreatedAt ordering
        var second = await CreateTodoAsync("Second todo");
        await Task.Delay(15);
        var third = await CreateTodoAsync("Third todo");

        var response = await _client.GetAsync("/api/todos");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<List<TodoResponseDto>>();
        Assert.NotNull(body);
        Assert.Equal(3, body!.Count);
        Assert.Equal(third.Id, body[0].Id);
        Assert.Equal(second.Id, body[1].Id);
        Assert.Equal(first.Id, body[2].Id);
    }

    // ---------------------------------------------------------------
    // GET /api/todos/{id}
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetTodo_WithExistingId_Returns200WithBody()
    {
        var created = await CreateTodoAsync("Fetch me", "desc");

        var response = await _client.GetAsync($"/api/todos/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TodoResponseDto>();
        Assert.Equal(created.Id, body!.Id);
        Assert.Equal("Fetch me", body.Title);
    }

    [Fact]
    public async Task GetTodo_WithUnknownId_Returns404()
    {
        var response = await _client.GetAsync($"/api/todos/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Spec §3.5: "{id} is a Guid; malformed ids return 400." The action is
    /// declared as [HttpGet("{id:guid}")]. When the URL segment fails the
    /// :guid route constraint, ASP.NET Core routing treats it as "no route
    /// matched" rather than invoking the action with a model-binding error,
    /// which produces 404, not 400. This test documents that mismatch.
    /// </summary>
    [Fact]
    public async Task GetTodo_WithMalformedId_ShouldReturn400PerSpec()
    {
        var response = await _client.GetAsync("/api/todos/not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Cycle 2 hardening test: a URL-encoded whitespace-only id segment
    /// (decodes to "   ") must still fail Guid.TryParse and hit the same
    /// MalformedId 400 path, not be treated as an "empty"/missing segment
    /// that falls through to routing 404 or matches a different route.
    /// </summary>
    [Fact]
    public async Task GetTodo_WithWhitespaceOnlyId_Returns400()
    {
        var response = await _client.GetAsync("/api/todos/%20%20%20");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Cycle 2 hardening test: an extremely long garbage id segment must
    /// still be rejected with 400 (Guid.TryParse failure), not cause a
    /// server error or an unhandled exception from routing/binding.
    /// </summary>
    [Fact]
    public async Task GetTodo_WithExtremelyLongGarbageId_Returns400()
    {
        var garbage = new string('a', 2000);

        var response = await _client.GetAsync($"/api/todos/{garbage}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Same route-constraint mismatch as above, exercised via PUT.</summary>
    [Fact]
    public async Task UpdateTodo_WithMalformedId_ShouldReturn400PerSpec()
    {
        var response = await _client.PutAsync(
            "/api/todos/not-a-guid",
            Json(new { title = "x", description = (string?)null, isCompleted = false }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Same route-constraint mismatch as above, exercised via DELETE.</summary>
    [Fact]
    public async Task DeleteTodo_WithMalformedId_ShouldReturn400PerSpec()
    {
        var response = await _client.DeleteAsync("/api/todos/not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------
    // PUT /api/todos/{id}
    // ---------------------------------------------------------------

    [Fact]
    public async Task UpdateTodo_WithExistingIdAndValidBody_Returns200AndPersists()
    {
        var created = await CreateTodoAsync("Old title", "old desc");

        var response = await _client.PutAsync(
            $"/api/todos/{created.Id}",
            Json(new { title = "New title", description = (string?)null, isCompleted = true }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TodoResponseDto>();
        Assert.Equal("New title", body!.Title);
        Assert.Null(body.Description);
        Assert.True(body.IsCompleted);
        Assert.True(body.UpdatedAt >= body.CreatedAt);

        // verify persisted via a fresh GET
        var getResponse = await _client.GetAsync($"/api/todos/{created.Id}");
        var getBody = await getResponse.Content.ReadFromJsonAsync<TodoResponseDto>();
        Assert.Equal("New title", getBody!.Title);
        Assert.True(getBody.IsCompleted);
    }

    [Fact]
    public async Task UpdateTodo_TogglingIsCompleted_Persists()
    {
        var created = await CreateTodoAsync("Toggle me");
        Assert.False(created.IsCompleted);

        var response = await _client.PutAsync(
            $"/api/todos/{created.Id}",
            Json(new { title = created.Title, description = created.Description, isCompleted = true }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TodoResponseDto>();
        Assert.True(body!.IsCompleted);
    }

    [Fact]
    public async Task UpdateTodo_WithUnknownId_Returns404()
    {
        var response = await _client.PutAsync(
            $"/api/todos/{Guid.NewGuid()}",
            Json(new { title = "Doesn't matter", description = (string?)null, isCompleted = false }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateTodo_WithEmptyTitle_Returns400()
    {
        var created = await CreateTodoAsync();

        var response = await _client.PutAsync(
            $"/api/todos/{created.Id}",
            Json(new { title = "", description = (string?)null, isCompleted = false }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateTodo_WithMissingIsCompleted_Returns400()
    {
        var created = await CreateTodoAsync();

        var response = await _client.PutAsync(
            $"/api/todos/{created.Id}",
            Json(new { title = "Updated title", description = (string?)null }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateTodo_WithTitleOver200Chars_Returns400()
    {
        var created = await CreateTodoAsync();

        var response = await _client.PutAsync(
            $"/api/todos/{created.Id}",
            Json(new { title = new string('x', 201), description = (string?)null, isCompleted = false }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------
    // DELETE /api/todos/{id}
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeleteTodo_WithExistingId_Returns204AndRemovesIt()
    {
        var created = await CreateTodoAsync();

        var deleteResponse = await _client.DeleteAsync($"/api/todos/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await _client.GetAsync($"/api/todos/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteTodo_WithUnknownId_Returns404()
    {
        var response = await _client.DeleteAsync($"/api/todos/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteTodo_CalledTwice_SecondCallReturns404()
    {
        var created = await CreateTodoAsync();

        var first = await _client.DeleteAsync($"/api/todos/{created.Id}");
        var second = await _client.DeleteAsync($"/api/todos/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    // ---------------------------------------------------------------
    // CORS
    // ---------------------------------------------------------------

    [Fact]
    public async Task Cors_PreflightForAllowedOrigin_IsAccepted()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/todos");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await _client.SendAsync(request);

        Assert.True(
            response.Headers.Contains("Access-Control-Allow-Origin"),
            "Expected CORS preflight to include Access-Control-Allow-Origin for the configured origin.");
    }
}
