import { useState } from 'react';
import { useMsal } from '@azure/msal-react';
import { googleLoginRequest, loginRequest } from '../authConfig';

/**
 * The unauthenticated landing screen (specs §16.9.2).
 *
 * Both actions are redirect flows: popups are blocked by default in many browsers and
 * would complicate the nginx/container setup for no benefit here. Sign-up and sign-in are
 * the same action — the Microsoft-hosted user flow shows the email + password form, the
 * Google button and the "No account? Sign up" link.
 */
export function LoginScreen() {
  const { instance } = useMsal();
  const [error, setError] = useState<string | null>(null);

  const signIn = async (google: boolean) => {
    setError(null);
    try {
      await instance.loginRedirect(google ? googleLoginRequest : loginRequest);
    } catch {
      setError('Could not start sign-in. Please try again.');
    }
  };

  return (
    <main className="app login">
      <h1 className="app__title">Todos</h1>
      <p className="login__lead">Sign in to view and manage your todos.</p>

      {error && (
        <p className="app__error" role="alert">
          {error}
        </p>
      )}

      <div className="login__actions">
        <button
          type="button"
          className="btn btn--primary"
          onClick={() => void signIn(false)}
        >
          Sign in / Sign up
        </button>

        <button
          type="button"
          className="btn"
          onClick={() => void signIn(true)}
        >
          Continue with Google
        </button>
      </div>
    </main>
  );
}
