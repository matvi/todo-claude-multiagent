import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import * as api from './api';
import { ApiError } from './api';
import type { Todo } from './types';

// Mock only the request functions; keep the real ApiError class so that
// `err instanceof Error` checks in useTodos.ts still behave correctly.
vi.mock('./api', async () => {
  const actual = await vi.importActual<typeof import('./api')>('./api');
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
    title: 'Existing todo',
    description: null,
    isCompleted: false,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

describe('App (integration, mocked API)', () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  it('shows a loading state, then renders the fetched todos', async () => {
    mockedApi.listTodos.mockResolvedValue([makeTodo({ title: 'From server' })]);

    render(<App />);

    expect(screen.getByText(/loading/i)).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText('From server')).toBeInTheDocument());
  });

  it('shows the empty state when the API returns no todos', async () => {
    mockedApi.listTodos.mockResolvedValue([]);

    render(<App />);

    await waitFor(() => expect(screen.getByText(/no todos yet/i)).toBeInTheDocument());
  });

  it('shows an inline error banner when the initial load fails', async () => {
    mockedApi.listTodos.mockRejectedValue(new ApiError(500, 'Unable to reach the server. Is the API running?'));

    render(<App />);

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('Unable to reach the server'),
    );
  });

  it('full create flow: submitting the form calls the API and renders the new todo', async () => {
    const user = userEvent.setup();
    mockedApi.listTodos.mockResolvedValue([]);
    const created = makeTodo({ id: 'new-1', title: 'Buy bread' });
    mockedApi.createTodo.mockResolvedValue(created);

    render(<App />);
    await waitFor(() => expect(screen.getByText(/no todos yet/i)).toBeInTheDocument());

    await user.type(screen.getByLabelText('Title'), 'Buy bread');
    await user.click(screen.getByRole('button', { name: /add todo/i }));

    await waitFor(() => expect(screen.getByText('Buy bread')).toBeInTheDocument());
    expect(mockedApi.createTodo).toHaveBeenCalledWith({ title: 'Buy bread', description: null });
  });

  it('full toggle-complete flow: clicking the checkbox calls updateTodo and re-renders as completed', async () => {
    const user = userEvent.setup();
    const todo = makeTodo({ id: 'id-2', title: 'Toggle me', isCompleted: false });
    mockedApi.listTodos.mockResolvedValue([todo]);
    mockedApi.updateTodo.mockResolvedValue({ ...todo, isCompleted: true });

    render(<App />);
    await waitFor(() => expect(screen.getByText('Toggle me')).toBeInTheDocument());

    await user.click(screen.getByRole('checkbox'));

    await waitFor(() => expect(screen.getByRole('checkbox')).toBeChecked());
    expect(mockedApi.updateTodo).toHaveBeenCalledWith('id-2', {
      title: 'Toggle me',
      description: null,
      isCompleted: true,
    });
  });

  it('full edit flow: editing and saving calls updateTodo and shows the new title', async () => {
    const user = userEvent.setup();
    const todo = makeTodo({ id: 'id-3', title: 'Old title' });
    mockedApi.listTodos.mockResolvedValue([todo]);
    mockedApi.updateTodo.mockResolvedValue({ ...todo, title: 'New title' });

    render(<App />);
    await waitFor(() => expect(screen.getByText('Old title')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /edit/i }));
    const titleInput = screen.getByLabelText('Edit title');
    await user.clear(titleInput);
    await user.type(titleInput, 'New title');
    await user.click(screen.getByRole('button', { name: /save/i }));

    await waitFor(() => expect(screen.getByText('New title')).toBeInTheDocument());
  });

  it('full delete flow: clicking Delete calls deleteTodo and removes the item from the list', async () => {
    const user = userEvent.setup();
    const todo = makeTodo({ id: 'id-4', title: 'Remove me' });
    mockedApi.listTodos.mockResolvedValue([todo]);
    mockedApi.deleteTodo.mockResolvedValue(undefined);

    render(<App />);
    await waitFor(() => expect(screen.getByText('Remove me')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /delete/i }));

    await waitFor(() => expect(screen.getByText(/no todos yet/i)).toBeInTheDocument());
    expect(mockedApi.deleteTodo).toHaveBeenCalledWith('id-4');
  });

  it('shows an inline error banner (and keeps the item) when delete fails', async () => {
    const user = userEvent.setup();
    const todo = makeTodo({ id: 'id-5', title: 'Sticky item' });
    mockedApi.listTodos.mockResolvedValue([todo]);
    mockedApi.deleteTodo.mockRejectedValue(new ApiError(404, 'Not found'));

    render(<App />);
    await waitFor(() => expect(screen.getByText('Sticky item')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /delete/i }));

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Not found'));
    expect(screen.getByText('Sticky item')).toBeInTheDocument();
  });

  it('renders multiple todos each in their own list item', async () => {
    mockedApi.listTodos.mockResolvedValue([
      makeTodo({ id: 'a', title: 'Todo A' }),
      makeTodo({ id: 'b', title: 'Todo B' }),
    ]);

    render(<App />);

    await waitFor(() => expect(screen.getAllByRole('listitem')).toHaveLength(2));
    const items = screen.getAllByRole('listitem');
    expect(within(items[0]).getByText('Todo A')).toBeInTheDocument();
    expect(within(items[1]).getByText('Todo B')).toBeInTheDocument();
  });
});
