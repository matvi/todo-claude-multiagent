import { afterEach, describe, expect, it, vi } from 'vitest';
import { configureApiAuth, listTodos } from './api';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/**
 * api.ts's auth wiring (specs §16.9.3, §16.18 items 27-28). Kept in a separate file from
 * the pre-existing api.test.ts so that file's "no provider configured" assumptions are
 * never disturbed by test ordering.
 */
describe('api.ts auth wiring', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    // Always detach: configureApiAuth is module-level state that would otherwise leak into
    // whichever test file vitest runs next (including the pre-existing api.test.ts).
    configureApiAuth(null);
  });

  it('item 27: attaches Authorization: Bearer <token> when a token provider is configured', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse([]));
    vi.stubGlobal('fetch', fetchMock);
    configureApiAuth(async () => 'abc123');

    await listTodos();

    const [, init] = fetchMock.mock.calls[0];
    const headers = init!.headers as Record<string, string>;
    expect(headers.Authorization).toBe('Bearer abc123');
  });

  it('item 27: sends no Authorization header when no provider is configured (existing-test compatibility)', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse([]));
    vi.stubGlobal('fetch', fetchMock);
    configureApiAuth(null);

    await listTodos();

    const [, init] = fetchMock.mock.calls[0];
    const headers = init!.headers as Record<string, string>;
    expect(headers.Authorization).toBeUndefined();
  });

  it('sends no Authorization header when the provider resolves null (not signed in)', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse([]));
    vi.stubGlobal('fetch', fetchMock);
    configureApiAuth(async () => null);

    await listTodos();

    const [, init] = fetchMock.mock.calls[0];
    const headers = init!.headers as Record<string, string>;
    expect(headers.Authorization).toBeUndefined();
  });

  it('item 28: a 401 response invokes the onUnauthorized callback (and still throws ApiError)', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ title: 'Unauthorized' }, 401));
    vi.stubGlobal('fetch', fetchMock);
    const onUnauthorized = vi.fn();
    configureApiAuth(async () => 'expired-token', onUnauthorized);

    await expect(listTodos()).rejects.toMatchObject({ status: 401 });
    expect(onUnauthorized).toHaveBeenCalledTimes(1);
  });

  it('does not invoke onUnauthorized for a non-401 error response', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ title: 'Server error' }, 500));
    vi.stubGlobal('fetch', fetchMock);
    const onUnauthorized = vi.fn();
    configureApiAuth(async () => 'token', onUnauthorized);

    await expect(listTodos()).rejects.toMatchObject({ status: 500 });
    expect(onUnauthorized).not.toHaveBeenCalled();
  });

  it('does not invoke onUnauthorized on a successful (2xx) response', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse([]));
    vi.stubGlobal('fetch', fetchMock);
    const onUnauthorized = vi.fn();
    configureApiAuth(async () => 'token', onUnauthorized);

    await listTodos();

    expect(onUnauthorized).not.toHaveBeenCalled();
  });
});
