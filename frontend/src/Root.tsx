import { useEffect, useState } from 'react';
import { useMsal } from '@azure/msal-react';
import { BrowserRouter, Link, Navigate, Route, Routes } from 'react-router-dom';
import App from './App';
import { configureApiAuth } from './api';
import { AuthGate } from './auth/AuthGate';
import { LoginScreen } from './auth/LoginScreen';
import { RequireAdmin } from './auth/RequireAdmin';
import { UserProvider, useCurrentUser } from './auth/UserContext';
import { isSessionRejected, onSessionRejected } from './auth/authSession';
import { AdminPage } from './pages/AdminPage';

/**
 * The application shell: authentication gate, routing and navigation (specs §16.9).
 * `App` remains the todo screen and knows nothing about authentication.
 *
 * The API token provider is registered in `main.tsx` before this tree renders, so the
 * first request a child makes already carries a bearer token.
 */
export default function Root() {
  // Set when the API rejected our token with 401: the session was cleared, so the user is
  // returned to the login screen rather than the app retrying in a loop (specs §16.9.3).
  const [sessionRejected, setSessionRejected] = useState(isSessionRejected);

  useEffect(() => onSessionRejected(() => setSessionRejected(true)), []);

  if (sessionRejected) {
    return <LoginScreen />;
  }

  return (
    <AuthGate>
      <UserProvider>
        <BrowserRouter>
          <Layout />
        </BrowserRouter>
      </UserProvider>
    </AuthGate>
  );
}

function Layout() {
  const { instance } = useMsal();
  const { user, role } = useCurrentUser();

  const signOut = () => {
    configureApiAuth(null);
    void instance.logoutRedirect();
  };

  return (
    <div className="shell">
      <nav className="shell__nav">
        <Link to="/">Todos</Link>

        {/* Hiding this link is UX, not security: the admin endpoints authorize
            server-side on every call (specs §16.9.4). */}
        {role === 'Admin' && <Link to="/admin">Admin</Link>}

        <span className="shell__spacer" />
        <span className="shell__user">{user?.displayName ?? user?.email ?? ''}</span>
        <button type="button" className="btn" onClick={signOut}>
          Sign out
        </button>
      </nav>

      <Routes>
        <Route path="/" element={<App />} />
        <Route
          path="/admin"
          element={
            <main className="app">
              <h1 className="app__title">Administration</h1>
              <RequireAdmin>
                <AdminPage />
              </RequireAdmin>
            </main>
          }
        />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </div>
  );
}
