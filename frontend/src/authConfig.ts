import type { Configuration, RedirectRequest } from '@azure/msal-browser';

/**
 * MSAL configuration for the Entra External ID (CIAM) tenant — specs §16.9.1.
 *
 * Every value here is a **public identifier** (tenant subdomain, tenant GUID, SPA client
 * id, scope name), baked into the bundle at build time via `--build-arg`. None of them is
 * a secret: this is a public client using the authorization-code flow with PKCE, and it
 * has no client secret by design (a secret in a SPA is a published secret).
 */
const tenantSubdomain = import.meta.env.VITE_ENTRA_TENANT_SUBDOMAIN ?? '';
const tenantId = import.meta.env.VITE_ENTRA_TENANT_ID ?? '';
const spaClientId = import.meta.env.VITE_ENTRA_SPA_CLIENT_ID ?? '';
const apiScope = import.meta.env.VITE_API_SCOPE ?? '';

/**
 * True when the build was given all four identity settings. The app refuses to render the
 * todo UI without them: an unconfigured build must fail visibly rather than silently
 * present an app whose every API call will 401.
 */
export const isAuthConfigured: boolean = Boolean(
  tenantSubdomain && tenantId && spaClientId && apiScope,
);

export const authority = `https://${tenantSubdomain}.ciamlogin.com/${tenantId}`;

export const msalConfig: Configuration = {
  auth: {
    clientId: spaClientId,
    authority,
    // REQUIRED for *.ciamlogin.com: MSAL rejects authorities it does not recognise as
    // Microsoft-owned unless they are listed here (specs §16.9.1).
    knownAuthorities: [`${tenantSubdomain}.ciamlogin.com`],
    redirectUri: window.location.origin,
    postLogoutRedirectUri: window.location.origin,
    navigateToLoginRequestUrl: true,
  },
  cache: {
    // sessionStorage (not localStorage) shrinks the XSS blast radius: tokens die with the
    // tab. A brand-new tab re-runs ssoSilent against the IdP session cookie.
    cacheLocation: 'sessionStorage',
    storeAuthStateInCookie: false,
  },
};

/** The delegated permission the API requires (`scp = Todos.ReadWrite`). */
export const loginRequest: RedirectRequest = {
  scopes: [apiScope],
};

/**
 * "Continue with Google": `domain_hint` is documented to route straight to the federated
 * IdP. If the hosted user flow ignores it, the user simply lands on the normal sign-in
 * page where Google is one click away — it degrades to the standard flow, never to an
 * error (specs §16.9.2, assumption A4).
 */
export const googleLoginRequest: RedirectRequest = {
  ...loginRequest,
  extraQueryParameters: { domain_hint: 'google.com' },
};
