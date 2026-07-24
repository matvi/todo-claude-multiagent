import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { TodoList } from './TodoList';
import type { Todo } from '../types';

function makeTodo(overrides: Partial<Todo> = {}): Todo {
  return {
    id: 'id-1',
    title: 'Sample todo',
    description: null,
    isCompleted: false,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

describe('TodoList', () => {
  const noop = vi.fn().mockResolvedValue(undefined);

  it('shows an empty-state message when there are no todos', () => {
    render(<TodoList todos={[]} onToggleComplete={noop} onUpdate={noop} onRemove={noop} />);

    expect(screen.getByText(/no todos yet/i)).toBeInTheDocument();
  });

  it('renders one list item per todo, showing title and description', () => {
    const todos = [
      makeTodo({ id: '1', title: 'First', description: 'First description' }),
      makeTodo({ id: '2', title: 'Second', description: null }),
    ];

    render(<TodoList todos={todos} onToggleComplete={noop} onUpdate={noop} onRemove={noop} />);

    expect(screen.getAllByRole('listitem')).toHaveLength(2);
    expect(screen.getByText('First')).toBeInTheDocument();
    expect(screen.getByText('First description')).toBeInTheDocument();
    expect(screen.getByText('Second')).toBeInTheDocument();
  });

  it('marks completed todos distinctly (checkbox reflects isCompleted)', () => {
    const todos = [makeTodo({ id: '1', title: 'Done item', isCompleted: true })];

    render(<TodoList todos={todos} onToggleComplete={noop} onUpdate={noop} onRemove={noop} />);

    const checkbox = screen.getByRole('checkbox') as HTMLInputElement;
    expect(checkbox.checked).toBe(true);
  });
});
