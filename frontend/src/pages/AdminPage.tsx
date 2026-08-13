import { useCallback, useEffect, useState } from 'react';
import * as api from '../api';
import { useCurrentUser } from '../auth/UserContext';
import type { UserProfile, UserRole } from '../types';

/**
 * Admin user list with promote/demote (specs §16.8, §16.9.4).
 *
 * Only ever rendered inside `<RequireAdmin>`, so it does not itself gate on the role —
 * and the server enforces it regardless: `GET /api/users` and
 * `PATCH /api/users/{id}/role` return 403 for a non-admin, plus 409 for self-demotion or
 * for removing the last admin. Those conflicts are surfaced verbatim from the API's
 * ProblemDetails rather than re-implemented here.
 */
export function AdminPage() {
  const { user: me } = useCurrentUser();
  const [users, setUsers] = useState<UserProfile[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setUsers(await api.listUsers());
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not load users.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const changeRole = async (target: UserProfile, role: UserRole) => {
    setError(null);
    setBusyId(target.id);
    try {
      const updated = await api.updateUserRole(target.id, role);
      setUsers((prev) => prev.map((u) => (u.id === updated.id ? updated : u)));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not change the role.');
    } finally {
      setBusyId(null);
    }
  };

  return (
    <section className="admin">
      <h2>Users</h2>

      {error && (
        <p className="app__error" role="alert">
          {error}
        </p>
      )}

      {loading ? (
        <p className="app__loading">Loading…</p>
      ) : (
        <table className="admin__table">
          <thead>
            <tr>
              <th scope="col">Name</th>
              <th scope="col">Email</th>
              <th scope="col">Role</th>
              <th scope="col">Action</th>
            </tr>
          </thead>
          <tbody>
            {users.map((u) => {
              const isSelf = me?.id === u.id;
              const nextRole: UserRole = u.role === 'Admin' ? 'User' : 'Admin';

              return (
                <tr key={u.id}>
                  <td>{u.displayName ?? '—'}</td>
                  <td>{u.email ?? '—'}</td>
                  <td>{u.role}</td>
                  <td>
                    <button
                      type="button"
                      className="btn"
                      disabled={busyId === u.id || (isSelf && nextRole === 'User')}
                      onClick={() => void changeRole(u, nextRole)}
                    >
                      {nextRole === 'Admin' ? 'Make Admin' : 'Make User'}
                    </button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </section>
  );
}
