# Deployment lessons learned

Errors actually hit while building and deploying this app (React + .NET 10 +
Postgres + Azure Container Apps + GitHub Actions/Azure Pipelines), with root
cause and fix, so a future build on this stack doesn't re-discover the same
things the hard way. This is about **operational/deployment bugs**, not
architecture decisions — see `architecture-memory.md` for those.

Each entry: **Symptom** (what you'd see) → **Root cause** → **Fix** →
**Takeaway for next time**.

---

## 1. Azure subscription-level restrictions (show up only at real deploy time)

### 1a. Postgres Flexible Server region-restricted (`LocationIsOfferRestricted`)
- **Symptom:** `az deployment group create` / `az postgres flexible-server create`
  fails: *"Subscriptions are restricted from provisioning in location 'eastus'."*
- **Root cause:** some subscription tiers (free trial, free-credit, certain
  sponsored subscriptions) are blocked from provisioning Postgres Flexible
  Server in high-demand regions. `eastus`, `eastus2`, and `westus2` were all
  restricted on this subscription; `centralus` worked.
- **Fix:** probe candidate regions directly (`az postgres flexible-server
  create` against a disposable resource group is faster and more reliable
  than guessing from `list-skus`, which doesn't reflect subscription-level
  restrictions). Decouple the restricted resource's region from the rest of
  the stack via its own Bicep parameter (e.g. `postgresLocation` separate
  from `location`) rather than forcing everything into one region.
- **Takeaway:** *before* a real deploy, don't assume every resource type is
  available in your default region for your subscription tier. If a
  deployment fails with an "offer restricted" / "not permitted" error whose
  fix-it link is "file a support ticket" or "try another region," trying
  another region first is almost always faster.

### 1b. ACR Tasks disabled (`TasksOperationsNotAllowed`)
- **Symptom:** `az acr build` fails: *"ACR Tasks requests for the registry
  ... are not permitted."*
- **Root cause:** ACR Tasks (the cloud-side build service `az acr build`
  relies on) is paused on free-trial/free-credit subscriptions.
- **Fix:** build locally with `docker build` + `az acr login` + `docker push`
  instead of `az acr build`. Needs a Docker daemon on the CI runner (GitHub
  Actions/Azure Pipelines Microsoft-hosted runners have one; a local dev
  machine might not if Docker Desktop isn't running).
- **Takeaway:** don't assume "convenience" CLI commands that shell out to a
  managed service (ACR Tasks, similar for other "cloud build" style commands)
  work on every subscription tier — have a local-build fallback path ready.

### 1c. `Microsoft.Insights` resource provider not registered
- **Symptom:** creating an Application Insights component for the first time
  on a subscription fails with `MissingSubscriptionRegistration`.
- **Root cause:** fresh/lightly-used subscriptions don't have every resource
  provider namespace registered by default.
- **Fix:** `az provider register --namespace Microsoft.Insights` (one-time,
  free, per subscription). Same pattern applies to any `MissingSubscriptionRegistration`
  error for any namespace — register it, don't work around it.
- **Takeaway:** the first time you provision a *new resource type* (not just
  a new resource) on a subscription, check for this class of error before
  assuming something is wrong with your template.

---

## 2. Bicep / infra bugs specific to a bootstrap-then-imperative-deploy model

### 2a. Container App hangs (`Operation expired`) from a premature ACR registry reference
- **Symptom:** `az deployment group create` runs for ~20-30 minutes then
  fails: *"Failed to provision revision for container app ... Operation
  expired."* — and the Container App never gets a single revision, even a
  failed one.
- **Root cause:** the Bicep module wired an ACR `registries` entry
  (`identity: system`) onto the Container App **unconditionally**, even on
  the bootstrap deploy where the app was still running a public placeholder
  image unrelated to that ACR. Azure Container Apps appears to block
  provisioning while trying to resolve the managed identity's `AcrPull`
  access — which doesn't exist yet by design (the role grant is a manual
  step that happens *after* this very deploy, using its outputs). Chicken-
  and-egg: needs the grant to finish provisioning, but the grant needs this
  deploy's outputs to exist.
- **Fix:** only attach the ACR `registries` entry when the image actually
  comes from that ACR (`startsWith(image, registryLoginServer)`), leaving it
  empty for a placeholder-image bootstrap deploy.
- **Takeaway:** in a "bootstrap with placeholder image, cut over to the real
  image later" pattern, audit *every* piece of config that assumes the real
  image/registry is already in play — registry credentials being the
  sneaky one, since the symptom (a multi-minute hang, not an immediate
  error) doesn't obviously point at the cause.

### 2b. Target-port mismatch with the bootstrap placeholder image
- **Symptom:** same "Operation expired" symptom as above, or later a live
  "upstream connect error / connection refused" from the ingress once the
  app is running.
- **Root cause:** the real app images listen on port 8080 (hardcoded into
  the Bicep), but the public placeholder image used for the very first
  bootstrap deploy (`mcr.microsoft.com/k8se/quickstart:latest`) listens on
  port 80. Ingress health probes against the wrong port never succeed.
- **Fix:** make the container's target port an overridable Bicep parameter,
  defaulting to the real app's port (8080), and only override it for a
  placeholder-image-only bootstrap run.
- **Takeaway:** verify what port a placeholder/quickstart image *actually*
  listens on (check docs or just test it) rather than assuming it matches
  your real app's port.

### 2c. Running the full infra Bicep on every CD deploy silently regressed a running app
- **Symptom:** a Container App that was working (correct target port, real
  image) unexpectedly reverted to the placeholder image and the wrong
  target port, with no explicit action taken against it — discovered later
  as a broken "upstream connect error" in the UI.
- **Root cause:** the CD pipeline originally ran the *entire* infra Bicep
  template on every deploy (for "idempotency"). An auto-triggered CD run
  (fired by an unrelated PR merge, before other fixes landed) redeployed
  the whole template using the checked-in default parameters — which didn't
  yet have the port/region overrides pinned — silently resetting live
  Container App config that had been correctly tuned via out-of-band
  parameter overrides during bootstrap.
- **Fix:** two-part — (1) don't run the full infra Bicep on every CD
  deploy at all; provision infra once, manually, and have CD only do
  imperative image build/push/update against already-existing resources;
  (2) for anything that genuinely must diverge from a Bicep parameter's
  compile-time default (e.g. a region override forced by a subscription
  restriction), **pin the real value into the checked-in parameters file**,
  not just pass it as an ad-hoc CLI override — otherwise the *next* person
  (or automated run) who deploys "the normal way" silently reverts it.
- **Takeaway:** "just re-run the whole template, it's idempotent" is not
  actually safe once any parameter has a manually-tuned value that isn't
  reflected in the checked-in defaults. Either make CD stop touching infra
  entirely once it's stable, or make sure 100% of the deployed state is
  reconstructable from the checked-in files with no undocumented overrides.

### 2d. ACR pull credential never gets wired onto the app once CD stops running Bicep
- **Symptom:** after fixing 2c (CD no longer runs Bicep), a freshly
  pushed/built image fails to deploy: `UNAUTHORIZED` pulling from ACR, even
  though the `AcrPull` role was correctly granted to the app's managed
  identity.
- **Root cause:** the bootstrap Bicep deploy correctly left the Container
  App's registry credential unset (per 2a — it was using a placeholder
  image, not ACR). Once CD stopped running Bicep at all, nothing was left
  to *ever* configure that credential — the role grant existed, but nothing
  was pointing the app at it.
- **Fix:** add an idempotent `az containerapp registry set --identity
  system` call to CD itself (safe to run on every deploy — it's a no-op if
  already set).
- **Takeaway:** when you remove a "do everything" step (like a full Bicep
  redeploy) in favor of narrower imperative steps, explicitly re-derive the
  full list of things that step *used to* accomplish and check each one is
  still covered somewhere — don't just remove the parts that were obviously
  causing problems.

---

## 3. Azure CLI quirks and bugs (not your template's fault)

### 3a. `az role assignment create` fails with a nonsensical `MissingSubscription` error
- **Symptom:** `az role assignment create --assignee <id> --role <role>
  --scope <valid-scope>` fails: *"MissingSubscription: The request did not
  have a subscription or a valid tenant level resource provider."* — even
  with a correct, verified scope and an active subscription context.
- **Root cause:** appears to be an `az` CLI-level bug (reproduced
  consistently across multiple role assignments, both `--assignee` by app
  ID and `--assignee-object-id` by object ID).
- **Fix:** bypass the CLI wrapper and call the ARM REST API directly:
  `az rest --method put --url "https://management.azure.com<scope>/providers/Microsoft.Authorization/roleAssignments/<new-guid>?api-version=2022-04-01" --body '{"properties":{"roleDefinitionId":"<role-def-id>","principalId":"<principal-id>","principalType":"ServicePrincipal"}}'` — this worked every time the CLI command failed.
- **Takeaway:** if a well-formed `az` command fails with an error that
  doesn't match your actual input (wrong subscription complaint when the
  subscription is clearly right), don't keep tweaking the command —
  try the equivalent raw `az rest` call before assuming your own setup is
  wrong.

### 3b. `az postgres flexible-server ad-admin create/delete` intermittently fails with `InternalServerError`
- **Symptom:** the command fails with a bare, unhelpful `InternalServerError`
  and a tracking ID.
- **Root cause:** Microsoft's own docs (as of this project) state CLI/SDK
  support for Postgres Entra administrator management is still "coming
  soon" — the underlying ARM API is fine, the CLI wrapper is the weak
  point.
- **Fix:** same pattern as 3a — `az rest --method put` directly against
  `.../flexibleServers/<name>/administrators/<objectId>?api-version=2024-08-01`
  with the `principalType`/`principalName`/`tenantId` properties.
- **Takeaway:** for less-mature CLI command groups (check the docs for
  "coming soon" / preview language), have the raw ARM REST equivalent ready
  as your first fallback, not your last resort.

### 3c. The `--display-name` flag for a Postgres AAD admin actually needs the UPN, not a friendly name
- **Symptom:** no immediate error — `az postgres flexible-server ad-admin
  create --display-name "Jane Doe" --object-id <id>` succeeds, but the
  stored `principalName` is just the literal string you passed ("Jane Doe"),
  not anything Entra-derived.
- **Root cause:** the flag is misleadingly named. For a **user** principal,
  Azure expects the actual UPN (`user@domain.com`, or the long `#EXT#`
  guest form) in that field — not a human-friendly display name.
- **Fix:** pass the real UPN as `--display-name` (or `principalName` via raw
  REST) even though the flag name suggests otherwise. Get it via `az ad
  signed-in-user show --query userPrincipalName` (or `--query
  {objectId:id, principalName:userPrincipalName}` for both at once).
- **Takeaway:** don't trust a CLI flag's name for a less-common command
  group — check what value the *feature* actually expects (here: whatever
  string the login flow will present as the connecting principal), which
  can differ from the flag's literal name.

### 3d. Long `#EXT#` guest UPNs get silently truncated to 63 characters
- **Symptom:** after setting a long guest-account UPN as a Postgres admin's
  principal name, `az postgres flexible-server ad-admin list` shows a
  truncated value (missing the tail of the domain).
- **Root cause:** the `principalName` field appears to have a 63-character
  storage limit, and the write silently truncates rather than erroring.
- **Turned out to be harmless:** Postgres actually validates the connecting
  identity via the AAD access token's claims (`oid`), not a literal string
  match against the stored `principalName` — a login using the *full,
  untruncated* UPN as the connection username succeeded fine even though
  the stored admin record showed the truncated value.
- **Takeaway:** don't panic and start over if you see an unexpectedly
  truncated identity field — test whether it actually affects the behavior
  you care about (here: login) before assuming it's broken. But also: don't
  *rely* on this being safe for every field — verify empirically per field/
  API rather than assuming truncation is always cosmetic.

---

## 4. Networking / firewall gotchas

### 4a. Postgres firewall silently drops (not rejects) connections from outside Azure
- **Symptom:** connecting to a Postgres Flexible Server from a local dev
  machine (not inside Azure) times out — no auth error, no clear rejection,
  just a hang until timeout.
- **Root cause:** the standard "allow Azure services" firewall rule
  (`0.0.0.0`–`0.0.0.0`, a special-cased range) only allows traffic
  *originating from other Azure resources* — it does **not** open the
  server to the public internet. A local machine's public IP isn't covered
  by it at all, so packets are dropped rather than rejected.
- **Fix:** either (a) add a temporary firewall rule for your current public
  IP, do the work, then remove it, or (b) run the connecting client from
  something already inside Azure (Cloud Shell, a jumpbox, a CI runner).
- **Takeaway:** a connection **timeout** to an Azure PaaS database from a
  local machine, with correct credentials, is very often a firewall/network
  issue, not a credential/auth issue — check network reachability before
  spending time debugging the auth flow.

---

## 5. Application-code bugs that only surface against a real deployed environment

### 5a. Npgsql's `UsePeriodicPasswordProvider` did not reliably supply a password on the very first connection (UNRESOLVED — needs a real code fix)
- **Symptom:** switching a passwordless connection string over to Entra
  (managed-identity) auth via Npgsql's `UsePeriodicPasswordProvider`
  produced, on the very first connection attempt (the app's own startup DB
  migration): `Npgsql.NpgsqlException: No password has been provided but
  the backend requires one (in cleartext)` — i.e. the password came back
  completely empty on that first attempt, not merely wrong.
- **Root cause (best evidence so far, not fully confirmed):** despite the
  documented intent that the periodic provider's callback is invoked (and
  blocks) on first use, the observed behavior was an empty password on the
  first-ever connection from a freshly built `NpgsqlDataSource`. Everything
  else was verified correct: the Entra admin existed, the app's managed-
  identity-backed Postgres role was created and granted, the connection
  string was passwordless and correctly formatted, the config flag was
  correctly read (`Postgres__UseEntraAuth=true` confirmed live on the
  Container App).
- **Fix applied so far:** none yet — **rolled back to password-based auth**
  to restore the live app (this is a running production-ish service; a
  broken DB connection is a real outage, not just a failed feature). The
  code needs a proper fix — most likely, eagerly/synchronously fetching the
  first token *before* the `NpgsqlDataSource` is used for the first
  connection (rather than relying solely on `UsePeriodicPasswordProvider`'s
  own first-tick timing), then verified against this same live server
  before attempting the cutover again.
- **Also noted, and it's a red herring:** the container logs showed
  `libgssapi_krb5.so.2: cannot open shared object file: No such file or
  directory` right before the failure. This looked alarming but is
  unrelated — Postgres AAD auth over Npgsql doesn't require GSSAPI; it's
  a missing optional library in the minimal container base image that
  Npgsql tries and gracefully falls back from. Don't chase this lead.
- **Takeaway (the big one):** *"we wrote unit tests for the branching logic
  and it built/ran without a live database"* is not the same as *"this
  actually works against a real deployed Postgres."* This exact gap was
  called out explicitly in this project's own test report before the
  cutover was attempted (*"no live Entra-enabled Postgres server ... true
  end-to-end verification is deferred to a human/devops against the
  deployed environment"*) — and it's exactly where the real bug was hiding.
  For any feature whose correctness depends on a live managed-identity /
  token-refresh / cloud-credential flow, budget for a **real, live
  verification step against the actual target service** before calling it
  done — mocked/in-memory tests cannot catch this class of bug. Also:
  always have a fast, known-good rollback path ready *before* flipping a
  live app to a new auth mode, and use it immediately if the live check
  fails, rather than continuing to debug on a broken production system.

---

## 6. Environment / tooling gotchas (not Azure-specific)

- **PowerShell has no `read -rs` equivalent** — use `Read-Host -AsSecureString`
  and marshal it to plaintext via
  `[Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($sec))`
  if you need the plaintext value for a CLI argument.
- **Long-running Azure operations (Postgres/Container Apps deploys taking
  15-30+ minutes) will exceed a ~10-minute hard tool-call timeout even when
  "backgrounded."** Submit with `--no-wait` and poll status via separate,
  short `az ... show` calls instead of trying to keep one long-lived call
  alive.
- **A cheaper Postgres SKU below `Standard_B1ms` (Burstable) does not
  exist** on Azure Database for PostgreSQL Flexible Server — it's already
  the platform's minimum for a managed instance. If a lower bill is the
  goal, the real levers are stopping the server when idle, or accepting the
  ~$16-18/month floor, not hunting for a smaller SKU that isn't there.
