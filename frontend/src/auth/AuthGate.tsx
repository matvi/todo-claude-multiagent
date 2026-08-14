import { useEffect, useRef, useState, type ReactNode } from 'react';
import { InteractionStatus } from '@azure/msal-browser';
import { useIsAuthenticated, useMsal } from '@azure/msal-react';
import { loginRequest } from '../authConfig';
import { LoginScreen } from './LoginScreen';

/**
 * Renders its children only for a signed-in user (specs §16.9.2).
 *
 * On load it attempts **silent SSO** — a hidden-iframe round trip that succeeds only if
 * the identity provider still has a session cookie for this browser. Any failure
 * (typically `InteractionRequiredAuthError`) simply means "not signed in", so the login
 * screen is rendered; it is never an error state.
 *
 * The attempt is made exactly once per mount: MSAL's `inProgress` changes while
 * `ssoSilent` runs, and re-triggering on that would spin the IdP in a loop.
 */
export function AuthGate({ children }: { children: ReactNode }) {
  const { instance, inProgress } = useMsal();
  const isAuthenticated = useIsAuthenticated();
  const [checking, setChecking] = useState(!isAuthenticated);
  const attempted = useRef(false);

  useEffect(() => {
    if (isAuthenticated || attempted.current) {
      setChecking(false);
      return;
    }

    // Wait for any redirect handling MSAL is already doing.
    if (inProgress !== InteractionStatus.None) {
      return;
    }

    attempted.current = true;
    let cancelled = false;

    instance
      .ssoSilent(loginRequest)
      .then((result) => {
        if (!cancelled && result.account) {
          instance.setActiveAccount(result.account);
        }
      })
      .catch(() => {
        // Not signed in (or the IdP session is gone) — show the login screen.
      })
      .finally(() => {
        if (!cancelled) {
          setChecking(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [instance, inProgress, isAuthenticated]);

  if (isAuthenticated) {
    return <>{children}</>;
  }

  if (checking) {
    return (
      <main className="app">
        <p className="app__loading">Checking sign-in…</p>
      </main>
    );
  }

  return <LoginScreen />;
}
