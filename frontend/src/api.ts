import type {
  CreateTodoRequest,
  Todo,
  UpdateTodoRequest,
  UpdateUserProfileRequest,
  UserProfile,
  UserRole,
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

/** Resolves the current access token, or null when the user is not signed in. */
export type AccessTokenProvider = () => Promise<string | null>;

// Module-level, deliberately: this file stays framework-agnostic (no React, no MSAL
// import), so the auth layer injects a token provider instead of the API client
// reaching into it. With nothing configured the client behaves exactly as it did
// before authentication existed — which is also what keeps the existing unit tests
// valid (specs §16.9.3).
let tokenProvider: AccessTokenProvider | null = null;
let unauthorizedHandler: (() => void) | null = null;

/**
 * Registers the access-token provider used for every subsequent request, and an
 * optional callback invoked when the API rejects the token with 401 (the app then
 * drops back to the login screen instead of retry-looping).
 *
 * Pass `null` to detach (e.g. on sign-out).
 */
export function configureApiAuth(
  getToken: AccessTokenProvider | null,
  onUnauthorized?: () => void,
): void {
  tokenProvider = getToken;
  unauthorizedHandler = onUnauthorized ?? null;
}

async function authHeaders(): Promise<Record<string, string>> {
  if (!tokenProvider) {
    return {};
  }

  const token = await tokenProvider();
  return token ? { Authorization: `Bearer ${token}` } : {};
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const headers = {
    'Content-Type': 'application/json',
    ...(await authHeaders()),
    ...(init?.headers as Record<string, string> | undefined),
  };

  let response: Response;
  try {
    response = await fetch(`${BASE_URL}${path}`, {
      ...init,
      headers,
    });
  } catch {
    // Network-level failure (server down, CORS, DNS, etc.).
    throw new ApiError(0, 'Unable to reach the server. Is the API running?');
  }

  if (!response.ok) {
    if (response.status === 401) {
      // The token was rejected (expired, wrong audience, or the caller is no longer a
      // valid user of this app). Re-authenticate rather than retry.
      unauthorizedHandler?.();
    }

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

// ---- Users (specs §16.8) ----

/** The caller's own profile and role, as re-derived server-side from PostgreSQL. */
export function getMe(): Promise<UserProfile> {
  return request<UserProfile>('/api/users/me', { method: 'GET' });
}

/** Self-service profile update. Only displayName and phoneNumber are mutable. */
export function updateMe(body: UpdateUserProfileRequest): Promise<UserProfile> {
  return request<UserProfile>('/api/users/me', {
    method: 'PUT',
    body: JSON.stringify(body),
  });
}

/** Admin-only. A non-admin caller gets 403 from the server regardless of the UI. */
export function listUsers(): Promise<UserProfile[]> {
  return request<UserProfile[]>('/api/users', { method: 'GET' });
}

/** Admin-only promote/demote. */
export function updateUserRole(id: string, role: UserRole): Promise<UserProfile> {
  return request<UserProfile>(`/api/users/${id}/role`, {
    method: 'PATCH',
    body: JSON.stringify({ role }),
  });
}
