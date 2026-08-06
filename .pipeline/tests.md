# Test Report — Entra-ONLY Postgres auth for the application (specs §14)

## Verdict: PASS

Branch: `pipeline/entra-passwordless-connection-string`. This report supersedes
all prior `.pipeline/tests.md` cycles (per process, this file is overwritten
each cycle). Scope: `.pipeline/specs.md` §14 (the current cycle) as implemented
in the "Implementation Changes — Entra-ONLY Postgres auth for the application
(2026-08-06)" entry of `.pipeline/changes.md`.

---

## What was verified

### 1. Build & full existing suite

```
cd backend && dotnet build -c Release   → Build succeeded, 0 Warning(s), 0 Error(s)
dotnet test  -c Release                 → Passed!  Failed: 0, Passed: 81, Skipped: 0, Total: 81
```

All 81 tests pass, including the 9 pre-existing `EntraTokenPasswordProviderTests`
(unchanged per §14.12, confirmed unchanged in the diff) and the fully rewritten
`TodoDbContextRegistrationTests` (28 tests). The "Failed to apply database
migrations at startup" lines seen in the console log during `ObservabilityTests`
are the pre-existing, intentional, caught-and-logged behavior of `Migrate()`
against the EF Core InMemory provider (it doesn't support `Migrate()`); they are
not test failures — every test in that class reports Passed.

### 2. §14.10 test-by-test coverage check (all present, all passing)

I read `TodoDbContextRegistrationTests.cs` in full and cross-checked it against
the explicit numbered list in specs §14.10:

| Spec item | Test(s) present | Verdict |
|---|---|---|
| 1. Production password-shaped string does not throw | `BuildEntraAuthenticatedDataSource_WithProductionPasswordConnectionString_DoesNotThrow` | PASS — real regression test against the exact string from §14.1(a) |
| 2. Same via DI, `NpgsqlDataSource` + `TodoDbContext` resolve | `AddTodoDbContext_WithPasswordBearingConnectionString_ResolvesDataSourceAndContext` | PASS |
| 3-4. `Password=` / `pwd=` / `PASSWORD=` stripped | `BuildEntraConnectionString_StripsPasswordAndItsAliases` (3 cases) | PASS |
| 5. `Passfile=` stripped | `BuildEntraConnectionString_StripsPassfile` | PASS |
| 6. Missing `Username` → `InvalidOperationException` naming both `ConnectionStrings:TodoDb` and `Username` | `BuildEntraConnectionString_MissingUsername_Throws` (absent / empty / whitespace) | PASS |
| 7. `Host`/`Port`/`Database`/`Trust Server Certificate`/unrelated keyword preserved | `BuildEntraConnectionString_PreservesAllOtherKeywords` (also covers `Command Timeout`, `Maximum Pool Size`) | PASS |
| 8. Remote host + `Disable`/`Allow` → forced `Require` | `BuildEntraConnectionString_RemoteHostWithWeakSsl_ForcesRequire` | PASS |
| 9. Remote host + `Require`/`VerifyFull`/`VerifyCA`/`Prefer`/omitted → unchanged | `BuildEntraConnectionString_RemoteHostWithAdequateSsl_LeavesItAlone` + `..._RemoteHostWithOmittedSsl_LeavesTheNpgsqlDefault` | PASS |
| 10. Loopback exemption (`localhost`, `127.0.0.1`, also `LOCALHOST`, `::1`) | `BuildEntraConnectionString_LoopbackHostWithSslDisabled_StaysDisabled` | PASS |
| 11. Idempotence | `BuildEntraConnectionString_IsIdempotent` | PASS |
| 12. Exactly one `NpgsqlDataSource` singleton backs the `DbContext` | `AddTodoDbContext_RegistersExactlyOneDataSourceSingleton_BackingTheDbContext` | PASS |
| 13. `Postgres:UseEntraAuth=true`/`false` changes nothing (flag is dead) | `AddTodoDbContext_LegacyUseEntraAuthFlag_HasNoEffect` (Theory: both values) | PASS |
| 14. Blank/missing connection-string guard unchanged | `AddTodoDbContext_MissingConnectionString_ThrowsInvalidOperationException` / `_BlankConnectionString_...` | PASS |
| 15. `EntraTokenPasswordProviderTests` still pass, unchanged | confirmed — file untouched in the diff, 9/9 pass as part of the 81 | PASS |
| Additional: registration stays lazy (no credential/network work on mere registration) | `AddTodoDbContext_RegistrationIsLazy_NoDataSourceBuiltUntilResolved` | PASS |
| Additional: startup logging, no secret leakage | 3 tests, see §3 below | PASS |

Also present, not separately numbered in §14.10 but required by §14.5:
`BuildEntraConnectionString_UnparseableInput_ThrowsNamingTheConfigKey` (malformed
input wrapped in `InvalidOperationException` naming the config key).

### 3. Deep-dive: does the "no secret in logs" test actually assert anything real?

Per the task's specific instruction, I inspected
`BuildEntraAuthenticatedDataSource_LogsPrincipalAndWarnings_WithoutLeakingSecrets`
line by line, not just its name:

- It builds a data source from a connection string containing the literal
  password value `Password=REDACTED` (the placeholder token itself, used as a
  stand-in "secret value" for the test).
- `RecordingLogger` is a real `ILogger` implementation that calls the actual
  `Func<TState, Exception?, string> formatter` supplied by the structured-logging
  call sites (`logger.LogInformation("... Username={Username}", ...)` etc.) and
  stores the **fully rendered message string** — not the raw template, not a
  boolean flag. This means the assertion inspects what would actually reach a
  sink/exporter.
- The production code (`LogStartupDiagnostics`) only ever passes `Host`,
  `Database`, `Username`, and the *previous* `SslMode` enum value as format
  arguments — the password/passfile value is never one of the substituted
  arguments in any of the three possible log lines. I confirmed this by reading
  `TodoDbContextRegistration.cs` lines 218-239 directly: no code path threads
  `normalized.ConnectionString`, the raw input string, or any password-bearing
  value into a log call.
- `Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("REDACTED"))`
  therefore is not a tautology — it is checking the rendered output of real
  logging calls against a value that (a) genuinely appears in the input to the
  function under test and (b) would appear in the message if the production
  code were changed to (incorrectly) interpolate the connection string or the
  stripped password into a log line. I did **not** mutate
  `TodoDbContextRegistration.cs` to confirm this by fault-injection — as a
  tester I do not edit application source files, even temporarily, so this is a
  static-analysis conclusion (verified by reading every call site of `logger.Log*`
  in the file), not a runtime-proven mutation-kill. Given the code review found
  no path where a password-bearing value is ever passed as a format argument,
  the assertion is real (it exercises actual rendered log text against a value
  that would appear if the production code regressed), but its sensitivity to a
  hypothetical future regression is inferred from the source, not empirically
  demonstrated by breaking and un-breaking the code.
- Two companion tests further strengthen this: `..._CleanConnectionString_LogsInformationOnly`
  (exactly one log entry when nothing needed correcting — proves the two warning
  branches are genuinely conditional, not always firing) and
  `..._NullLogger_DoesNotThrow` (the null-safety contract required by §14.5, so
  tests/hosts without a logger don't crash).

**Conclusion: the no-leak test is a real, falsifiable assertion**, not
"it didn't throw."

### 4. Config-file / dead-reference audit (repo-wide grep)

Searched the whole repository (not just `backend/`) for `Postgres:UseEntraAuth`,
`Postgres__UseEntraAuth`, and `UseEntraAuthKey`:

- **Application code**: zero occurrences of `UseEntraAuthKey` and zero live
  branches on `Postgres:UseEntraAuth` remain in `backend/src/TodoApi/`. The only
  in-code mention is a rewritten test name/comment (`AddTodoDbContext_LegacyUseEntraAuthFlag_HasNoEffect`
  and its XML-doc paragraph) and a doc comment in
  `TodoDbContextRegistration.cs`'s class summary — both explicitly documenting
  that the flag is *gone*, not using it.
- **`infra/main.bicep`** still contains `Postgres__UseEntraAuth` (an env var on
  `todo-api`) and **`infra/README.md`** still documents it. Per this cycle's
  explicit scope statement (specs §14.1: *"infra/main.bicep env wiring... [is
  covered]"*, but §14.8 assigns the actual Bicep edit to *"the devops agent —
  author only, never apply"* as a distinct downstream step) and per the parent
  task's explicit instruction, **this is expected, documented drift, not a
  defect in this cycle's deliverable.** `changes.md`'s "Known limitations / TODOs"
  section already flags it as devops's outstanding item (§14.8 item 1). I did
  not flag it as a failure; noting it here only for completeness/traceability.
- **`appsettings.json`**: the `"Postgres": { "UseEntraAuth": false }` block is
  fully deleted (confirmed by reading the file — only `Logging`, `AllowedHosts`,
  `ConnectionStrings`, `Cors` remain).
- **`Program.cs`**: comment updated to "PostgreSQL DbContext. Entra /
  managed-identity auth only (specs §14)." — no residual "Uses password auth by
  default..." text.
- No `Password=` literal remains in any committed application config: checked
  `appsettings.Development.json`, `appsettings.json`, `docker-compose.yml`,
  `.env.example`, `TodoDbContextFactory.cs`, `TodoApiFactory.cs`,
  `ObservabilityTests.cs` — all passwordless, confirmed by direct read.

### 5. `BuildEntraConnectionString` edge cases — additional scrutiny beyond the unit tests

Independently reasoned through cases not explicitly enumerated in §14.10 to make
sure the implementation's behavior is actually correct, not just internally
self-consistent with its own tests:

- **`pwd=` alias stripping**: `NpgsqlConnectionStringBuilder.Password` setter is
  the canonical property backing both `Password` and `pwd` keywords in Npgsql's
  keyword table, so setting `.Password = null` clears whichever alias was used
  at parse time — confirmed via the passing `pwd=hunter2` theory case (test
  re-parses via a **fresh** `NpgsqlConnectionStringBuilder`, so it is asserting
  against the actual serialized output, not an in-memory object still holding
  the alias).
- **SslMode omitted on a remote host**: Npgsql's own default (`Prefer` in
  Npgsql 10) is left untouched per the spec's literal rule (only `Disable`/
  `Allow` are corrected) — this is a documented, reviewer-flagged risk in
  `changes.md` (assumption 2: `Prefer` silently downgrades to plaintext if the
  server refuses TLS) and is explicitly not something this cycle was asked to
  change; the checked-in Azure Bicep always sets `Ssl Mode=Require` explicitly,
  so it is unreachable in the deployed configuration. Correctly *not* silently
  "fixed" beyond the spec's instructions.
- **Case-insensitive loopback matching** (`LOCALHOST`) and the **bracketed
  `[::1]` form** are both exercised/handled — `LOCALHOST` via the Theory test,
  `[::1]` via the `IsLoopbackHost` implementation's explicit `Trim('[', ']')`
  (code-read, no test targets the bracketed form directly — noted as a minor gap
  below).
- **Non-loopback IPv4 addresses that are not `127.0.0.1`** (e.g. a private
  `10.x` address) are correctly *not* exempted (fail-safe direction, forces
  TLS) — confirmed by reading `LoopbackHosts` array (`localhost`, `127.0.0.1`,
  `::1` only, no wildcard/prefix matching).
- **Malformed input** (`Host=db;NotARealKeyword=1`) throws via
  `NpgsqlConnectionStringBuilder`'s constructor and is correctly re-wrapped —
  confirmed the test expects `InvalidOperationException` naming
  `ConnectionStrings:TodoDb`, matching §14.5 step 1's requirement.

### 6. Regression check — nothing else broken

- `git diff --stat` confirms only backend + local-dev + docs files changed; no
  frontend files touched (matches `changes.md`'s "No frontend code changed").
- Full existing controller/DTO/health/CORS test suite (the pre-§14 52 tests)
  still passes unchanged as part of the 81/81 total — no regression from this
  cycle's rewrite of `TodoDbContextRegistrationTests.cs`.

---

## Test results summary

| Suite | Passed | Failed | Skipped | Total |
|---|---|---|---|---|
| `TodoApi.Tests` (`dotnet test -c Release`) | 81 | 0 | 0 | 81 |

Build: `dotnet build -c Release` → 0 Warning(s), 0 Error(s).

---

## Gaps (explicitly not verifiable in this environment — matches §14.10's own scope limit)

These are the same items `changes.md` and specs §14.10 already call out as
requiring a live Azure environment or a human. I did not attempt to fake or
mock around them, per instructions:

1. **Real Entra token acquisition against Azure Postgres Flexible Server** — no
   live server in this environment; the `EntraTokenPasswordProvider` tests
   (unchanged, 9/9 passing) already cover the token-callback contract with a
   fake credential, which is the maximum verifiable without Azure.
2. **The Postgres AAD handshake / in-DB `pgaadauth` grants (§14.7)** — requires
   a live server connection as the Entra admin; explicitly a human step.
3. **`docker-compose.yml` `trust`-auth local container end-to-end behavior
   (§14.6)** — I did not have Docker available to independently re-run the
   engineer's empirical probe (Probes A/B in `changes.md`). The engineer's
   documented method (counting-provider probe showing 0 password-provider
   invocations under `trust`, plus a full `dotnet run` + CRUD round-trip) is
   methodologically sound and specific enough to be credible, but I could not
   independently reproduce it in this test pass. This is a **process gap**, not
   a **defect** — flagging for transparency, not as a blocker.
4. **The live cutover (§14.9)** — explicitly human-run, post-merge, against the
   real `todo-api` Container App. `.pipeline/deployment-lessons-learned.md` §5a
   (referenced by the spec as needing to be closed out) does not yet exist in
   this repo; that is expected, since the live cutover has not been run yet.
5. **`[::1]` bracketed-host loopback exemption** — implemented (`IsLoopbackHost`
   strips `[` `]` before comparing) but not covered by a dedicated unit test
   (only bare `::1` is tested). Low risk (same code path as `::1`), noting as a
   minor coverage gap rather than a failure — does not change the PASS verdict.

None of these gaps are testable without a live Azure subscription and are
correctly deferred to the human-run procedures in specs §14.6/§14.7/§14.9, as
the spec itself mandates.

---

## Coverage vs. specs.md §14 acceptance criteria

- §14.4 configuration contract (single `ConnectionStrings:TodoDb` key, all
  `Postgres:UseEntraAuth` references deleted from app code/config) — **verified**.
- §14.5 backend behavior (normalizer rules 1-6, startup logging, no dual mode) —
  **verified** by unit tests + direct code read.
- §14.6 local dev (passwordless `docker-compose.yml`/`appsettings.Development.json`/
  `.env.example`, documented `down -v` + `az login` guidance) — **verified** by
  direct file read; empirical Npgsql-provider-not-invoked-under-trust claim is
  the engineer's own documented probe, not independently reproduced here (gap
  #3 above).
- §14.10 testing requirements — **all 15 enumerated items present and passing**,
  plus additional logging/laziness coverage.
- §14.11 non-functional (breaking-change disclosure, TLS enforcement, no secret
  logging) — **verified**.
- §14.12 out-of-scope items (server auth unchanged, `todoadmin` preserved, no
  Key Vault, `EntraTokenPasswordProvider`/`UsePasswordProvider` untouched) —
  **verified**: `infra/modules/postgres.bicep` was not part of this diff (only
  `infra/main.bicep`, `main.parameters.json` mention the still-present
  `Postgres__UseEntraAuth` env var / admin password, which is expected drift
  per §14.8, not a code defect); `EntraTokenPasswordProvider.cs` is byte-for-byte
  absent from `git diff --stat`, confirming it was not touched.

---

## Commit / PR

Verdict is PASS. Proceeding to commit, push `pipeline/entra-passwordless-connection-string`,
and open a PR against `main` per instructions, noting the required manual
production cutover (§14.9) as a pre-merge-effective, not pre-merge, step.
