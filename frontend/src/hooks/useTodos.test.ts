import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useTodos } from './useTodos';
import * as api from '../api';
import { ApiError } from '../api';
import type { Todo } from '../types';

// Mock only the request functions; keep the real ApiError class so that
// `err instanceof Error` checks in useTodos.ts still behave correctly.
vi.mock('../api', async () => {
  const actual = await vi.importActual<typeof import('../api')>('../api');
  return {
    ...actual,
    listTodos: vi.fn(),
    createTodo: vi.fn(),
    updateTodo: vi.fn(),
    deleteTodo: vi.fn(),
  };
});

const mockedApi = vi.mocked(api);

function makeTodo(overrides: Partial<Todo> = {}): Todo {
  return {
    id: 'id-1',
    title: 'Sample',
    description: null,
    isCompleted: false,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

describe('useTodos', () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  it('loads todos on mount and exposes them with loading=false afterwards', async () => {
    const todos = [makeTodo({ id: '1' }), makeTodo({ id: '2' })];
    mockedApi.listTodos.mockResolvedValue(todos);

    const { result } = renderHook(() => useTodos());

    expect(result.current.loading).toBe(true);

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.todos).toEqual(todos);
    expect(result.current.error).toBeNull();
  });

  it('surfaces an error message when the initial load fails', async () => {
    mockedApi.listTodos.mockRejectedValue(new ApiError(500, 'Server exploded'));

    const { result } = renderHook(() => useTodos());

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.error).toBe('Server exploded');
    expect(result.current.todos).toEqual([]);
  });

  it('create() prepends the newly created todo (newest first)', async () => {
    mockedApi.listTodos.mockResolvedValue([]);
    const created = makeTodo({ id: 'new-id', title: 'Fresh todo' });
    mockedApi.createTodo.mockResolvedValue(created);

    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => {
      await result.current.create({ title: 'Fresh todo' });
    });

    expect(result.current.todos).toEqual([created]);
    expect(mockedApi.createTodo).toHaveBeenCalledWith({ title: 'Fresh todo' });
  });

  it('create() sets an error and rethrows on failure, leaving the list unchanged', async () => {
    mockedApi.listTodos.mockResolvedValue([]);
    mockedApi.createTodo.mockRejectedValue(new ApiError(400, 'Title is required'));

    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => {
      await expect(result.current.create({ title: '' })).rejects.toThrow();
    });

    expect(result.current.error).toBe('Title is required');
    expect(result.current.todos).toEqual([]);
  });

  it('update() replaces the matching todo in place', async () => {
    const original = makeTodo({ id: '1', title: 'Old' });
    mockedApi.listTodos.mockResolvedValue([original]);
    const updated = makeTodo({ id: '1', title: 'New' });
    mockedApi.updateTodo.mockResolvedValue(updated);

    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => {
      await result.current.update('1', { title: 'New', description: null, isCompleted: false });
    });

    expect(result.current.todos).toEqual([updated]);
  });

  it('toggleComplete() calls update with the flipped isCompleted flag', async () => {
    const original = makeTodo({ id: '1', title: 'T', description: 'D', isCompleted: false });
    mockedApi.listTodos.mockResolvedValue([original]);
    mockedApi.updateTodo.mockResolvedValue({ ...original, isCompleted: true });

    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => {
      await result.current.toggleComplete(original);
    });

    expect(mockedApi.updateTodo).toHaveBeenCalledWith('1', {
      title: 'T',
      description: 'D',
      isCompleted: true,
    });
    expect(result.current.todos[0].isCompleted).toBe(true);
  });

  it('remove() deletes the todo and removes it from local state', async () => {
    const todo = makeTodo({ id: '1' });
    mockedApi.listTodos.mockResolvedValue([todo]);
    mockedApi.deleteTodo.mockResolvedValue(undefined);

    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => {
      await result.current.remove('1');
    });

    expect(mockedApi.deleteTodo).toHaveBeenCalledWith('1');
    expect(result.current.todos).toEqual([]);
  });

  it('remove() on failure keeps the todo in state and sets an error', async () => {
    const todo = makeTodo({ id: '1' });
    mockedApi.listTodos.mockResolvedValue([todo]);
    mockedApi.deleteTodo.mockRejectedValue(new ApiError(404, 'Not found'));

    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => {
      await expect(result.current.remove('1')).rejects.toThrow();
    });

    expect(result.current.todos).toEqual([todo]);
    expect(result.current.error).toBe('Not found');
  });
});
