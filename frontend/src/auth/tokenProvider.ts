import {
  InteractionRequiredAuthError,
  type IPublicClientApplication,
} from '@azure/msal-browser';
import { loginRequest } from '../authConfig';
import type { AccessTokenProvider } from '../api';

/**
 * Builds the access-token provider handed to the API client (specs §16.9.3).
 *
 * Silent acquisition first — MSAL serves it from cache or refreshes it behind the scenes;
 * when the identity platform says interaction is required, fall back to a redirect and
 * resolve to `null`, because the page is navigating away and the current request is moot.
 *
 * Deliberately a plain factory rather than a React hook: it is registered **before** the
 * component tree renders, so no component can issue its first API call before a token
 * provider exists (child effects run before parent effects in React, which would
 * otherwise produce a spurious 401 on load).
 */
export function createAccessTokenProvider(
  pca: IPublicClientApplication,
): AccessTokenProvider {
  return async () => {
    const account = pca.getActiveAccount() ?? pca.getAllAccounts()[0] ?? null;
    if (!account) {
      return null;
    }

    try {
      const result = await pca.acquireTokenSilent({ ...loginRequest, account });
      return result.accessToken;
    } catch (error) {
      if (error instanceof InteractionRequiredAuthError) {
        await pca.acquireTokenRedirect({ ...loginRequest, account });
        return null;
      }
      throw error;
    }
  };
}
