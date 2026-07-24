import { useState } from 'react';
import type { Todo, UpdateTodoRequest } from '../types';

interface TodoItemProps {
  todo: Todo;
  onToggleComplete: (todo: Todo) => Promise<void>;
  onUpdate: (id: string, input: UpdateTodoRequest) => Promise<void>;
  onRemove: (id: string) => Promise<void>;
}

function formatDate(iso: string): string {
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}

export function TodoItem({
  todo,
  onToggleComplete,
  onUpdate,
  onRemove,
}: TodoItemProps) {
  const [editing, setEditing] = useState(false);
  const [title, setTitle] = useState(todo.title);
  const [description, setDescription] = useState(todo.description ?? '');
  const [busy, setBusy] = useState(false);

  function startEdit() {
    setTitle(todo.title);
    setDescription(todo.description ?? '');
    setEditing(true);
  }

  function cancelEdit() {
    setEditing(false);
  }

  async function saveEdit() {
    const trimmedTitle = title.trim();
    if (trimmedTitle.length === 0) {
      return;
    }
    setBusy(true);
    try {
      await onUpdate(todo.id, {
        title: trimmedTitle,
        description: description.trim() === '' ? null : description.trim(),
        isCompleted: todo.isCompleted,
      });
      setEditing(false);
    } catch {
      // Error surfaced via hook state; stay in edit mode.
    } finally {
      setBusy(false);
    }
  }

  async function handleToggle() {
    setBusy(true);
    try {
      await onToggleComplete(todo);
    } catch {
      // handled upstream
    } finally {
      setBusy(false);
    }
  }

  async function handleRemove() {
    setBusy(true);
    try {
      await onRemove(todo.id);
    } catch {
      // handled upstream
    } finally {
      setBusy(false);
    }
  }

  if (editing) {
    return (
      <li className="todo-item todo-item--editing">
        <div className="todo-item__edit">
          <input
            className="todo-item__edit-title"
            type="text"
            value={title}
            maxLength={200}
            onChange={(e) => setTitle(e.target.value)}
            aria-label="Edit title"
          />
          <textarea
            className="todo-item__edit-description"
            value={description}
            maxLength={2000}
            onChange={(e) => setDescription(e.target.value)}
            aria-label="Edit description"
          />
          <div className="todo-item__actions">
            <button
              className="btn btn--primary"
              onClick={saveEdit}
              disabled={busy || title.trim().length === 0}
            >
              Save
            </button>
            <button className="btn" onClick={cancelEdit} disabled={busy}>
              Cancel
            </button>
          </div>
        </div>
      </li>
    );
  }

  return (
    <li className={`todo-item${todo.isCompleted ? ' todo-item--completed' : ''}`}>
      <input
        className="todo-item__checkbox"
        type="checkbox"
        checked={todo.isCompleted}
        onChange={handleToggle}
        disabled={busy}
        aria-label={`Mark "${todo.title}" as ${
          todo.isCompleted ? 'incomplete' : 'complete'
        }`}
      />
      <div className="todo-item__body">
        <span className="todo-item__title">{todo.title}</span>
        {todo.description && (
          <span className="todo-item__description">{todo.description}</span>
        )}
        <span className="todo-item__date">Created {formatDate(todo.createdAt)}</span>
      </div>
      <div className="todo-item__actions">
        <button className="btn" onClick={startEdit} disabled={busy}>
          Edit
        </button>
        <button className="btn btn--danger" onClick={handleRemove} disabled={busy}>
          Delete
        </button>
      </div>
    </li>
  );
}
