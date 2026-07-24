import { useCallback, useEffect, useState } from 'react';
import * as api from '../api';
import type { CreateTodoRequest, Todo, UpdateTodoRequest } from '../types';

interface UseTodosResult {
  todos: Todo[];
  loading: boolean;
  error: string | null;
  reload: () => Promise<void>;
  create: (input: CreateTodoRequest) => Promise<void>;
  update: (id: string, input: UpdateTodoRequest) => Promise<void>;
  toggleComplete: (todo: Todo) => Promise<void>;
  remove: (id: string) => Promise<void>;
}

function messageFrom(err: unknown): string {
  if (err instanceof Error) {
    return err.message;
  }
  return 'Something went wrong.';
}

/**
 * Owns the todo list state and orchestrates all CRUD calls. State is updated
 * on API success (no optimistic updates) to keep behaviour simple and correct.
 */
export function useTodos(): UseTodosResult {
  const [todos, setTodos] = useState<Todo[]>([]);
  const [loading, setLoading] = useState<boolean>(true);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await api.listTodos();
      setTodos(data);
    } catch (err) {
      setError(messageFrom(err));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void reload();
  }, [reload]);

  const create = useCallback(async (input: CreateTodoRequest) => {
    setError(null);
    try {
      const created = await api.createTodo(input);
      // Newest first.
      setTodos((prev) => [created, ...prev]);
    } catch (err) {
      setError(messageFrom(err));
      throw err;
    }
  }, []);

  const update = useCallback(async (id: string, input: UpdateTodoRequest) => {
    setError(null);
    try {
      const updated = await api.updateTodo(id, input);
      setTodos((prev) => prev.map((t) => (t.id === id ? updated : t)));
    } catch (err) {
      setError(messageFrom(err));
      throw err;
    }
  }, []);

  const toggleComplete = useCallback(
    async (todo: Todo) => {
      await update(todo.id, {
        title: todo.title,
        description: todo.description,
        isCompleted: !todo.isCompleted,
      });
    },
    [update],
  );

  const remove = useCallback(async (id: string) => {
    setError(null);
    try {
      await api.deleteTodo(id);
      setTodos((prev) => prev.filter((t) => t.id !== id));
    } catch (err) {
      setError(messageFrom(err));
      throw err;
    }
  }, []);

  return {
    todos,
    loading,
    error,
    reload,
    create,
    update,
    toggleComplete,
    remove,
  };
}
