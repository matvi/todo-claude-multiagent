import type { Todo, UpdateTodoRequest } from '../types';
import { TodoItem } from './TodoItem';

interface TodoListProps {
  todos: Todo[];
  onToggleComplete: (todo: Todo) => Promise<void>;
  onUpdate: (id: string, input: UpdateTodoRequest) => Promise<void>;
  onRemove: (id: string) => Promise<void>;
}

export function TodoList({
  todos,
  onToggleComplete,
  onUpdate,
  onRemove,
}: TodoListProps) {
  if (todos.length === 0) {
    return <p className="todo-list__empty">No todos yet. Add one above.</p>;
  }

  return (
    <ul className="todo-list">
      {todos.map((todo) => (
        <TodoItem
          key={todo.id}
          todo={todo}
          onToggleComplete={onToggleComplete}
          onUpdate={onUpdate}
          onRemove={onRemove}
        />
      ))}
    </ul>
  );
}
