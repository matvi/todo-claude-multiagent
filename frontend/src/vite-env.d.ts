/// <reference types="vite/client" />

// All of these are baked into the bundle at build time (`--build-arg`). They are public
// identifiers — a base URL, a tenant subdomain, a tenant GUID, a public-client id and a
// scope name. No secret is ever passed to the frontend build (specs §16.6(3)).
interface ImportMetaEnv {
  readonly VITE_API_BASE_URL: string;
  readonly VITE_ENTRA_TENANT_SUBDOMAIN: string;
  readonly VITE_ENTRA_TENANT_ID: string;
  readonly VITE_ENTRA_SPA_CLIENT_ID: string;
  readonly VITE_API_SCOPE: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
