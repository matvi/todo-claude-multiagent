import type {
  CreateTodoRequest,
  Todo,
  UpdateTodoRequest,
} from './types';

const BASE_URL = (import.meta.env.VITE_API_BASE_URL ?? '').replace(/\/$/, '');

/** Error thrown for any non-2xx API response. */
export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${BASE_URL}${path}`, {
      headers: { 'Content-Type': 'application/json' },
      ...init,
    });
  } catch {
    // Network-level failure (server down, CORS, DNS, etc.).
    throw new ApiError(0, 'Unable to reach the server. Is the API running?');
  }

  if (!response.ok) {
    throw new ApiError(
      response.status,
      await extractErrorMessage(response),
    );
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

async function extractErrorMessage(response: Response): Promise<string> {
  try {
    const body = await response.json();
    // ASP.NET Core ProblemDetails / ValidationProblemDetails shape.
    if (body?.errors && typeof body.errors === 'object') {
      const messages = Object.values(body.errors as Record<string, string[]>)
        .flat()
        .filter(Boolean);
      if (messages.length > 0) {
        return messages.join(' ');
      }
    }
    if (typeof body?.title === 'string') {
      return body.title;
    }
  } catch {
    // Response had no JSON body; fall through to generic message.
  }
  return `Request failed with status ${response.status}`;
}

export function listTodos(): Promise<Todo[]> {
  return request<Todo[]>('/api/todos', { method: 'GET' });
}

export function createTodo(body: CreateTodoRequest): Promise<Todo> {
  return request<Todo>('/api/todos', {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

export function updateTodo(
  id: string,
  body: UpdateTodoRequest,
): Promise<Todo> {
  return request<Todo>(`/api/todos/${id}`, {
    method: 'PUT',
    body: JSON.stringify(body),
  });
}

export function deleteTodo(id: string): Promise<void> {
  return request<void>(`/api/todos/${id}`, { method: 'DELETE' });
}
