import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  isSessionRejected,
  markSessionRejected,
  onSessionRejected,
  resetSessionRejected,
} from './authSession';

describe('authSession (specs §16.9.3 — the plumbing behind §16.18 item 28)', () => {
  beforeEach(() => {
    resetSessionRejected();
  });

  it('isSessionRejected is false until markSessionRejected is called', () => {
    expect(isSessionRejected()).toBe(false);

    markSessionRejected();

    expect(isSessionRejected()).toBe(true);
  });

  it('notifies subscribers when the session is rejected', () => {
    const listener = vi.fn();
    const unsubscribe = onSessionRejected(listener);

    markSessionRejected();

    expect(listener).toHaveBeenCalledTimes(1);
    unsubscribe();
  });

  it('an unsubscribed listener is never called', () => {
    const listener = vi.fn();
    const unsubscribe = onSessionRejected(listener);
    unsubscribe();

    markSessionRejected();

    expect(listener).not.toHaveBeenCalled();
  });

  it('resetSessionRejected clears the flag (test / sign-in helper)', () => {
    markSessionRejected();
    expect(isSessionRejected()).toBe(true);

    resetSessionRejected();

    expect(isSessionRejected()).toBe(false);
  });
});
