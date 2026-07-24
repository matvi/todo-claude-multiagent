import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { TodoItem } from './TodoItem';
import type { Todo } from '../types';

function makeTodo(overrides: Partial<Todo> = {}): Todo {
  return {
    id: 'id-1',
    title: 'Buy milk',
    description: '2% please',
    isCompleted: false,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

describe('TodoItem', () => {
  it('renders title, description and created date', () => {
    render(
      <ul>
        <TodoItem
          todo={makeTodo()}
          onToggleComplete={vi.fn()}
          onUpdate={vi.fn()}
          onRemove={vi.fn()}
        />
      </ul>,
    );

    expect(screen.getByText('Buy milk')).toBeInTheDocument();
    expect(screen.getByText('2% please')).toBeInTheDocument();
    expect(screen.getByText(/created/i)).toBeInTheDocument();
  });

  it('checking the checkbox calls onToggleComplete with the todo', async () => {
    const user = userEvent.setup();
    const onToggleComplete = vi.fn().mockResolvedValue(undefined);
    const todo = makeTodo();

    render(
      <ul>
        <TodoItem todo={todo} onToggleComplete={onToggleComplete} onUpdate={vi.fn()} onRemove={vi.fn()} />
      </ul>,
    );

    await user.click(screen.getByRole('checkbox'));

    expect(onToggleComplete).toHaveBeenCalledWith(todo);
  });

  it('clicking Delete calls onRemove with the todo id', async () => {
    const user = userEvent.setup();
    const onRemove = vi.fn().mockResolvedValue(undefined);
    const todo = makeTodo({ id: 'abc-123' });

    render(
      <ul>
        <TodoItem todo={todo} onToggleComplete={vi.fn()} onUpdate={vi.fn()} onRemove={onRemove} />
      </ul>,
    );

    await user.click(screen.getByRole('button', { name: /delete/i }));

    expect(onRemove).toHaveBeenCalledWith('abc-123');
  });

  it('Edit -> change title -> Save calls onUpdate with the new trimmed title and existing isCompleted', async () => {
    const user = userEvent.setup();
    const onUpdate = vi.fn().mockResolvedValue(undefined);
    const todo = makeTodo({ id: 'abc-123', title: 'Old title', isCompleted: true });

    render(
      <ul>
        <TodoItem todo={todo} onToggleComplete={vi.fn()} onUpdate={onUpdate} onRemove={vi.fn()} />
      </ul>,
    );

    await user.click(screen.getByRole('button', { name: /edit/i }));

    const titleInput = screen.getByLabelText('Edit title');
    await user.clear(titleInput);
    await user.type(titleInput, '  New title  ');
    await user.click(screen.getByRole('button', { name: /save/i }));

    expect(onUpdate).toHaveBeenCalledWith('abc-123', {
      title: 'New title',
      description: '2% please',
      isCompleted: true,
    });
  });

  it('Edit -> Cancel discards changes without calling onUpdate', async () => {
    const user = userEvent.setup();
    const onUpdate = vi.fn();
    const todo = makeTodo({ title: 'Original' });

    render(
      <ul>
        <TodoItem todo={todo} onToggleComplete={vi.fn()} onUpdate={onUpdate} onRemove={vi.fn()} />
      </ul>,
    );

    await user.click(screen.getByRole('button', { name: /edit/i }));
    const titleInput = screen.getByLabelText('Edit title');
    await user.clear(titleInput);
    await user.type(titleInput, 'Changed but cancelled');
    await user.click(screen.getByRole('button', { name: /cancel/i }));

    expect(onUpdate).not.toHaveBeenCalled();
    expect(screen.getByText('Original')).toBeInTheDocument();
  });

  it('Save button is disabled when the edited title is emptied out', async () => {
    const user = userEvent.setup();
    const onUpdate = vi.fn();
    render(
      <ul>
        <TodoItem todo={makeTodo()} onToggleComplete={vi.fn()} onUpdate={onUpdate} onRemove={vi.fn()} />
      </ul>,
    );

    await user.click(screen.getByRole('button', { name: /edit/i }));
    const titleInput = screen.getByLabelText('Edit title');
    await user.clear(titleInput);

    expect(screen.getByRole('button', { name: /save/i })).toBeDisabled();
  });
});
