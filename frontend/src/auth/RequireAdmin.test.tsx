import { render, screen } from '@testing-library/react';
import { useEffect } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { RequireAdmin } from './RequireAdmin';
import { useCurrentUser, type CurrentUserState } from './UserContext';

vi.mock('./UserContext', () => ({
  useCurrentUser: vi.fn(),
}));

const mockedUseCurrentUser = vi.mocked(useCurrentUser);

function state(overrides: Partial<CurrentUserState>): CurrentUserState {
  return {
    user: null,
    role: null,
    loading: false,
    error: null,
    reload: vi.fn(async () => {}),
    ...overrides,
  };
}

/** A stand-in for AdminPage: proves whether RequireAdmin actually MOUNTS its children
 *  (as opposed to just visually hiding them), which is what "issues no admin API call"
 *  depends on in the real app. */
function ChildThatSignalsMount({ onMount }: { onMount: () => void }) {
  useEffect(() => {
    onMount();
  }, [onMount]);
  return <div>Admin child content</div>;
}

/** specs §16.9.4, §16.18 item 29. */
describe('RequireAdmin', () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  it('role=User renders the not-authorized panel and never mounts children (no admin API call)', () => {
    mockedUseCurrentUser.mockReturnValue(state({ role: 'User' }));
    const onMount = vi.fn();

    render(
      <RequireAdmin>
        <ChildThatSignalsMount onMount={onMount} />
      </RequireAdmin>,
    );

    expect(screen.getByText(/not authorized/i)).toBeInTheDocument();
    expect(screen.queryByText('Admin child content')).not.toBeInTheDocument();
    expect(onMount).not.toHaveBeenCalled();
  });

  it('role=Admin renders children', () => {
    mockedUseCurrentUser.mockReturnValue(state({ role: 'Admin' }));
    const onMount = vi.fn();

    render(
      <RequireAdmin>
        <ChildThatSignalsMount onMount={onMount} />
      </RequireAdmin>,
    );

    expect(screen.getByText('Admin child content')).toBeInTheDocument();
    expect(screen.queryByText(/not authorized/i)).not.toBeInTheDocument();
    expect(onMount).toHaveBeenCalledTimes(1);
  });

  it('loading state renders neither children nor the not-authorized panel', () => {
    mockedUseCurrentUser.mockReturnValue(state({ role: null, loading: true }));

    render(
      <RequireAdmin>
        <div>Admin child content</div>
      </RequireAdmin>,
    );

    expect(screen.queryByText('Admin child content')).not.toBeInTheDocument();
    expect(screen.queryByText(/not authorized/i)).not.toBeInTheDocument();
  });

  it('error state surfaces the error and does not render children', () => {
    mockedUseCurrentUser.mockReturnValue(state({ role: null, error: 'Could not load your profile.' }));

    render(
      <RequireAdmin>
        <div>Admin child content</div>
      </RequireAdmin>,
    );

    expect(screen.getByRole('alert')).toHaveTextContent('Could not load your profile.');
    expect(screen.queryByText('Admin child content')).not.toBeInTheDocument();
  });
});
