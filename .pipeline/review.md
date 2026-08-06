# Review — Entra-ONLY Postgres auth for the application (specs §14)

Reviewed: 2026-08-06. Branch `pipeline/entra-passwordless-connection-string`.
Scope of judgement, per the task framing: is what is on this branch — the
already-merged app code (`0266239`, merged to `main` as `ef444c6` via PR #8) plus
the **uncommitted** devops changes to `infra/main.bicep` and `infra/README.md` —
internally consistent and safe to commit + push + PR. Not "is it deployed".

## VERDICT: APPROVED

---

## Rationale

Everything below was verified against the actual artifacts, not against the
agents' self-reports.

### 1. The application genuinely has zero password-auth code path (§14.3 / §14.4)

`backend/src/TodoApi/Data/TodoDbContextRegistration.cs:60-87` —
`AddTodoDbContext` has a single, unconditional path: it registers one lazy
`NpgsqlDataSource` singleton built by `BuildEntraAuthenticatedDataSource` and
one `TodoDbContext` over it. There is no `if`, no `UseEntraAuthKey`, no
`options.UseNpgsql(connectionString)` fallback branch. The blank-connection-string
guard and its message are preserved verbatim (lines 67-73), as §14.5 requires.

Repo-wide grep confirms it: the only surviving `UseEntraAuth` strings anywhere
in shipped code/config are (a) three doc comments that document the flag's
*removal*, (b) the dead-flag regression test
`AddTodoDbContext_LegacyUseEntraAuthFlag_HasNoEffect`, and (c) `infra/README.md`
lines 390/400, which are the `--remove-env-vars` cutover command and its
verification. `infra/main.bicep:215` mentions it only to say it no longer exists.
No live reference remains.

No `Password=` / `Passfile=` / `POSTGRES_PASSWORD` / `todoadmin` literal exists in
any application config: `appsettings.json` (the `Postgres` section is fully gone),
`appsettings.Development.json`, `TodoDbContextFactory.cs`, `docker-compose.yml`
(`POSTGRES_HOST_AUTH_METHOD: trust`, no password), `.env.example`, and the two
test-support files. The only `Password=` in the tracked tree outside `.pipeline/`
is `infra/README.md:427`, which is the D6 recovery command interpolating an
interactively-read `$PGPWD` shell variable — not a committed credential.

The normalizer (`BuildEntraConnectionString`, lines 159-204) implements §14.5
steps 1-6 in the mandated order, uses `NpgsqlConnectionStringBuilder` rather than
hand-rolled parsing, is `internal static` and genuinely pure (no I/O, no ambient
state), and returns a record rather than an `out` param — a shape §14.5 explicitly
left to the engineer. `UsePasswordProvider` is used (line 130), not
`UsePeriodicPasswordProvider`; `EntraTokenPasswordProvider.cs` is untouched, as
§14.12 requires (absent from `git diff bb8786d..0266239 --stat`).

### 2. The Postgres server still accepts BOTH auth methods (§14.8 item 2, §14.12)

`infra/modules/postgres.bicep:73-77` still reads
`activeDirectoryAuth: 'Enabled'` **and** `passwordAuth: 'Enabled'`, and
`administratorLogin` / `administratorLoginPassword` (lines 13, 17, 68-69) are
intact. `git diff --stat -- infra/modules/` is **empty** — devops did not touch the
module at all. `main.parameters.json` and the `@secure() postgresAdminPassword`
parameter are likewise unchanged.

I confirmed this against the *live* server too, not just the template: a read-only
`az deployment group what-if -g rg-todo-demo -f infra/main.bicep` shows the
`pg-todo-demo-cus01` delta containing only Azure-side default noise
(`dataEncryption`, `replica`, `storage.iops/tier/type`, `replicationRole`) — **no
`authConfig` line at all**. Password `psql` access as `todoadmin` survives, exactly
as the user required.

### 3. `infra/main.bicep`'s deletion is clean

The diff is three hunks: delete the four-line `Postgres__UseEntraAuth` env-var
object from `todoApi.envVars`, and rewrite two comment blocks. No value, resource,
parameter, SKU or module-wiring change. I read the whole `todoApi`/`todoWeb`
section (lines 160-250) — the remaining `envVars` array is well-formed with three
entries (`Cors__AllowedOrigins__0`, `ConnectionStrings__TodoDb`,
`APPLICATIONINSIGHTS_CONNECTION_STRING`), and nothing else referenced the deleted
variable.

I ran the validation myself rather than trusting `infra.md`:
- `az bicep build --file infra/main.bicep` → **exit 0**, no errors, no warnings.
- `az bicep lint --file infra/main.bicep` → **exit 0**, clean.
- `az deployment group what-if` (read-only, live RG) → the `todo-api` delta is
  exactly what `infra.md` claims:
  `Delete .env.1.secretRef = todo-db-connection`,
  `Create .env.1.value = Host=...;Username=todo-api;Ssl Mode=Require;...`,
  `Modify .env.2.name : Postgres__UseEntraAuth -> APPLICATIONINSIGHTS_CONNECTION_STRING`,
  `Delete .env.3`, `Delete properties.configuration.secrets = [todo-db-connection]`.
  `infra.md`'s self-report is accurate, including its honest disclosure that a full
  apply would also revert the image to `mcr.microsoft.com/k8se/quickstart:latest`.

### 4. The Phase 4 runbook is internally consistent and honest about the reduced rollback safety

The user's non-default choice (delete `todo-db-connection` **during** the cutover,
overriding both `infra-proposal.md` §3a and spec §14.9 step 6 / §14.13 Q1) is
**not glossed over**. It is disclosed in three places, each naming the consequence
plainly rather than burying it:

- `.pipeline/infra.md` decision table marks it **OVERRIDE** against the proposal's
  own recommendation, and adds a dedicated "Consequence of decision 1, stated
  plainly" section.
- `infra/README.md:352-362` — a ⚠️ callout at the head of step D: *"there is no
  revision-rollback safety net… the previous revision's `secretRef` dangles and
  `az containerapp revision activate "$PREV_REV"` will not restore a working app on
  its own"*, plus the instruction to run D3/D4/D5 in one maintenance window.
- `infra/README.md:217` and `:412-437` — the CUTOVER SEQUENCING preamble and D6
  recovery path, which correctly state that fix-forward is preferred, and that the
  secret must be re-created **before** `revision activate` can help, and that this
  only works because the server keeps password auth *and* the target image must be
  a pre-§14 one.

Ordering is correct throughout: D3a (env flip) precedes D3b (secret removal),
with the reason given (ACA refuses to remove a secret a live `secretRef` still
references). D1 (record `PREV_REV`) precedes D2 (confirm the new image). Step B is
correctly converted to verify-first `pgaadauth_list_principals(false)` with
`pgaadauth_create_principal_with_oid` as a conditional repair, and carries the
§14.7 "do NOT add the MI to `flexibleServers/administrators`" warning and the "oid
mismatch" diagnostic. The stale spots §14.8(3) enumerated (Files-table "secret",
the `Postgres__UseEntraAuth=true` step, the "DROPPED at cutover" wording, the
"First time applying" callout) are all corrected, and the "`passwordAuth: Enabled`
is KEPT ON PURPOSE — do not 'helpfully' disable" note is present.

### 5. Tests actually cover the spec, and I re-ran them

`dotnet test -c Release` → **Passed! Failed: 0, Passed: 81, Skipped: 0** —
independently reproduced, matching `tests.md`. I read
`TodoDbContextRegistrationTests.cs` rather than trusting the coverage table: all 15
numbered §14.10 items map to real, falsifiable assertions. Notably the normalizer
tests re-parse into a fresh `NpgsqlConnectionStringBuilder` instead of
string-matching raw output (§14.10's explicit instruction), item 1 uses the
verbatim production-shaped string from §14.1(a), and item 13 is a `[Theory]` over
both `"true"` and `"false"` — proving the flag is dead rather than merely defaulted.
`BuildEntraAuthenticatedDataSource_LogsPrincipalAndWarnings_WithoutLeakingSecrets`
is a genuine assertion: it asserts on rendered log text and on the *count* of
warnings (2), with a companion test proving the warning branches are conditional
(`CleanConnectionString_LogsInformationOnly` asserts exactly one entry). The
rewrite of the pre-existing password-branch test was necessary, not a smell —
its central assertion encoded the behaviour §14 deletes — and `tests.md` states
that reason as §14.10 requires. The tester correctly refused to fake the four
items that need live Azure/Docker and said so.

### 6. Security / PCI DSS

**PCI DSS is not in scope** and this cycle does not move it into scope: the data
model is `todos(id, title, description, is_completed, created_at, updated_at)`.
No PAN, CVV, track data, payment processor, or billing field exists anywhere in
the repo. Nothing to tokenize; no hosted-fields question arises.

No credential-handling or credential-logging regression was introduced — the
posture is strictly better:
- The token is obtained inside `EntraTokenPasswordProvider.GetPassword/Async`
  and returned straight to Npgsql. It is never stored in a field, never written to
  config, never placed in the connection string, and never passed as a log format
  argument. I checked every `logger.Log*` call site in
  `TodoDbContextRegistration.cs:218-239` — the only substituted arguments are
  `Host`, `Database`, `Username`, and the *previous* `SslMode` enum.
- `Program.cs:64`'s `logger.LogError(ex, "Failed to apply database migrations…")`
  is the one place a driver exception reaches a log. Because the connection string
  is now passwordless by construction, even an exception that echoed it would leak
  nothing — which is a net improvement over the pre-§14 state.
- TLS is now *forced* to `Require` for non-loopback hosts configured
  `Disable`/`Allow`, specifically because the token rides in the cleartext-password
  field (`TodoDbContextRegistration.cs:189-195`).
- No secret in IaC or YAML: `main.parameters.json` carries only non-secret values;
  `postgresAdminPassword` remains `@secure()` and is supplied from the
  `PGADMIN_PASSWORD` GitHub secret at deploy time; the `todo-db-connection` ACA
  secret is deleted, leaving `todo-api` with **zero** Container Apps secrets.
- No injection surface change (EF Core parameterized queries only, no raw SQL),
  no auth checks to miss (the app is intentionally anonymous, §7/§8).

SOLID spot-check on the two security-relevant classes: `EntraTokenPasswordProvider`
is a single-responsibility, constructor-validated, sealed class whose only
dependency is the `TokenCredential` **abstraction** (DIP) — which is precisely what
makes the token seam testable without Azure. `TodoDbContextRegistration` separates
registration, a pure normalizer, and a null-safe logging helper. `changes.md`'s
design claims are backed by the diff.

### 7. CI/CD triggers

Exactly one system auto-triggers. `.github/workflows/ci.yml` → `pull_request:
branches: [main]` + `workflow_dispatch`, credential-free, running the real commands
(`dotnet restore/build/test TodoApi.sln -c Release`, `npm ci`/`npm run build`,
docker build of both images, `az bicep build`/`lint`).
`.github/workflows/cd.yml` → `push: branches: [main]` + `workflow_dispatch`, OIDC,
`concurrency: cd-main`. `azure-pipelines.yml` → `trigger: none` / `pr: none`, with
an inline comment explaining why. Neither pipeline writes
`ConnectionStrings__TodoDb` or `Postgres__UseEntraAuth`, so a CD run after the
cutover will not revert it — I confirmed this by grep, matching `infra.md`'s claim.
§14.8 forbids CD changes this cycle and none were made.

---

## Blocking issues

**None.**

---

## Non-blocking suggestions

1. **(Highest value, and the answer to the review question about branch
   protection: yes, surface it — but do not block on it.)** `main` has **no branch
   protection and no rulesets** (`infra.md` verified this read-only:
   `"protected": false`, `rulesets → []`). Combined with `cd.yml`'s `push: [main]`
   trigger and a GitHub `production` Environment that has no required-reviewer rule
   configured, the net effect is that **any direct push to `main` deploys to
   production with zero human review** — the "human reviews and merges the PR" gate
   this entire pipeline is built around is a convention, not an enforced control.
   `infra.md` is right that this is the single highest-value unclosed item in the
   repo and that it costs $0 and ~60 seconds. It is explicitly out of §14's scope
   (§14.8 forbids CD/repo-config changes this cycle) and is a repo-admin action no
   agent may take, so it is correctly not a blocker here — but it should be the
   very next thing the user does, ideally before the cutover. The `gh api` one-liner
   in `infra.md` and the UI steps in `infra/README.md:449` are both ready to use.
   Adding a required reviewer on the `production` Environment would additionally
   give the deploy job the approval gate it currently lacks.

2. **Harden `infra/README.md` step D1's `PREV_REV` capture.** D1 records "the
   currently active revision", which is only the correct rollback target while the
   pre-§14 image is still running. That happens to be true right now — the live
   what-if shows `todo-api` on
   `acrtododemo3bkqjv23abtua.azurecr.io/todo-api:bb8786da…` — but PR #8 is already
   merged, so the next CD run will roll the Entra-only image and D1 would then
   silently capture a revision that *cannot* serve as the D6 fallback. D6 does warn
   that the rollback target "really means a pre-§14 image", so the operator is not
   left blind; the fix is just to make D1 mechanical rather than advisory, e.g.
   list revisions with their image tags and pin `PREV_REV` to the last revision
   whose tag predates the §14 merge. Worth a one-paragraph edit before a human runs
   the cutover, given decision 1 removed the safety net.

3. **Deviation recorded for traceability, not objection:** deleting
   `todo-db-connection` inside the cutover contradicts spec §14.9 step 6 and
   §14.13 Q1 ("never in the same change as the cutover"). This is a direct user
   decision that overrides the spec and the devops agent's own recommendation, and
   it is disclosed as an OVERRIDE in `infra.md` and as an "Approved deviation from
   specs §14.9" in the README. Accepted. If the spec is ever re-read as
   authoritative, §14.9 step 6 should be amended so the two documents stop
   disagreeing.

4. **`Ssl Mode=Prefer` (and omitted `Ssl Mode`) on a remote host is left alone.**
   This is the literal §14.5 step-4 rule and `changes.md` assumption 2 flags it
   honestly. `Prefer` silently falls back to plaintext if the server refuses TLS,
   which would expose the token — unreachable today because
   `infra/main.bicep:177` always emits an explicit `Ssl Mode=Require`. Worth
   tightening to "force `Require` unless the configured mode is at least `Require`"
   in a future cycle, as pure defence-in-depth.

5. **`Trust Server Certificate=true`** encrypts but skips certificate
   verification (MITM-susceptible). Deferred by the user this cycle and correctly
   *not* bundled into a cutover that has no fallback; it is recorded as a follow-up
   in both `infra.md` and README Phase 4. Give it its own cycle with its own live
   verification.

6. **Minor test gap** (tester already self-reported it): the bracketed IPv6
   loopback form `[::1]` is handled by `IsLoopbackHost`'s `Trim('[', ']')`
   (`TodoDbContextRegistration.cs:255`) but has no dedicated case in the loopback
   `[Theory]`. One `[InlineData("[::1]")]` closes it.

7. **`.pipeline/deployment-lessons-learned.md` §5a is still UNRESOLVED and is not
   on this branch** (it lives on `docs/deployment-lessons-learned`, commit
   `011da3d`). §14.9 requires the cutover outcome to be written back into it. Both
   `infra.md` and README D flag this; whoever runs the cutover needs to locate the
   file first or the close-out will be lost. Consider merging that doc branch so
   the record lives in one place.

8. **Process observation, no action needed on this branch.** The app code was
   merged to `main` (PR #8, `ef444c6`) after the tester's PASS but before this
   review ran, so the review gate was applied after the fact for that half of the
   work. The code turned out sound, so no harm resulted — but this is exactly the
   failure mode item 1 above describes, and enabling branch protection with a
   required approval is what prevents it from recurring.
