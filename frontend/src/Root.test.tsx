import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import Root from './Root';
import { markSessionRejected, resetSessionRejected } from './auth/authSession';

// Root's own module-scope useMsal call (inside Layout) is never reached in this test —
// sessionRejected short-circuits before AuthGate/UserProvider/Layout render — but the
// module must still resolve at import time.
vi.mock('@azure/msal-react', () => ({
  useMsal: vi.fn(() => ({ instance: {}, accounts: [], inProgress: 'none' })),
  useIsAuthenticated: vi.fn(() => true),
}));

/**
 * specs §16.18 item 28 at the app-shell level: "A 401 from the API returns the app to the
 * unauthenticated state (no retry loop)." api.ts's 401 handler calls markSessionRejected();
 * Root subscribes to that and renders the login screen instead of the authenticated tree.
 */
describe('Root — session-rejected fallback', () => {
  beforeEach(() => {
    resetSessionRejected();
  });

  it('renders the login screen immediately when the session was already marked rejected', () => {
    markSessionRejected();

    render(<Root />);

    expect(screen.getByText(/sign in to view/i)).toBeInTheDocument();
  });
});
