// Mirrors the backend DTOs (see backend TodoResponse / CreateTodoRequest / UpdateTodoRequest).

export interface Todo {
  id: string;
  title: string;
  description: string | null;
  isCompleted: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface CreateTodoRequest {
  title: string;
  description?: string | null;
}

export interface UpdateTodoRequest {
  title: string;
  description: string | null;
  isCompleted: boolean;
}

// ---- Users (mirrors the backend UserResponse / UpdateUserProfileRequest, specs §16.8) ----

/**
 * The two application roles. The value shown here is only ever what the server told us;
 * the server re-derives it from PostgreSQL on every request, so it is display state,
 * never an authorization decision (specs §16.9.4).
 */
export type UserRole = 'User' | 'Admin';

export interface UserProfile {
  id: string;
  entraObjectId: string;
  email: string | null;
  displayName: string | null;
  phoneNumber: string | null;
  role: UserRole;
  createdAt: string;
}

export interface UpdateUserProfileRequest {
  displayName: string | null;
  phoneNumber: string | null;
}
