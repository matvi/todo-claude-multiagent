# Review — End-user authentication with Microsoft Entra External ID (specs §16)

Branch: `pipeline/entra-external-id-auth` · reviewed commit `01bfaac` plus the
uncommitted devops working-tree changes (`.github/workflows/*.yml`,
`azure-pipelines.yml`, `.pipeline/infra.md`).
Reviewer: reviewer agent · Date: 2026-08-13.
This **overwrites** the stale 2026-07-23 review from an unrelated cycle.

---

## VERDICT: APPROVED

---

## Rationale

Everything below was verified against the repository itself, not against the
self-reports in `changes.md` / `tests.md` / `infra.md`.

### 1. The spec's security requirements are actually implemented

- **Deny-by-default is structural, not per-controller.**
  `AuthenticationRegistration.cs:60-62` sets
  `FallbackPolicy = RequireAuthenticatedUser()`. A repo-wide grep of
  `backend/src` for endpoints and `AllowAnonymous` returns exactly **one**
  carve-out: `Program.cs:72-74` (`GET /health`). Both controllers
  (`TodosController.cs:24-31`, `UsersController.cs:24-29`) carry
  `[Authorize]`; there is no third controller and no minimal-API endpoint that
  escapes the fallback policy. Spec §16.7.3 / §16.7.7 satisfied.
- **Scope enforcement is on *both* controllers**, which was §16.7.2a's explicit
  gap. `TodosController.cs:31` and `UsersController.cs:28` both carry
  `[RequiredScope(RequiredScopesConfigurationKey = "AzureAd:Scopes")]`, bound to
  configuration, not a literal. The `git diff` on `TodosController.cs` is
  **attributes + XML doc only** — no route, DTO, status code or ordering change,
  as §16.15 requires.
- **`RoleClaimsTransformation` does not trust inbound roles.**
  `RoleClaimsTransformation.cs:145-174` rebuilds *every* identity on the
  principal (`principal.Identities`, not just the primary) filtering out `role`,
  `roles`, `ClaimTypes.Role` **and** the identity's own `RoleClaimType`, then
  writes exactly one role claim from the DB row
  (`RoleClaimsTransformation.cs:117`). The principal is cloned, never mutated in
  place. `tid` is re-checked (`:70-78`) and `oid` is the only identity key —
  `ProfileClaimsReader.TryGetObjectId` has **no** fallback to `sub`, `email` or
  `preferred_username` (`ProfileClaimsReader.cs:43-49`).
- **Fail-closed is real, not simulated.** The provisioning `catch`
  (`RoleClaimsTransformation.cs:101-111`) adds `role_source_unavailable` and
  **no** role claim; `AuthOutcomeMiddleware.cs:48-56` maps that to `503`
  `ProblemDetails` before `UseAuthorization()` runs. There is no "assume User"
  branch anywhere. `AuthOutcomeMiddleware.cs:41-46` skips `IAllowAnonymous`
  endpoints, so `/health` survives a broken token.
- **Startup fail-fast covers all five keys plus shape.**
  `EntraAuthOptions.cs:50-84` names every missing key in one exception, and
  additionally rejects a non-`https` `Instance` and a non-GUID `TenantId`.
  `Users:PhoneClaimName` is correctly *not* in the list.
- **Token validation is hardened, never disabled.**
  `AuthenticationRegistration.cs:95-130`: `RequireHttpsMetadata = true`, all
  four `Validate*` flags forced `true`, `ClockSkew` left at the default,
  `MapInboundClaims = false`, and `ValidAudiences`/`ValidIssuers` are **unioned**
  with the existing framework-derived values (`Union` at `:132-137`) rather than
  replaced — an allow-list addition that cannot weaken validation. The
  documented `IDX10205` remedy is applied pre-emptively; issuer validation is
  never turned off.
- **Provisioning cannot be raced into Admin.** `UserProvisioningService.cs:69`
  writes the server-side literal `UserRole.User`; the 23505 path detaches and
  re-reads (`:87-108`), and rethrows if no winner exists (so it still fails
  closed). No "first user is Admin" rule exists.
- **Middleware order matches §16.7.6 exactly** (`Program.cs:57-76`):
  `UseCors → UseAuthentication → UseAuthOutcome → UseAuthorization → /health
  (AllowAnonymous) → MapControllers`.
- **Data model matches §16.4.** `TodoDbContext.cs` adds `users` with a unique
  `ix_users_entra_object_id`, `ck_users_role` CHECK, role as text via
  `HasConversion<string>()`. Migration `20260812224117_AddUsers.cs` is
  **additive only** — it creates `users` and touches `todos` not at all; `todos`
  gains no `owner_id` and no FK (§16.4.3 honoured).

### 2. changes.md's claims match the diff

Spot-checked every claim in the §16 entry of `changes.md` that had security or
contract weight. All held. The two most load-bearing — "closed the
`[RequiredScope]` gap on `TodosController`" and "inbound role claims are
stripped before the DB role is written" — are both true in the code, not just in
prose. The declared deviations (token-provider factory instead of a hook,
optional `onUnauthorized` second arg, `RequiredScopeAttribute` living in
`Microsoft.Identity.Web.Resource`, `Results.Problem` for the ProblemDetails
body, "sign-in not configured" screen) are all visible in the source and are
justified, not scope drift.

### 3. tests.md's PASS is real, and the tests are substantive

I re-ran both suites myself:

```
dotnet test TodoApi.sln -c Release  → Passed! Failed: 0, Passed: 164, Skipped: 0, Total: 164
npm test                            → 12 files, 60 passed (60)
```

Both numbers match `tests.md` exactly. The tests are not decorative:

- **Scope enforcement (item 30)** is proven *discriminatingly*: no `scp` ⇒ 403 on
  `/api/todos` **and** `/api/users/me`, wrong `scp` ⇒ 403, correct `scp` ⇒ 200
  (`AuthenticationEndpointTests.cs:145-205`). The positive control is what makes
  this proof rather than assertion, and it settles the tester's own honest
  uncertainty about whether `[RequiredScope]` wires up without
  `AddRequiredScopeAuthorization()`: empirically, it does.
- **Fail-closed 503** is exercised through the *real* transformation and the
  *real* middleware via a DI-swapped throwing `IUserProvisioningService`
  (`TodoApiFactory.cs:150-159`), including
  `RoleSourceUnavailable_ViaThrowingProvisioningService_DoesNotSilentlyGrantAdmin`
  and `..._HealthStaysUp`. Not a mock of the outcome.
- **Anti-escalation** is covered both as a unit test and end-to-end
  (`GetUsers_SpoofedAdminRoleClaimInToken_ButDbRowIsUser_Returns403`).
- **Startup fail-fast** is covered for all five keys individually, all five
  together, non-TLS `Instance` and non-GUID `TenantId` (`EntraAuthOptionsTests`).
- The test host swaps only the *default* scheme; the real `JwtBearer` handler
  registration is asserted to still exist
  (`RealJwtBearerScheme_IsStillRegistered_TestSchemeIsOnlyTheDefault`), so the
  swap is provably a swap and not a removal. §16.18 item 22's sanctioned
  modification is confined to `TodoApiFactory.cs` / `ObservabilityTests.cs`;
  `TodoDbContextRegistrationTests` and `EntraTokenPasswordProviderTests` do not
  appear in `git diff main...HEAD` at all, as item 23 requires.

**The "last remaining Admin" 409 branch (tester's Note A) — invariant confirmed
holds.** I traced it independently: `UsersController.UpdateRole` gates on
`AdminOnly`, so the actor's DB row is `Admin` at request time; the count at
`:149-150` excludes only `target.Id`, so whenever `target != actor` the actor
is still counted and the count is ≥ 1. The only path to zero Admins is
`target == actor`, which guard (1) at `:135-143` catches first with 409. The
outward guarantee "never zero Admins" therefore holds via the self-demotion
guard. The second guard is defensive, not dead-and-dangerous. Not a defect.

### 4. IaC and pipelines match infra.md

- `infra/modules/keyvault.bicep` — `enableRbacAuthorization: true`,
  `accessPolicies: []`, soft-delete 7d, purge protection off, Standard SKU.
  **No `Microsoft.KeyVault/vaults/secrets` child resource, no secret value, no
  `Microsoft.Authorization/roleAssignments` anywhere in `infra/`** (grepped).
  Neither `todo-api`'s nor `todo-web`'s managed identity is granted anything on
  the vault — the documented, deliberate deviation from the literal request, and
  the correct one (§16.6).
- `infra/main.bicep:85-105` adds four non-`@secure()` identifier params with
  **empty-string defaults**; `:236-243` composes `AzureAd__Instance` /
  `AzureAd__Audience` behind `empty()` guards so a blank input stays blank rather
  than becoming a plausible-looking broken URL; `:303-327` adds the five plain
  env vars to `todo-api`. `todo-api` still passes **zero** Container Apps
  secrets. `main.parameters.json` carries only the non-secret `apiScopeName`
  filled in.
- **Exactly one system auto-triggers.** `azure-pipelines.yml:17-18` is
  `trigger: none` / `pr: none` (manual only, with a documented 3-step switchover
  that includes disabling GitHub Actions). `.github/workflows/ci.yml` triggers on
  push/PR (validation only, credential-free, no Azure login).
  `.github/workflows/cd.yml:24-27` triggers on `pull_request: [closed]` into
  `main`, job-gated by `if: github.event.pull_request.merged == true` (`:53`),
  with `workflow_dispatch` removed — so a direct push to `main` deploys nothing
  and an unmerged close deploys nothing. Image tag is
  `pull_request.merge_commit_sha`, not the ephemeral `github.sha`. Correct.
- **The CI commands are the real ones**: `dotnet restore/build/test TodoApi.sln
  -c Release` and `npm ci && npm run build && npm test` — byte-identical to what
  I just ran successfully. No invented lint or audit gate; the absence of both is
  explained and correct given `package.json` has no `lint` script and the known
  dev-only esbuild/vite advisories.
- CD's two preflights (repo variables set; five non-blank `AzureAd__*` already on
  `todo-api`) are read-only and fail *before* touching anything — they convert
  the "revision that never goes healthy" hazard into a clean refusal. Good call.
- Deploy is gated behind a merged PR **and** the `production` GitHub Environment
  (`cd.yml:56`), which is where a required-reviewer rule attaches without
  editing the workflow. Auth is OIDC only; no client secret anywhere.

### 5. Security scan

- **No hardcoded secrets.** Grepped the whole tree (including every
  `.pipeline/*.md`) for `client_secret`, `password=<value>`, API-key/PAT/HMAC
  shapes, `AccountKey=`, `BEGIN PRIVATE KEY`, `ghp_`, `xox*`. Every hit is
  documentation *about* the absence of a password (e.g. `specs.md:1982` is
  explicitly `Password=REDACTED`). `.env.example`, `frontend/.env.development`,
  `appsettings.json`, both Dockerfiles and all three pipeline files contain
  blanks and placeholders only. The five `AzureAd__*` and four `VITE_*` values
  are genuinely public identifiers (tenant GUID, subdomain, client ID, scope
  name) and are correctly plain env vars / build args, not secrets.
- **Injection:** EF Core LINQ only in `UsersController` and
  `UserProvisioningService`; no raw SQL, no string-built predicates, no dynamic
  `FromSql`. Route `{id}` is `Guid.TryParse`d before use (`UsersController.cs:110`).
- **Mass assignment:** `UpdateUserProfileRequest` has exactly two `init`
  properties with `[MaxLength(256)]` / `[MaxLength(64)]`; `role`, `email`, `id`,
  `entraObjectId` have no bindable surface. `UpdateUserRoleRequest` accepts a
  string parsed case-sensitively against the enum (`ignoreCase: false`), matching
  the DB CHECK.
- **IDOR:** `GET/PUT /api/users/me` resolve the row by the caller's own `oid`
  (`UsersController.cs:190-196`), never by a client-supplied id.
- **Insecure deserialization:** none — `System.Text.Json` into sealed DTOs, no
  polymorphic/type-name handling, no `BinaryFormatter`, no `eval`-equivalent on
  the client.
- **Logging:** `AuthOutcomeMiddleware.cs:60-64` logs method + path only;
  `UserProvisioningService.cs:81-83` logs `oid` only;
  `UsersController.cs:171-176` logs actor `oid`, target id, old/new role. No
  token, `Authorization` header, email, display name or phone reaches a log
  statement anywhere in `Auth/` or `Controllers/`.
- **Frontend:** `sessionStorage` token cache, no `dangerouslySetInnerHTML`
  anywhere, admin gating is server-enforced with the client-side hide explicitly
  labelled UX-only in `RequireAdmin.tsx` and `Root.tsx`. `configureApiAuth`'s
  401 handler clears the MSAL cache and drops to the login screen rather than
  retry-looping.

### 6. SOLID (spot-check of the security-critical classes)

`RoleClaimsTransformation` depends on `IUserProvisioningService` and
`IOptions<…>` only — which is exactly what let the tester inject a throwing role
source and prove fail-closed behaviour through production code (DIP paying off
concretely, not decoratively). `ProfileClaimsReader` is pure and static with no
authorization influence; `AuthOutcomeMiddleware` is the single place the
failure-marker → HTTP-status mapping exists; `EntraAuthOptions` owns binding +
validation and nothing else. The claim in `changes.md` is backed by the diff.

### 7. PCI DSS — verified independently, not accepted on assertion

I grepped `backend/`, `frontend/`, `infra/`, `.github/` and `azure-pipelines.yml`
for `payment|cardhold|PAN|cvv|cvc|card_number|cardNumber|stripe|checkout|
invoice|billing`. **Zero hits in source, schema, DTOs, IaC or pipelines** (the
only matches were the string `checkout` inside `actions/checkout@v4` and a
base64 substring in a NuGet lock file). The data model is `todos` (title,
description, flag, timestamps) + `users` (oid, email, display name, phone, role,
timestamps). There is no payment entity, endpoint, column, provider integration
or log field. No raw PAN/CVV/track data is stored anywhere, encrypted or
otherwise, because none exists to store. Nothing in §16 introduces any — the two
roles carry no payment meaning, and `Admin` grants exactly `GET /api/users` and
`PATCH /api/users/{id}/role`. §16.10's forward constraints for a future payments
feature (processor tokenization/hosted fields only; `Admin` never implicitly
grants CDE access; no `[AllowAnonymous]` on payment endpoints; §13.6 Key Vault
*reference* pattern for processor credentials; no payment data in logs) are
recorded and are consistent with this implementation's posture. **PCI DSS is not
in scope for this application, and this cycle does not change that.**

### 8. The three open items I was asked to weigh

- **(a) Unverifiable branch protection / no PR yet** — genuinely an
  environment/credential gap (`gh` is unauthenticated; the check requires a token
  even to read). `infra.md` states this as a *finding* with the exact command and
  the exact remediation rather than glossing it. Correct handling. It does mean
  the human-approval gate is currently a convention — see suggestion 4.
- **(b) `Postgres__UseEntraAuth=true` still in `infra/main.bicep:296`** —
  **reasonable scoping, not dodged work.** §16.12 item 2 *instructs* this cycle
  to leave it alone because `pipeline/dev-environment` removes it and duplicating
  the change would conflict. A backend test pins that the app ignores the value,
  so it is dead config, not a live defect. The devops agent flagged it in Drift
  notes rather than silently leaving it. Correct.
- **(c) The unreachable "last remaining Admin" branch** — invariant re-derived
  independently above and it holds. Not a defect.

---

## Blocking issues

**None.**

---

## Non-blocking suggestions

1. **Strip inbound `todo:*` marker claims before the idempotence guard
   (highest-value hardening).** `RoleClaimsTransformation.cs:63` returns the
   principal *untouched* if it already carries `todo:role_source` or
   `todo:auth_failure`. Those claim types are only namespaced, not filtered — a
   token carrying both `todo:role_source = "db"` **and** `roles = Admin` would
   short-circuit at line 63 with the inbound `roles` claim never stripped, and
   `RequireRole("Admin")` would then succeed. This is **not exploitable today**:
   claim *types* are set by the issuing tenant, not by the end user, and Entra
   directory-extension claims are named `extension_<appId>_<name>` (colons are
   not permitted), so no self-service sign-up path can mint a `todo:`-prefixed
   claim. But §16.7.1a asserts these claims are ones "a token can never spoof",
   and today that is a naming convention rather than an enforced property.
   Fix: filter `TodoClaimTypes.RoleSource` / `TodoClaimTypes.AuthFailure` out of
   the cloned identity in `CloneWithoutRoleClaims`, and evaluate the idempotence
   guard against the *transformation's own* per-request state (or against a claim
   whose value is a per-request nonce) rather than a claim type the token could
   in principle carry. Suggested regression test for the tester:
   `TransformAsync_TokenCarryingForgedRoleSourceMarkerAndAdminRole_StillResultsInUser`.
2. **The devops PIPELINE-APPLY artifacts are not committed.** `.github/workflows/
   ci.yml`, `.github/workflows/cd.yml`, `azure-pipelines.yml` and
   `.pipeline/infra.md` are modified in the working tree but absent from
   `git diff main...HEAD` at `01bfaac`. Everything I approved above about the
   pipelines describes the **working tree**; the committed `cd.yml` still
   triggers on `push: main` + `workflow_dispatch`. Commit them before opening the
   PR, or the CD hardening (merged-PR-only trigger, both preflights, the four
   frontend build args, paired rollback summary) does not ship. This is
   mechanical, not rework — hence not blocking — but it must not be forgotten.
3. **Branch protection on `main` is the one gate nothing in this repo enforces.**
   Until a human runs `gh auth login` and applies the rule (`infra.md`'s
   "Branch protection status" section has the exact five steps), a direct push to
   `main` bypasses review entirely — and while CD would not deploy such a push,
   the code would still be on the trunk. While you are there, consider adding a
   required reviewer to the `production` GitHub Environment that `cd.yml:56`
   already targets; that turns the deploy approval from convention into
   enforcement without touching the workflow.
4. **`Microsoft.KeyVault` is `NotRegistered` on the subscription.** A
   subscription Owner must `az provider register -n Microsoft.KeyVault`; the
   RG-scoped CD principal cannot. The first Key Vault deploy fails with
   `MissingSubscriptionRegistration` otherwise. Already documented in
   `infra.md` and `infra/README.md` Phase 5 — repeating it here so it is on the
   merge checklist.
5. **A full `az deployment group create` remains a hazardous operation.**
   `todoApiImage`/`todoWebImage` default to
   `mcr.microsoft.com/k8se/quickstart:latest` and are unpinned in
   `main.parameters.json`, so an unparameterised apply replaces both running apps
   with the Azure quickstart page; and the live app still has
   `Postgres__UseEntraAuth=false` + the `todo-db-connection` secret while the IaC
   encodes the §13/§14 cutover, so an apply performs that cutover as a side
   effect. `infra.md` documents both correctly and prescribes pinning the current
   tags. Consider making that safer structurally later (e.g. make the image
   params required with no default so a forgetful apply fails instead of
   reverting), rather than relying on the runbook.
6. **`main.parameters.json` still has three blank identity params.** Unavoidable
   until the §16.11 portal bootstrap produces them, but note the ordering trap
   already called out: if Path B (`az containerapp update --set-env-vars`) runs
   first and the params are never backfilled, a later full apply blanks the five
   env vars and the API fails fast. Backfill both, or neither.
7. **Swagger is registered before the auth middleware in Development**
   (`Program.cs:47-51`), so `/swagger` and the OpenAPI document are reachable
   without a token locally. Development-only and unchanged from prior cycles, so
   not a finding — but it is worth a one-line comment saying so, since a reader
   who sees "everything is authenticated by default" may be surprised.
8. **`PUT /api/users/me` is full-replace, not patch semantics.** Omitting
   `phoneNumber` sets it to `null` (`UsersController.cs:74-76`). That is correct
   for `PUT` and consistent with `PUT /api/todos/{id}`, but the SPA has no
   profile-edit UI yet, so whoever builds one must send both fields. Worth a note
   in `README.dev.md`'s API table.
9. **Consider covering the "last remaining Admin" guard under true request
   concurrency** if that code is ever intended to be load-bearing — otherwise
   consider annotating it in code as deliberately defensive/unreachable via the
   public API, so a future reader does not delete it as dead code or, worse,
   remove the self-demotion guard believing this one covers it.
10. **`dotnet-install.sh` is untracked at the repo root.** Unrelated to this
    cycle (`changes.md` says so), but it should be deleted or `.gitignore`d so it
    does not get swept into a future commit.
11. **Record the `[RequiredScope]` wiring finding in
    `architecture-memory.md`.** The tester established empirically that
    `AddMicrosoftIdentityWebApi` enables `[RequiredScope]` enforcement *without*
    an explicit `AddRequiredScopeAuthorization()` call. That is a version-coupled
    behaviour of Microsoft.Identity.Web 4.14.2; the two
    `..._TokenWithoutScopeClaim_Returns403` tests are the regression guard, and a
    future MIW/.NET upgrade that flips them from 403 to 200 would be a silent
    authorization downgrade. Worth one line in memory so the next upgrader knows
    why those tests matter.
12. **Live verification is still outstanding and correctly declared.** Nothing in
    §16 is proven against a real tenant: token issuance, the issuer/authority host
    mismatch (A5), `scp` presence (A8), `oid` stability (A2), Google attribute
    collection (A3), `domain_hint` routing (A4), admin consent, Key Vault RBAC,
    and the bootstrap `UPDATE`. `tests.md` §5 lists exactly this set rather than
    faking it, and every unverified assumption degrades to a fail-closed
    401/403/503 or a null profile field — never to an open door. This cycle is
    **not** end-to-end verified until §16.17's acceptance criteria have been run
    against the live deployment and the result recorded.
