import { render, screen, waitFor } from '@testing-library/react';
import { InteractionRequiredAuthError, InteractionStatus } from '@azure/msal-browser';
import { useIsAuthenticated, useMsal } from '@azure/msal-react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthGate } from './AuthGate';

vi.mock('@azure/msal-react', () => ({
  useMsal: vi.fn(),
  useIsAuthenticated: vi.fn(),
}));

const mockedUseMsal = vi.mocked(useMsal);
const mockedUseIsAuthenticated = vi.mocked(useIsAuthenticated);

/** specs §16.9.2, §16.18 item 26. */
describe('AuthGate', () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  it('already authenticated: renders children immediately and never calls ssoSilent', () => {
    mockedUseIsAuthenticated.mockReturnValue(true);
    const ssoSilent = vi.fn();
    mockedUseMsal.mockReturnValue({
      instance: { ssoSilent, setActiveAccount: vi.fn() } as never,
      accounts: [],
      inProgress: InteractionStatus.None,
    } as never);

    render(
      <AuthGate>
        <div>Protected content</div>
      </AuthGate>,
    );

    expect(screen.getByText('Protected content')).toBeInTheDocument();
    expect(ssoSilent).not.toHaveBeenCalled();
  });

  it('item 26: ssoSilent success skips the login screen and renders children', async () => {
    // useIsAuthenticated is a live MSAL context value in the real app; simulate it flipping
    // to true once the silent sign-in resolves, mirroring MSAL's own account-added event.
    let authenticated = false;
    mockedUseIsAuthenticated.mockImplementation(() => authenticated);

    const setActiveAccount = vi.fn();
    const account = { homeAccountId: 'fake-account' } as never;
    mockedUseMsal.mockReturnValue({
      instance: {
        ssoSilent: vi.fn().mockImplementation(async () => {
          authenticated = true;
          return { account };
        }),
        setActiveAccount,
      } as never,
      accounts: [],
      inProgress: InteractionStatus.None,
    } as never);

    render(
      <AuthGate>
        <div>Protected content</div>
      </AuthGate>,
    );

    expect(screen.getByText(/checking sign-in/i)).toBeInTheDocument();

    await waitFor(() => expect(screen.getByText('Protected content')).toBeInTheDocument());
    expect(setActiveAccount).toHaveBeenCalledWith(account);
  });

  it('item 26: InteractionRequiredAuthError falls back to the login screen without crashing', async () => {
    mockedUseIsAuthenticated.mockReturnValue(false);
    mockedUseMsal.mockReturnValue({
      instance: {
        ssoSilent: vi
          .fn()
          .mockRejectedValue(new InteractionRequiredAuthError('interaction_required')),
        setActiveAccount: vi.fn(),
      } as never,
      accounts: [],
      inProgress: InteractionStatus.None,
    } as never);

    render(
      <AuthGate>
        <div>Protected content</div>
      </AuthGate>,
    );

    expect(screen.getByText(/checking sign-in/i)).toBeInTheDocument();

    await waitFor(() => expect(screen.getByText(/sign in to view/i)).toBeInTheDocument());
    expect(screen.queryByText('Protected content')).not.toBeInTheDocument();
  });

  it('any other ssoSilent failure also degrades to the login screen, not a crash', async () => {
    mockedUseIsAuthenticated.mockReturnValue(false);
    mockedUseMsal.mockReturnValue({
      instance: {
        ssoSilent: vi.fn().mockRejectedValue(new Error('network error')),
        setActiveAccount: vi.fn(),
      } as never,
      accounts: [],
      inProgress: InteractionStatus.None,
    } as never);

    render(
      <AuthGate>
        <div>Protected content</div>
      </AuthGate>,
    );

    await waitFor(() => expect(screen.getByText(/sign in to view/i)).toBeInTheDocument());
  });
});
