/**
 * A minimal store for one fact: "the API rejected our token with 401".
 *
 * The rejection is detected in the API client (outside React) and has to reach the
 * component tree, so it is recorded here as a flag plus a subscription rather than being
 * pushed into React state directly. Reading the flag at mount closes the race where the
 * 401 arrives before a listener is registered (specs §16.9.3).
 */
type Listener = () => void;

const listeners = new Set<Listener>();
let sessionRejected = false;

export function markSessionRejected(): void {
  sessionRejected = true;
  for (const listener of listeners) {
    listener();
  }
}

export function isSessionRejected(): boolean {
  return sessionRejected;
}

/** Test/sign-in helper: forget a previous rejection. */
export function resetSessionRejected(): void {
  sessionRejected = false;
}

/** Subscribes to rejections. Returns an unsubscribe function. */
export function onSessionRejected(listener: Listener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}
