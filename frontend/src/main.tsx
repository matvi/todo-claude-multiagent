import { StrictMode } from 'react';
import { createRoot, type Root as ReactRoot } from 'react-dom/client';
import { PublicClientApplication } from '@azure/msal-browser';
import { MsalProvider } from '@azure/msal-react';
import Root from './Root';
import { configureApiAuth } from './api';
import { markSessionRejected } from './auth/authSession';
import { createAccessTokenProvider } from './auth/tokenProvider';
import { isAuthConfigured, msalConfig } from './authConfig';
import './index.css';

const rootElement = document.getElementById('root');
if (!rootElement) {
  throw new Error('Root element #root not found in index.html');
}

const reactRoot: ReactRoot = createRoot(rootElement);

/**
 * A build that was not given the Entra identifiers cannot sign anyone in, and every API
 * call would fail with 401. Say so plainly instead of rendering an app that silently does
 * not work — and never fall back to an unauthenticated mode (specs §16.9.1).
 */
function renderConfigurationError() {
  reactRoot.render(
    <StrictMode>
      <main className="app">
        <h1 className="app__title">Todos</h1>
        <p className="app__error" role="alert">
          Sign-in is not configured for this build. The VITE_ENTRA_* / VITE_API_SCOPE
          build arguments must be supplied when the image is built.
        </p>
      </main>
    </StrictMode>,
  );
}

async function bootstrap() {
  if (!isAuthConfigured) {
    renderConfigurationError();
    return;
  }

  const pca = new PublicClientApplication(msalConfig);

  // Both awaits are required before rendering: MSAL v3+ must be initialized, and the
  // redirect response must be consumed so the account is in the cache (specs §16.9.2).
  await pca.initialize();
  const redirectResult = await pca.handleRedirectPromise();

  const account = redirectResult?.account ?? pca.getAllAccounts()[0] ?? null;
  if (account) {
    pca.setActiveAccount(account);
  }

  // Registered before the first render so no component can issue an API call without a
  // bearer token. A 401 means the token was rejected: drop the cached session and let the
  // app fall back to the login screen instead of retrying (specs §16.9.3).
  configureApiAuth(createAccessTokenProvider(pca), () => {
    void pca.clearCache();
    markSessionRejected();
  });

  reactRoot.render(
    <StrictMode>
      <MsalProvider instance={pca}>
        <Root />
      </MsalProvider>
    </StrictMode>,
  );
}

void bootstrap().catch(() => {
  // Initialization/redirect handling failed (e.g. a malformed authority). Show the same
  // explicit failure rather than a blank page.
  renderConfigurationError();
});
