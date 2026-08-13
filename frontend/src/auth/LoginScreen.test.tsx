import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useMsal } from '@azure/msal-react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { LoginScreen } from './LoginScreen';

vi.mock('@azure/msal-react', () => ({
  useMsal: vi.fn(),
}));

const mockedUseMsal = vi.mocked(useMsal);

/** specs §16.18 items 24-25. */
describe('LoginScreen', () => {
  let loginRedirect: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    loginRedirect = vi.fn().mockResolvedValue(undefined);
    mockedUseMsal.mockReturnValue({
      instance: { loginRedirect } as never,
      accounts: [],
      inProgress: 'none',
    } as never);
  });

  it('item 24: renders both a sign-in/sign-up action and a Continue with Google action', () => {
    render(<LoginScreen />);

    expect(screen.getByRole('button', { name: /sign in \/ sign up/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /continue with google/i })).toBeInTheDocument();
  });

  it('"Sign in / Sign up" calls loginRedirect WITHOUT a domain_hint', async () => {
    const user = userEvent.setup();
    render(<LoginScreen />);

    await user.click(screen.getByRole('button', { name: /sign in \/ sign up/i }));

    expect(loginRedirect).toHaveBeenCalledTimes(1);
    const request = loginRedirect.mock.calls[0][0] as { extraQueryParameters?: unknown };
    expect(request.extraQueryParameters).toBeUndefined();
  });

  it('item 25: "Continue with Google" calls loginRedirect with extraQueryParameters.domain_hint === "google.com"', async () => {
    const user = userEvent.setup();
    render(<LoginScreen />);

    await user.click(screen.getByRole('button', { name: /continue with google/i }));

    expect(loginRedirect).toHaveBeenCalledTimes(1);
    const request = loginRedirect.mock.calls[0][0] as {
      extraQueryParameters?: Record<string, string>;
    };
    expect(request.extraQueryParameters).toEqual({ domain_hint: 'google.com' });
  });

  it('shows an inline error if loginRedirect itself throws synchronously/rejects', async () => {
    loginRedirect.mockRejectedValueOnce(new Error('popup blocked or similar'));
    const user = userEvent.setup();
    render(<LoginScreen />);

    await user.click(screen.getByRole('button', { name: /sign in \/ sign up/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/could not start sign-in/i);
  });
});
