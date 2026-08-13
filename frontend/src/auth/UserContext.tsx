import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';
import * as api from '../api';
import type { UserProfile, UserRole } from '../types';

export interface CurrentUserState {
  user: UserProfile | null;
  /**
   * The role the **server** reported for this user. It is display state only: every admin
   * action is authorized again server-side against PostgreSQL, so nothing here can grant
   * access (specs §16.9.4).
   */
  role: UserRole | null;
  loading: boolean;
  error: string | null;
  reload: () => Promise<void>;
}

const CurrentUserContext = createContext<CurrentUserState | null>(null);

/**
 * Fetches `GET /api/users/me` once after sign-in and shares it with the app. The row is
 * provisioned server-side on the first authenticated request, so this call is what
 * surfaces the caller's profile and role (specs §16.9.4).
 */
export function UserProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<UserProfile | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setUser(await api.getMe());
    } catch (err) {
      setUser(null);
      setError(err instanceof Error ? err.message : 'Could not load your profile.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void reload();
  }, [reload]);

  const value = useMemo<CurrentUserState>(
    () => ({ user, role: user?.role ?? null, loading, error, reload }),
    [user, loading, error, reload],
  );

  return (
    <CurrentUserContext.Provider value={value}>{children}</CurrentUserContext.Provider>
  );
}

export function useCurrentUser(): CurrentUserState {
  const context = useContext(CurrentUserContext);
  if (!context) {
    throw new Error('useCurrentUser must be used inside a <UserProvider>.');
  }
  return context;
}
