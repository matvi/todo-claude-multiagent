# Test Report — End-user authentication with Microsoft Entra External ID (specs §16)

## Verdict: PASS

Branch: `pipeline/entra-external-id-auth`. This report supersedes all prior
`.pipeline/tests.md` cycles (overwritten each cycle, per process). Scope:
`.pipeline/specs.md` §16 (this cycle only) as implemented in the
"Implementation Changes — End-user authentication with Microsoft Entra
External ID (2026-08-12)" entry of `.pipeline/changes.md`. §1–§14 are
prior/settled and were not re-tested, except for confirming their existing
test suites still pass unmodified.

---

## Headline result: the engineer's flagged risk is fixed

The engineer reported **34 of 81 backend tests failing** because
`WebApplicationFactory`-based tests now boot an app that (a) requires the five
`AzureAd:*` settings and (b) authenticates every non-`/health` endpoint. I
extended the test infrastructure (`TodoApiFactory` and the sibling
`ObservabilityTestFactory` in `ObservabilityTests.cs`) with a test
authentication scheme and valid-shaped `AzureAd:*` configuration.

```
cd backend
dotnet build TodoApi.sln -c Release   → Build succeeded, 0 Warning(s), 0 Error(s)
dotnet test  TodoApi.sln -c Release   → Passed! Failed: 0, Passed: 164, Skipped: 0, Total: 164
```

**164/164 backend tests pass** (the pre-existing 81 + 83 new), run 5 times
consecutively with identical results (no flakiness).

```
cd frontend
npm run build   → tsc + vite build succeed, 0 errors
npm test        → 12 files, 60/60 tests pass
```

**60/60 frontend tests pass** (the pre-existing 37 + 23 new), run twice
consecutively with identical results.

---

## 1. Why the tests were failing, and the fix (specs §16.18 item 22)

Root cause, confirmed by reproducing it: `Program.cs`'s top-level statements
call `builder.Services.AddTodoAuthentication(builder.Configuration)`
**synchronously, before `WebApplicationBuilder.Build()` is ever called**.
`WebApplicationFactory`'s `ConfigureWebHost` → `ConfigureAppConfiguration`
hook, by contrast, is only applied at the point the test host's deferred
builder intercepts that `Build()` call — i.e. **after** `AddTodoAuthentication`
has already thrown. I verified this empirically: adding the five `AzureAd:*`
keys via `ConfigureAppConfiguration` (the "obvious" fix) reproduced the exact
same "Entra External ID authentication is not configured" failure on every
test, including ones that never call `/api/*`, because the whole host fails
to boot.

**Fix:** `TestAzureAdEnvironment.cs` (new) sets the five `AzureAd__*` values as
real **process environment variables** from each factory's constructor —
which runs before `StartServer()`/`CreateHost()` is ever triggered — since
`WebApplication.CreateBuilder(args)` reads environment variables as part of
its own default configuration sources from the first line of `Program.cs`.
Safe to set once, process-wide, because every host in the assembly wants the
same default shape and the assembly already disables test parallelization
(`[assembly: CollectionBehavior(DisableTestParallelization = true)]`, already
present in `ObservabilityTests.cs`).

**Test scheme:** `TestAuthHandler.cs` (new) is an `AuthenticationHandler`
registered as scheme `"Test"` and made the **default** authentication scheme
in `TodoApiFactory`/`ObservabilityTestFactory`'s `ConfigureServices`. It reads
claims encoded (base64 JSON) in the `Authorization: Bearer <value>` header via
the `TestToken` fluent builder (`TestToken.cs`, new). Crucially, **the real
`Microsoft.Identity.Web`/`JwtBearer` "Bearer" scheme registration from
`Program.cs` is left completely intact** — only the *default* scheme a bare
`[Authorize]` resolves to changes. This is verified directly by
`AuthenticationEndpointTests.RealJwtBearerScheme_IsStillRegistered_TestSchemeIsOnlyTheDefault`,
which resolves `IAuthenticationSchemeProvider` from the test host's DI
container and asserts the `Bearer` scheme's handler type is still
`JwtBearerHandler`.

`TodoApiFactory.ConfigureClient` attaches a default **valid** test token to
every client the factory creates, so the entire pre-existing
`TodosControllerTests.cs` (29 tests) keeps passing **completely unmodified**
— exactly the "test host supplies an authenticated principal" wording of
spec item 21. Dedicated new tests override or clear that header via
`CreateAuthenticatedClient` / `CreateUnauthenticatedClient` /
`CreateClientWithRawBearerToken` to exercise unauthenticated / wrong-scope /
wrong-tenant / broken-token paths.

`TodoApiFactory(simulateRoleSourceUnavailable: true)` swaps
`IUserProvisioningService` for a throwing stub, so the **real**
`RoleClaimsTransformation` (unmodified production code) genuinely fails
closed through the real middleware pipeline, not a simulation of the outcome.

None of this touches `backend/src/TodoApi/` application code — only
`backend/tests/TodoApi.Tests/TodoApiFactory.cs` and
`backend/tests/TodoApi.Tests/ObservabilityTests.cs` (both existing test
infrastructure, extended per the explicit sanction in spec item 22), plus new
test-only files.

---

## 2. Coverage vs. specs §16.18 (the tester's own checklist) — item by item

| # | Requirement | Test(s) | Result |
|---|---|---|---|
| 1 | `GET /api/todos` no `Authorization` ⇒ 401 | `AuthenticationEndpointTests.GetTodos_NoAuthorizationHeader_Returns401`, `.GetUsersMe_NoAuthorizationHeader_Returns401`, `.PostTodos_NoAuthorizationHeader_Returns401` | PASS |
| 2 | `GET /health` no header ⇒ 200 `{"status":"ok"}` | `.Health_NoAuthorizationHeader_Returns200` | PASS |
| 3 | Test-key token accepted only via the swapped test scheme; production config rejects unsigned/`alg=none` | `TokenValidationHardeningTests.ProductionValidationConfiguration_RejectsAnUnsignedAlgNoneToken` (rejects a real unsigned JWT via the actual hardened `TokenValidationParameters`) + `AuthenticationEndpointTests.RealJwtBearerScheme_IsStillRegistered_TestSchemeIsOnlyTheDefault` (proves swap, not removal) | PASS |
| 4 | Startup fails fast naming the missing key (`TenantId`/`Instance`/`ClientId`/`Audience`) | `EntraAuthOptionsTests` — 5×`AnyOneOfTheFiveRequiredKeysBlank`, 5×`...MissingEntirely`, `AllFiveKeysMissing_NamesEveryOneInTheSameException` | PASS |
| 5 | `RequireHttpsMetadata`/`ValidateIssuer`/`ValidateAudience`/`ValidateLifetime`/`ValidateIssuerSigningKey` all `true` after `AddMicrosoftIdentityWebApi` binding | `TokenValidationHardeningTests.AddTodoAuthentication_ConfiguresJwtBearerOptions_WithAllValidationFlagsTrue_ViaRealDiRegistration` — resolves the **real** DI-registered `JwtBearerOptions` via the production `AddTodoAuthentication` entry point, not a hand-built options object | PASS |
| 6 | Unknown `oid` ⇒ row created, role `User` | `RoleClaimsTransformationTests.TransformAsync_UnknownOid_CreatesRowWithUserRole_AndPrincipalHasUserRole` + `UserProvisioningServiceTests.GetOrCreateAsync_UnknownOid_CreatesRowWithUserRole` (real service, EF InMemory) | PASS |
| 7 | Known `oid`, `role=Admin` ⇒ principal Admin, no insert | `.TransformAsync_KnownOidWithAdminRole_PrincipalHasAdminRole_AndNoNewRowIsCreated` | PASS |
| 8 | Anti-escalation: inbound `roles=Admin` + DB `User` ⇒ result `User`, exactly one role claim | `.TransformAsync_InboundRolesClaimClaimsAdmin_ButDbRowIsUser_ResultIsUser_WithExactlyOneRoleClaim` | PASS |
| 9 | `tid` ≠ configured tenant ⇒ no role claim, rejected | `.TransformAsync_WrongTenant_RejectsAsInvalidPrincipal_NoRoleClaim_NoProvisioningCall`, `.TransformAsync_MissingTenantClaim_...` + end-to-end `AuthenticationEndpointTests.GetTodos_TokenFromWrongTenant_Returns401_WithWwwAuthenticateHeader` | PASS |
| 10 | Missing/unparseable `oid` ⇒ rejected, never falls back to `sub`/`email`, no row created | `.TransformAsync_MissingOid_RejectsAsInvalidPrincipal_NeverFallsBackToSubOrEmail`, `.TransformAsync_UnparseableOid_...` + `AuthenticationEndpointTests.GetTodos_TokenMissingOid_Returns401` | PASS |
| 11 | Idempotence: 2nd `TransformAsync` call ⇒ one DB lookup, one role claim | `.TransformAsync_CalledTwiceOnSamePrincipal_PerformsExactlyOneDbLookup_AndYieldsOneRoleClaim`, `.TransformAsync_CalledTwiceOnARejectedPrincipal_...` | PASS |
| 12 | Concurrent first requests for the same `oid` ⇒ exactly one row, re-read not thrown | `UserProvisioningServiceTests.GetOrCreateAsync_TwoConcurrentFirstRequestsForTheSameOid_ProduceExactlyOneRow` — genuine `Task.WhenAll` race of two independent `DbContext`s over the same EF Core InMemory store; the unique index is enforced and the losing insert's catch-and-reread path (real `UserProvisioningService` code) executes | PASS (stable across 5 repeated runs) |
| 13 | DB failure ⇒ fail closed, never defaults to User/Admin | `RoleClaimsTransformationTests.TransformAsync_ProvisioningThrows_FailsClosed_NoRoleClaim_NeverDefaultsToUserOrAdmin` + end-to-end `AuthenticationEndpointTests.RoleSourceUnavailable_ViaThrowingProvisioningService_Returns503` (real transformation, real middleware, thrown from a real DI-swapped provisioning service) | PASS |
| 14 | `GET /api/users` as `User` ⇒ 403; as `Admin` ⇒ 200 | `UsersControllerAuthorizationTests.GetUsers_AsUser_Returns403`, `.GetUsers_AsAdmin_Returns200` | PASS |
| 15 | `PATCH /api/users/{id}/role` as `User` ⇒ 403 | `.PatchRole_AsUser_Returns403` | PASS |
| 16 | Self-demotion ⇒ 409; last remaining Admin ⇒ 409 | `.PatchRole_SelfDemotion_Returns409` (+ verifies the row is still Admin afterward). See **Note A** below on the distinct-target "last admin" branch. | PASS (see note) |
| 17 | Invalid role value ⇒ 400 | `.PatchRole_InvalidRoleValue_Returns400`, `.PatchRole_LowercaseRoleValue_Returns400_CaseSensitive`, `.PatchRole_UnknownTargetId_Returns404` | PASS |
| 18 | `PUT /api/users/me` mass-assignment guard | `.PutMe_BodyContainingRoleEmailAndEntraObjectId_IgnoresThoseFields`, `.PutMe_SentByAUser_CannotSelfPromoteToAdmin_VerifiedViaSubsequentAdminCheck` | PASS |
| 19 | `GET /api/users/me` returns caller's own row, never another's | `.GetMe_ReturnsCallersOwnRow_NeverAnotherUsers`, `.GetMe_FirstEverSignIn_LazilyProvisionsRowWithUserRole` | PASS |
| 20 | Phone claim: configured name / suffix fallback / absent | `ProfileClaimsReaderTests` (5 tests) + `RoleClaimsTransformationTests.TransformAsync_PassesConfiguredPhoneClaimNameThroughToProvisioning` | PASS |
| 21 | Existing `TodosControllerTests` pass once authenticated | All 29 tests in the **unmodified** file pass via `TodoApiFactory.ConfigureClient`'s default valid token | PASS |
| 22 | `TodoApiFactory` extended with test scheme + stubbed role source (state the reason) | Reason stated above and in-code (`TodoApiFactory.cs`, `TestAuthHandler.cs`, `TestAzureAdEnvironment.cs` doc comments) | DONE |
| 23 | `TodoDbContextRegistrationTests`/`EntraTokenPasswordProviderTests` untouched, still pass | `git diff` shows **zero changes** to either file; both pass as part of the 164 | PASS |
| 24 | Unauthenticated state renders `<LoginScreen>` with both actions | `LoginScreen.test.tsx` — `item 24` | PASS |
| 25 | "Continue with Google" calls `loginRedirect` with `domain_hint === 'google.com'` | `LoginScreen.test.tsx` — `item 25` (+ a companion test proving the plain sign-in button sends **no** `domain_hint`) | PASS |
| 26 | `ssoSilent` success skips login screen; `InteractionRequiredAuthError` falls back without crashing | `AuthGate.test.tsx` — 4 tests (success path, `InteractionRequiredAuthError`, any other rejection, already-authenticated skip) | PASS |
| 27 | `api.ts` attaches `Authorization: Bearer <token>` when configured, none when not | `api.auth.test.ts` — 3 tests | PASS |
| 28 | 401 returns app to unauthenticated state (no retry loop) | `api.auth.test.ts` (`onUnauthorized` invoked exactly once on 401, never on other statuses) + `authSession.test.ts` (the pub/sub primitive) + `Root.test.tsx` (renders `<LoginScreen>` once the session is marked rejected) | PASS |
| 29 | Role gating: `User` hides admin link / `RequireAdmin` renders not-authorized **without** issuing admin API calls; `Admin` renders the admin view | `RequireAdmin.test.tsx` — proves via a mounting-probe child component that `User` never even **mounts** the protected subtree (not just visually hides it), `Admin` does, plus loading/error states | PASS |
| 30 | No/insufficient `scp` ⇒ 403 on **both** `/api/todos` and `/api/users/me` | `AuthenticationEndpointTests.GetTodos_TokenWithoutScopeClaim_Returns403`, `.GetUsersMe_TokenWithoutScopeClaim_Returns403`, `.GetTodos_TokenWithWrongScope_Returns403`, `.PostTodos_TokenWithoutScopeClaim_Returns403` + positive control `.GetTodos_TokenWithCorrectScope_Returns200` | PASS — **see the important note below** |
| 31 | Fail-fast naming `AzureAd:Scopes`; non-TLS `Instance`; non-GUID `TenantId` | `EntraAuthOptionsTests.FromConfiguration_ScopesBlank_IsPartOfTheFailFastList`, `.FromConfiguration_InstanceIsHttp_ThrowsBecauseTlsIsRequired`, `.FromConfiguration_TenantIdIsNotAGuid_Throws` | PASS |
| 32 | `invalid_principal` ⇒ 401 + `WWW-Authenticate: Bearer`, no claim echoed; `role_source_unavailable` ⇒ 503; both `ProblemDetails` | `AuthenticationEndpointTests.InvalidPrincipalMarker_Returns401_ProblemDetailsBody_NoClaimEchoed`, `.RoleSourceUnavailableMarker_Returns503_ProblemDetailsBody` | PASS |
| 33 | `/health` returns 200 even with a broken/failed token | `.Health_GarbageBearerToken_StillReturns200`, `.Health_TokenMarkedAsInvalidPrincipal_StillReturns200` | PASS |

### The item-30 finding, spelled out (this was the specific thing I was asked to prove, not assume)

`[RequiredScope(RequiredScopesConfigurationKey = "AzureAd:Scopes")]` is
present on both controllers, but **`AddRequiredScopeAuthorization()` (the
DI-registration extension Microsoft.Identity.Web ships for its
`ScopeAuthorizationHandler`) is never called anywhere in `Program.cs` or
`AuthenticationRegistration.cs`.** I reflected on the installed
`Microsoft.Identity.Web` 4.14.2 assembly and confirmed `RequiredScopeAttribute`
implements only a metadata marker interface (`IAuthRequiredScopeMetadata`) —
not `IAuthorizeData`, not an MVC filter — so it was not obvious from the code
alone that the attribute does anything without that extra call.

I did not stop at that finding. I wrote `AuthenticationEndpointTests` against
the **real, unmodified** `AddTodoAuthentication`/`AddTodoAuthorization`
registration (exactly what `Program.cs` calls) and sent real HTTP requests
with and without a `scp` claim:

- Token **without** `scp` → `/api/todos` = **403**, `/api/users/me` = **403**.
- Token with the **wrong** `scp` value → `/api/todos` = **403**.
- Token **with** the correct `scp` → `/api/todos` = **200** (positive control,
  proving the check discriminates rather than always-403ing for an unrelated
  reason).

**Conclusion: `[RequiredScope]` is genuinely enforced in this codebase as
shipped**, evidently through some automatic wiring inside
`AddMicrosoftIdentityWebApi`/the ASP.NET Core endpoint-metadata pipeline that
I was not able to fully trace via reflection alone, not through the
`AddRequiredScopeAuthorization()` extension. This is exercised, reproducible,
executable proof — not "the attribute is present so it must work." If a
future .NET/Microsoft.Identity.Web upgrade ever changes this,
`GetTodos_TokenWithoutScopeClaim_Returns403` /
`GetUsersMe_TokenWithoutScopeClaim_Returns403` will catch the regression
immediately (they would go from 403 to 200).

### Note A — the distinct-target "last remaining Admin" 409 branch appears unreachable (informational, not a defect)

`UsersController.UpdateRole` has two guards: (1) self-demotion → 409, checked
first; (2) "would leave zero Admins" → 409, checked second via
`COUNT(Admin) WHERE Id != target.Id`. I traced the logic and could not
construct an HTTP request that reaches guard (2) with a **different** person
than the actor as `target`: the `AdminOnly` policy guarantees the actor's own
row is `Admin` at the time of the request, and the actor's own `Id` always
differs from `target.Id` whenever `target` isn't the actor — so that `COUNT`
always includes at least the actor's own row (≥ 1), meaning it can never be
zero unless `target == actor`, which guard (1) already catches first. I wrote
`PatchRole_DemotingACoAdminWhenTwoAdminsExist_Succeeds_OneAdminRemains` to
document this boundary (demoting a co-admin when 2 admins exist correctly
**succeeds**, not 409). The outward guarantee spec §16.8/§16.17 requires
("never leave the system with zero Admins") **does hold** — it is fully
covered by the self-demotion guard in the only scenario where it's reachable
— so this is not a bug and not a test failure, just dead/defensive code worth
a look by the architect/engineer on a future pass (possibly reachable only
under true request-level concurrency, which I did not attempt to construct
here since it isn't required to prove the invariant holds).

---

## 3. Regression check — everything outside this cycle's scope

- **47 pre-existing tests from before this cycle, plus the 6 added in the
  `pipeline/entra-passwordless-connection-string` cycle (81 total)**: all
  still pass, unmodified, including `TodosControllerTests` (29),
  `TodoDbContextRegistrationTests` (28), `EntraTokenPasswordProviderTests`
  (9), `TelemetryRegistrationTests`, and `ObservabilityTests` (5, only its
  supporting factory was extended — see §1).
- **Frontend: all 37 pre-existing tests pass unmodified** (`App.test.tsx`,
  `api.test.ts`, `useTodos.test.ts`, `TodoForm/TodoList/TodoItem.test.tsx`) —
  confirms the engineer's claim that `App.tsx`/`useTodos.ts`/the todo
  components were untouched this cycle.
- `git diff --stat` / `git status` confirms **zero changes** to any file
  under `backend/src/TodoApi/` were made by this testing pass — only test
  files and `.pipeline/tests.md` were touched, per my constraints.

---

## 4. Files added/changed by this pass

**Backend — extended existing test infrastructure (sanctioned by spec item 22):**
- `backend/tests/TodoApi.Tests/TodoApiFactory.cs` — test auth scheme, valid
  `AzureAd:*` env vars via `TestAzureAdEnvironment`, `ConfigureClient` default
  token, `CreateAuthenticatedClient`/`CreateUnauthenticatedClient`/
  `CreateClientWithRawBearerToken`/`SeedUserAsync` helpers,
  `simulateRoleSourceUnavailable` constructor flag.
- `backend/tests/TodoApi.Tests/ObservabilityTests.cs` — same test-scheme +
  env-var wiring added to the sibling `ObservabilityTestFactory` so its
  `/api/todos` call (used by the trace-correlation test) still authenticates.

**Backend — new test-only files:**
- `TestAuthHandler.cs`, `TestToken.cs`, `TestAzureAdEnvironment.cs` — shared
  test authentication infrastructure.
- `EntraAuthOptionsTests.cs`, `TokenValidationHardeningTests.cs` — pure unit
  tests of startup fail-fast and token-validation hardening.
- `RoleClaimsTransformationTests.cs` — pure unit tests of the security-critical
  claims transformation, with a hand-rolled `FakeUserProvisioningService`
  (no mocking library is referenced in this project).
- `ProfileClaimsReaderTests.cs` — pure unit tests of claim extraction.
- `UserProvisioningServiceTests.cs` — tests the real service against EF Core
  InMemory, including the concurrency/unique-violation race.
- `AuthenticationEndpointTests.cs`, `UsersControllerAuthorizationTests.cs` —
  end-to-end `WebApplicationFactory` tests through the real, unmodified
  authentication/authorization pipeline.

**Frontend — new test-only files:**
- `src/auth/authSession.test.ts`, `src/api.auth.test.ts`,
  `src/auth/LoginScreen.test.tsx`, `src/auth/AuthGate.test.tsx`,
  `src/auth/RequireAdmin.test.tsx`, `src/Root.test.tsx`.

No application source file under `backend/src/TodoApi/` or `frontend/src/`
(other than the `*.test.ts(x)` files listed) was modified.

---

## 5. Gaps — not verifiable in this environment (per specs §16.18's own list)

Exactly as specs §16.18 anticipates, the following require a live tenant and
are **not** verified here; they remain open until the specs §16.11/§16.17
human-run bootstrap and live-verification steps are executed:

- Real tenant creation, real token issuance/validation against
  `*.ciamlogin.com`.
- Google federation, first-sign-up attribute collection (phone), and
  `domain_hint` actually bypassing the account picker (assumptions A3/A4).
- Admin consent, Key Vault RBAC, and the one-time bootstrap `UPDATE ... SET
  role='Admin'` SQL (specs §16.11 step 13).
- Whether the External ID issuer/authority host mismatch is accepted by
  `Microsoft.Identity.Web` out of the box against a **real** token
  (assumption A5) — the pure unit tests prove the `ValidIssuers` allow-list is
  populated correctly, but cannot fetch real OIDC metadata or validate a
  live-signed token end-to-end.
- Whether the optional claims (`email`, `given_name`, `family_name`, the
  phone extension) actually appear in a real **access** token, not just the
  ID token (assumption A6).
- Whether the External ID user flow issues `scp` as documented (assumption
  A8) — the app-side scope enforcement is proven (item 30 above); whether a
  real token from the real tenant actually carries the claim is a live-tenant
  question.
- Frontend build-arg wiring in CI/CD (`.github/workflows/*.yml`,
  `azure-pipelines.yml`) — explicitly devops-agent scope, not this cycle's or
  the tester's.

None of these gaps block a PASS verdict for this cycle: they are exactly the
set of things specs §16.18's own final paragraph says must be listed here
rather than faked, and each degrades safely per the implementation's
documented fail-closed behavior (verified above) rather than silently.

---

## 6. How to reproduce

```bash
# Backend
export PATH="/c/Users/ingda/dotnet10:$PATH"   # .NET 10 SDK, per prior-cycle env notes
cd backend
dotnet build TodoApi.sln -c Release
dotnet test  TodoApi.sln -c Release           # 164/164

# Frontend
cd frontend
npm ci
npm run build                                 # tsc + vite build
npm test                                       # 60/60 (vitest run)
```
