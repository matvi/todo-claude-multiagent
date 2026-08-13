import type { ReactNode } from 'react';
import { useCurrentUser } from './UserContext';

/**
 * Renders admin-only UI, or a "not authorized" panel.
 *
 * **This is UX, not security.** A user who types `/admin` in the address bar gets this
 * panel, and even if they bypassed it in devtools every admin API call would still return
 * 403 — the server re-derives the role from PostgreSQL on every request and never trusts
 * anything the client asserts (specs §16.9.4). The panel exists so a `User` never issues
 * an admin API call in the first place.
 */
export function RequireAdmin({ children }: { children: ReactNode }) {
  const { role, loading, error } = useCurrentUser();

  if (loading) {
    return <p className="app__loading">Loading…</p>;
  }

  if (error) {
    return (
      <p className="app__error" role="alert">
        {error}
      </p>
    );
  }

  if (role !== 'Admin') {
    return (
      <section className="panel panel--warning" role="alert">
        <h2>Not authorized</h2>
        <p>You need the Admin role to manage users.</p>
      </section>
    );
  }

  return <>{children}</>;
}
