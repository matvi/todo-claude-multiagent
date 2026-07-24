import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { TodoForm } from './TodoForm';

describe('TodoForm', () => {
  it('submits trimmed title and description, then clears the form', async () => {
    const user = userEvent.setup();
    const onCreate = vi.fn().mockResolvedValue(undefined);
    render(<TodoForm onCreate={onCreate} />);

    await user.type(screen.getByLabelText('Title'), '  Buy milk  ');
    await user.type(screen.getByLabelText('Description'), '  2% please  ');
    await user.click(screen.getByRole('button', { name: /add todo/i }));

    expect(onCreate).toHaveBeenCalledWith({ title: 'Buy milk', description: '2% please' });
    expect(screen.getByLabelText('Title')).toHaveValue('');
    expect(screen.getByLabelText('Description')).toHaveValue('');
  });

  it('sends null description when left blank', async () => {
    const user = userEvent.setup();
    const onCreate = vi.fn().mockResolvedValue(undefined);
    render(<TodoForm onCreate={onCreate} />);

    await user.type(screen.getByLabelText('Title'), 'No description');
    await user.click(screen.getByRole('button', { name: /add todo/i }));

    expect(onCreate).toHaveBeenCalledWith({ title: 'No description', description: null });
  });

  it('disables the submit button when the title is empty or whitespace-only', async () => {
    const user = userEvent.setup();
    const onCreate = vi.fn();
    render(<TodoForm onCreate={onCreate} />);

    const button = screen.getByRole('button', { name: /add todo/i });
    expect(button).toBeDisabled();

    await user.type(screen.getByLabelText('Title'), '   ');
    expect(button).toBeDisabled();
    expect(onCreate).not.toHaveBeenCalled();
  });

  it('does not call onCreate when submitting an empty form', async () => {
    const onCreate = vi.fn();
    const { container } = render(<TodoForm onCreate={onCreate} />);

    const form = container.querySelector('form')!;
    form.requestSubmit ? form.requestSubmit() : form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));

    expect(onCreate).not.toHaveBeenCalled();
  });
});
