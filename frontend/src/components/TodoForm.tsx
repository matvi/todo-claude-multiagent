import { useState, type FormEvent } from 'react';
import type { CreateTodoRequest } from '../types';

interface TodoFormProps {
  onCreate: (input: CreateTodoRequest) => Promise<void>;
}

export function TodoForm({ onCreate }: TodoFormProps) {
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [submitting, setSubmitting] = useState(false);

  const trimmedTitle = title.trim();
  const canSubmit = trimmedTitle.length > 0 && !submitting;

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    if (!canSubmit) {
      return;
    }
    setSubmitting(true);
    try {
      await onCreate({
        title: trimmedTitle,
        description: description.trim() === '' ? null : description.trim(),
      });
      setTitle('');
      setDescription('');
    } catch {
      // Error surfaced via the hook's error state; keep the form values.
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <form className="todo-form" onSubmit={handleSubmit}>
      <input
        className="todo-form__title"
        type="text"
        placeholder="What needs to be done?"
        value={title}
        maxLength={200}
        onChange={(e) => setTitle(e.target.value)}
        aria-label="Title"
      />
      <textarea
        className="todo-form__description"
        placeholder="Description (optional)"
        value={description}
        maxLength={2000}
        onChange={(e) => setDescription(e.target.value)}
        aria-label="Description"
      />
      <button className="btn btn--primary" type="submit" disabled={!canSubmit}>
        {submitting ? 'Adding…' : 'Add todo'}
      </button>
    </form>
  );
}
