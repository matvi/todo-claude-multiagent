import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, createTodo, deleteTodo, listTodos, updateTodo } from './api';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

describe('api client', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('listTodos performs a GET and returns the parsed JSON array', async () => {
    const todos = [
      {
        id: '1',
        title: 'A',
        description: null,
        isCompleted: false,
        createdAt: '2026-01-01T00:00:00Z',
        updatedAt: '2026-01-01T00:00:00Z',
      },
    ];
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(todos));
    vi.stubGlobal('fetch', fetchMock);

    const result = await listTodos();

    expect(result).toEqual(todos);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/api/todos');
    expect(init?.method).toBe('GET');
  });

  it('createTodo performs a POST with a JSON body and returns the created todo', async () => {
    const created = {
      id: '2',
      title: 'New',
      description: 'desc',
      isCompleted: false,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
    };
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(created, 201));
    vi.stubGlobal('fetch', fetchMock);

    const result = await createTodo({ title: 'New', description: 'desc' });

    expect(result).toEqual(created);
    const [, init] = fetchMock.mock.calls[0];
    expect(init?.method).toBe('POST');
    expect(JSON.parse(init!.body as string)).toEqual({ title: 'New', description: 'desc' });
  });

  it('updateTodo performs a PUT to /api/todos/{id}', async () => {
    const updated = {
      id: '3',
      title: 'Updated',
      description: null,
      isCompleted: true,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-02T00:00:00Z',
    };
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(updated));
    vi.stubGlobal('fetch', fetchMock);

    const result = await updateTodo('3', {
      title: 'Updated',
      description: null,
      isCompleted: true,
    });

    expect(result).toEqual(updated);
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/api/todos/3');
    expect(init?.method).toBe('PUT');
  });

  it('deleteTodo performs a DELETE and resolves without a body on 204', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(deleteTodo('4')).resolves.toBeUndefined();
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/api/todos/4');
    expect(init?.method).toBe('DELETE');
  });

  it('throws ApiError with the server status on a non-2xx response', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      jsonResponse({ title: 'Not Found' }, 404),
    );
    vi.stubGlobal('fetch', fetchMock);

    const error = await listTodos().catch((e) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(404);
  });

  it('surfaces ASP.NET ValidationProblemDetails "errors" messages', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      jsonResponse(
        { errors: { Title: ['The Title field is required.'] } },
        400,
      ),
    );
    vi.stubGlobal('fetch', fetchMock);

    const error = await createTodo({ title: '' }).catch((e) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).message).toContain('required');
  });

  it('throws a network-failure ApiError (status 0) when fetch rejects', async () => {
    const fetchMock = vi.fn().mockRejectedValue(new TypeError('Failed to fetch'));
    vi.stubGlobal('fetch', fetchMock);

    const error = await listTodos().catch((e) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(0);
  });
});
