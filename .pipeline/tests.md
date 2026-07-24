# Test Report — Todo Application (Cycle 1)

## Verdict: FAIL

Reason: backend tests reveal a reproducible, spec-violating behavior (malformed
GUID path segments return `404` instead of the spec-mandated `400` on
`GET/PUT/DELETE /api/todos/{id}`). Everything else tested — full CRUD,
validation, ordering, CORS, health check, and the entire frontend — passes.
This is a single, well-isolated defect that the engineer should fix on the
next pass; it is not a fundamental design problem.

---

## What was tested and how

### Backend (`backend/tests/TodoApi.Tests`)

- Framework: xUnit + `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory<Program>`)
  driving the real ASP.NET Core pipeline (routing, `[ApiController]` model
  validation, CORS, controllers) over `HttpClient` — i.e. integration tests,
  not just calling controller methods directly.
- Database: **EF Core InMemory provider**, not Postgres. Docker Desktop's
  daemon is not running in this environment (confirmed below), so a real
  Postgres instance was not available; this matches the engineer's note in
  `changes.md`. A `TodoApiFactory : WebApplicationFactory<Program>` swaps out
  the app's Npgsql-backed `TodoDbContext` registration for a uniquely-named
  InMemory database per test instance, and injects a dummy (never-dialled)
  `ConnectionStrings:TodoDb` value purely so `Program.cs`'s "connection string
  must be configured" startup guard doesn't throw before the swap happens.
  Each test class instance gets a fresh factory/database (`IDisposable`
  pattern), so tests don't leak state into each other.
- Files added:
  - `backend/tests/TodoApi.Tests/TodoApiFactory.cs`
  - `backend/tests/TodoApi.Tests/TodosControllerTests.cs`
  - `backend/tests/TodoApi.Tests/TodoApi.Tests.csproj` — added
    `Microsoft.EntityFrameworkCore.InMemory` (10.0.0) package reference (the
    only change to the csproj; no application source files were touched).

Coverage against spec §3.5 (API surface) and §2 (data model):

| Requirement | Test(s) | Result |
|---|---|---|
| `GET /health` → 200 `{"status":"ok"}` | `Health_ReturnsOkWithStatusOk` | PASS |
| `POST /api/todos` valid → 201, `Location` header, body shape | `CreateTodo_WithValidBody_Returns201WithLocationAndBody` | PASS |
| Create without description → null, not error | `CreateTodo_WithoutDescription_SucceedsWithNullDescription` | PASS |
| Create with empty title → 400 | `CreateTodo_WithEmptyTitle_Returns400ValidationProblem` | PASS |
| Create with missing title → 400 | `CreateTodo_WithMissingTitle_Returns400ValidationProblem` | PASS |
| Create with title > 200 chars → 400 | `CreateTodo_WithTitleOver200Chars_Returns400` | PASS |
| Create with title exactly 200 chars → 201 (boundary) | `CreateTodo_WithTitleExactly200Chars_Succeeds` | PASS |
| Create with description > 2000 chars → 400 | `CreateTodo_WithDescriptionOver2000Chars_Returns400` | PASS |
| Malformed JSON body → 400 | `CreateTodo_WithMalformedJsonBody_Returns400` | PASS |
| Whitespace-only title → 400 (spec: "1–200 chars after trim") | `CreateTodo_WithWhitespaceOnlyTitle_Returns400` | PASS |
| `GET /api/todos` empty → `200 []` | `GetTodos_WhenEmpty_ReturnsEmptyArray` | PASS |
| `GET /api/todos` ordering newest-first | `GetTodos_ReturnsAllCreatedTodos_NewestFirst` | PASS |
| `GET /api/todos/{id}` existing → 200 | `GetTodo_WithExistingId_Returns200WithBody` | PASS |
| `GET /api/todos/{id}` unknown → 404 | `GetTodo_WithUnknownId_Returns404` | PASS |
| `GET /api/todos/{id}` malformed guid → **400 per spec** | `GetTodo_WithMalformedId_ShouldReturn400PerSpec` | **FAIL (actual: 404)** |
| `PUT /api/todos/{id}` valid → 200, persists | `UpdateTodo_WithExistingIdAndValidBody_Returns200AndPersists` | PASS |
| `PUT` toggling `isCompleted` persists | `UpdateTodo_TogglingIsCompleted_Persists` | PASS |
| `PUT` unknown id → 404 | `UpdateTodo_WithUnknownId_Returns404` | PASS |
| `PUT` empty title → 400 | `UpdateTodo_WithEmptyTitle_Returns400` | PASS |
| `PUT` missing `isCompleted` → 400 | `UpdateTodo_WithMissingIsCompleted_Returns400` | PASS |
| `PUT` title > 200 chars → 400 | `UpdateTodo_WithTitleOver200Chars_Returns400` | PASS |
| `PUT` malformed guid → **400 per spec** | `UpdateTodo_WithMalformedId_ShouldReturn400PerSpec` | **FAIL (actual: 404)** |
| `DELETE` existing → 204, then 404 on re-fetch | `DeleteTodo_WithExistingId_Returns204AndRemovesIt` | PASS |
| `DELETE` unknown id → 404 | `DeleteTodo_WithUnknownId_Returns404` | PASS |
| `DELETE` twice → second call 404 | `DeleteTodo_CalledTwice_SecondCallReturns404` | PASS |
| `DELETE` malformed guid → **400 per spec** | `DeleteTodo_WithMalformedId_ShouldReturn400PerSpec` | **FAIL (actual: 404)** |
| CORS preflight allows configured origin | `Cors_PreflightForAllowedOrigin_IsAccepted` | PASS |

**Backend result: 24 passed, 3 failed, 0 skipped, 27 total.**
Command: `dotnet test TodoApi.sln -c Release` (using the user-local .NET 10.0.302
SDK at `C:\Users\ingda\dotnet10`, since only .NET 6/8/9 are on the system
`PATH`/`Program Files` — same substitution the engineer documented).
`dotnet build TodoApi.sln -c Release` → 0 warnings, 0 errors (confirms the
engineer's build report is still accurate after adding the test project's
`Microsoft.EntityFrameworkCore.InMemory` reference).

### Frontend (`frontend/`)

No test tooling existed yet; it was added as part of this pass, consistent
with the existing Vite + React + TS stack:

- Added devDependencies: `vitest@2.1.9`, `@testing-library/react@16.3.2`,
  `@testing-library/jest-dom@6.9.1`, `@testing-library/user-event@14.6.1`,
  `jsdom@25.0.1`.
- `frontend/vite.config.ts` — added a `test` block (`environment: 'jsdom'`,
  `globals: true`, `setupFiles`).
- `frontend/src/test/setup.ts` — imports `@testing-library/jest-dom/vitest`.
- `frontend/package.json` — added `"test": "vitest run"` and
  `"test:watch": "vitest"` scripts.
- Test files added (all colocated with the code they test):
  - `frontend/src/api.test.ts` — `api.ts` fetch wrapper: GET/POST/PUT/DELETE
    call shape, JSON body parsing, `ApiError` on non-2xx, ASP.NET
    `ValidationProblemDetails.errors` message extraction, network-failure
    (`fetch` rejects) → `ApiError(status: 0)`.
  - `frontend/src/hooks/useTodos.test.ts` — initial load (success + failure),
    `create` (success prepends newest-first, failure sets `error` + rethrows),
    `update`, `toggleComplete` (flips `isCompleted`, keeps title/description),
    `remove` (success + failure), all against a mocked `api` module (mocking
    only the four request functions and keeping the real `ApiError` class, so
    `err instanceof Error` checks inside the hook still behave correctly).
  - `frontend/src/components/TodoForm.test.tsx` — trims title/description on
    submit, sends `description: null` when blank, disables submit for
    empty/whitespace-only title, no-op on submit with empty form.
  - `frontend/src/components/TodoList.test.tsx` — empty state, one `<li>` per
    todo, checkbox reflects `isCompleted`.
  - `frontend/src/components/TodoItem.test.tsx` — renders title/description/
    date; checkbox click → `onToggleComplete(todo)`; Delete click →
    `onRemove(id)`; Edit → change title → Save → `onUpdate(id, {...})` with
    trimmed title and unchanged `isCompleted`; Edit → Cancel discards changes;
    Save disabled when edited title is emptied.
  - `frontend/src/App.test.tsx` — end-to-end component-level flows against a
    mocked API client: loading state → rendered list; empty state; inline
    error banner on load failure; full create flow (form submit → API call →
    new item rendered); full toggle-complete flow; full edit flow; full
    delete flow (including keeping the item and showing the error banner when
    delete fails); multiple todos each rendered as their own list item.

**Frontend result: 37 passed, 0 failed, 6 test files.**
Command: `npm run test` (`vitest run`), run from `frontend/`.

`npm run build` (`tsc && vite build`) was re-run after adding the test
dependencies/config and still succeeds (0 type errors, bundle produced) —
confirms the new devDependencies didn't regress the production build.

---

## Bugs / spec mismatches found (not fixed — for the engineer)

1. **Malformed `{id}` returns 404, not 400, on all three id-based endpoints**
   (`GET/PUT/DELETE /api/todos/{id}`). Spec §3.5: *"`{id}` is a `Guid`;
   malformed ids return `400`."* Actual behavior, reproduced by three failing
   tests (`GetTodo_WithMalformedId_ShouldReturn400PerSpec`,
   `UpdateTodo_WithMalformedId_ShouldReturn400PerSpec`,
   `DeleteTodo_WithMalformedId_ShouldReturn400PerSpec`): `GET
   /api/todos/not-a-guid` → `404 Not Found`, not `400 Bad Request`.
   Root cause: the actions are declared with a route constraint
   (`[HttpGet("{id:guid}")]` etc.). When the URL segment fails the `:guid`
   constraint, ASP.NET Core's routing treats the request as "no endpoint
   matched" rather than invoking the action with a model-binding failure, so
   it falls through to a plain 404 instead of a 400 `ProblemDetails`. Typical
   fixes: drop the `:guid` route constraint and bind `id` as `string`,
   `Guid.TryParse` it manually, and return `BadRequest()` on failure; or add
   a fallback/exception-based translation. This is a real, easily
   reproducible defect, not a testing artifact — I confirmed it consistently
   across all three affected verbs.

2. **Not a bug (verified, documented for completeness):** a whitespace-only
   title (e.g. `"   "`) *does* correctly get rejected with 400, even though
   `CreateTodoRequest.Title` is trimmed only after `[Required]`/`[StringLength]`
   validation runs. This works because ASP.NET Core's `RequiredAttribute`
   internally trims string values before checking for emptiness (when
   `AllowEmptyStrings` is false, the default) — so the apparent "trim after
   validate" ordering in `TodosController.CreateTodo`/`UpdateTodo` does not
   actually let whitespace-only titles through. Confirmed via
   `CreateTodo_WithWhitespaceOnlyTitle_Returns400`, which passes.

3. **Test-infrastructure gotcha worth flagging** (not an app bug, but
   relevant if others write EF Core test doubles against this project):
   simply removing the `DbContextOptions<TodoDbContext>` service descriptor
   and re-calling `AddDbContext` with `UseInMemoryDatabase` is *not* enough
   with the EF Core 10 / current `AddDbContext` implementation — it still
   also registers other services keyed by `TodoDbContext` (e.g.
   `IDbContextOptionsConfiguration<TodoDbContext>`), and leaving those in
   place causes `"Services for database providers 'Npgsql...',
   'Microsoft.EntityFrameworkCore.InMemory' have been registered ... Only a
   single database provider can be registered"` at request time. The fix used
   in `TodoApiFactory.cs` removes *every* descriptor whose `ServiceType` is,
   or is generically parameterized with, `TodoDbContext` before re-adding the
   InMemory-backed registration. No production code needed to change for
   this — it's purely a test-harness detail — but worth knowing if the
   engineer or a future tester extends the test factory.

---

## Gaps (not testable in this environment)

- **No real Postgres / Azure DB for PostgreSQL integration test.** Docker
  Desktop's daemon is not running here (`docker version` succeeds for the
  CLI but `docker compose` / `docker version` server-side calls fail with
  `open //./pipe/dockerDesktopLinuxEngine: The system cannot find the file
  specified`), confirming the engineer's note in `changes.md`. This means:
  - The EF Core migration (`InitialCreate`) was **not** applied to a real
    Postgres instance and verified against actual `varchar(200)`/`varchar(2000)`
    column constraints, the `timestamptz` defaults, or the
    `ix_todos_created_at DESC` index — those were reviewed by reading the
    migration/`OnModelCreating` code, not exercised end-to-end.
  - `Npgsql`-specific behaviors (e.g. actual SSL/`Ssl Mode=Require` connection
    behavior, real DB-level `NOT NULL`/length enforcement) are unverified.
  - Recommend: once Docker Desktop is available, run `docker compose up -d`
    from the repo root, then `dotnet run` from `backend/src/TodoApi` and
    confirm `GET/POST/PUT/DELETE /api/todos` against the real Postgres
    instance, and inspect the generated schema with `\d todos` in `psql`.
- **No Azure deployment smoke test.** `scripts/deploy-azure.sh` was not run
  (per spec §5.7, a human runs it; no Azure resources were provisioned in
  this pass either). CORS-against-a-real-frontend-FQDN, Container Apps
  scale-to-zero cold start, and ACR pull/managed-identity behavior are
  therefore unverified — this matches the spec's explicit scope (manual `az`
  commands, not CI/CD).
- **Concurrency / race conditions** (e.g. two simultaneous `PUT`s to the same
  todo) are not tested — not called out as a requirement in specs.md.
- **Load/performance** was not tested — explicitly out of scope per specs.md
  §7 ("Do not over-invest").

---

## Environment notes

- .NET SDK: only 6.0.427 / 8.0.303 / 9.0.302 are on the default `PATH`
  (`C:\Program Files\dotnet`); a user-local **.NET SDK 10.0.302** exists at
  `C:\Users\ingda\dotnet10` (matches the engineer's note that they installed
  a user-local .NET 10 SDK) and was used for `dotnet build`/`dotnet test`
  after prepending it to `PATH`. Any downstream agent (or CI) needs this SDK
  discoverable the same way.
- Node: v22.18.0 / npm 10.9.3 — matches the frontend's `node:22-alpine`
  Dockerfile base image expectation.
- Docker Desktop CLI is installed (`docker version` client succeeds,
  `docker compose version` reports v2.31.0-desktop.2) but the Docker Desktop
  **engine/daemon was not running**, so no containers could be built or run,
  and Postgres could not be started. This corroborates `changes.md`'s stated
  limitation.

---

## Files added/modified by this test pass

### Backend (test project only — no application source files touched)
- `backend/tests/TodoApi.Tests/TodoApi.Tests.csproj` (added
  `Microsoft.EntityFrameworkCore.InMemory` package reference)
- `backend/tests/TodoApi.Tests/TodoApiFactory.cs` (new)
- `backend/tests/TodoApi.Tests/TodosControllerTests.cs` (new)

### Frontend
- `frontend/package.json` (added test devDependencies + `test`/`test:watch`
  scripts)
- `frontend/vite.config.ts` (added `test` config block)
- `frontend/src/test/setup.ts` (new)
- `frontend/src/api.test.ts` (new)
- `frontend/src/hooks/useTodos.test.ts` (new)
- `frontend/src/components/TodoForm.test.tsx` (new)
- `frontend/src/components/TodoList.test.tsx` (new)
- `frontend/src/components/TodoItem.test.tsx` (new)
- `frontend/src/App.test.tsx` (new)

---

## Summary for the next engineer pass

Fix the route-constraint/malformed-id handling on `TodosController` (GET, PUT,
DELETE by id) so malformed GUIDs return `400` instead of `404`, per specs.md
§3.5. Once that's fixed, re-run:

```bash
# backend
cd backend && dotnet test TodoApi.sln -c Release

# frontend
cd frontend && npm run test
```

Everything else (24 of 27 backend tests, all 37 frontend tests) already
passes and does not need further engineering changes for this cycle.

---

# Cycle 2 verification

## Verdict: PASS

The malformed-GUID defect from cycle 1 (spec §3.5) is fixed, verified by
reading the actual updated source (not just trusting `changes.md`), by
re-running the full existing backend + frontend suites, and by adding two new
hardening tests that also pass. No regressions found anywhere.

### 1. Code review of `TodosController.cs`

Read the file directly (not the engineer's diff summary). Confirmed for all
three id-based actions:

- `GetTodo` (line 34-52), `UpdateTodo` (line 81-109), `DeleteTodo`
  (line 112-133): route template is `[Http*("{id}")]` (no `:guid` constraint),
  parameter is `string id`, each starts with `Guid.TryParse(id, out var
  todoId)` and returns `MalformedId(id)` — a `BadRequest(new
  ValidationProblemDetails(ModelState))` with an `errors["id"]` entry — on
  parse failure, before doing any DB lookup. The well-formed-but-unknown-GUID
  path (`FindAsync` → `NotFound()`) is unchanged, so `404` for a valid-shape
  but nonexistent id still works as before.
- `CreateTodo`'s `CreatedAtAction(nameof(GetTodo), new { id = todo.Id }, ...)`
  is untouched; a `Guid` route value still serializes correctly into the
  unconstrained `{id}` template — verified this doesn't break by the passing
  `CreateTodo_WithValidBody_Returns201WithLocationAndBody` test (checks the
  `Location` header).
- Searched the whole `backend/src/TodoApi` tree for any other `:guid`,
  `HttpGet`/`HttpPut`/`HttpDelete`/`HttpPost`/`Map*` route declarations: the
  only other route is `MapGet("/health", ...)` in `Program.cs`, which has no
  id segment and is unaffected. No other id-based routes were missed — the
  fix is complete and correctly scoped.
- The `MalformedId` helper is a single, well-documented, DRY implementation
  shared by all three actions (no copy-paste drift).

Verdict on the code: fix is correct, complete, and matches the reviewer's
suggested approach exactly, including the `ValidationProblemDetails` shape
(reviewer's non-blocking suggestion #2, which the engineer also adopted).

### 2. Backend test suite re-run

Environment: prepended `C:\Users\ingda\dotnet10` to `PATH` and set
`DOTNET_ROOT` before invoking `dotnet`, as instructed (confirmed `dotnet
--version` reports `10.0.302`, not the system 9.0.302).

```
cd backend
dotnet test TodoApi.sln -c Release
```

Result (before adding new tests, to independently reproduce the engineer's
claim): **27 passed, 0 failed, 0 skipped, 27 total.**

- All three previously-failing tests now pass:
  `GetTodo_WithMalformedId_ShouldReturn400PerSpec`,
  `UpdateTodo_WithMalformedId_ShouldReturn400PerSpec`,
  `DeleteTodo_WithMalformedId_ShouldReturn400PerSpec`.
- The other 24 tests (full CRUD, validation boundaries, 404s, ordering, CORS,
  health) remain green — no regressions.

This independently confirms the engineer's reported "27 passed, 0 failed" —
actual pass count matches, not just trusted.

### 3. New hardening tests added

Added two tests to
`backend/tests/TodoApi.Tests/TodosControllerTests.cs`, immediately after
`GetTodo_WithMalformedId_ShouldReturn400PerSpec`, to close small gaps not
already covered (route-level edge cases beyond the simple `"not-a-guid"`
literal already tested for all three verbs):

- `GetTodo_WithWhitespaceOnlyId_Returns400` — requests
  `/api/todos/%20%20%20` (a URL-encoded whitespace-only segment, decodes to
  three spaces). Confirms whitespace doesn't get treated as an "empty"
  segment that routes elsewhere or 404s, and that `Guid.TryParse` correctly
  rejects it with `400`.
- `GetTodo_WithExtremelyLongGarbageId_Returns400` — requests
  `/api/todos/{2000-char string of 'a's}`. Confirms a pathological long
  garbage segment is rejected with a clean `400`, not a `500`/unhandled
  exception from routing or model binding.

Re-ran the full backend suite after adding these:

```
dotnet test TodoApi.sln -c Release
```

Result: **29 passed, 0 failed, 0 skipped, 29 total.** Both new tests pass;
no regressions from the addition.

### 4. Frontend regression check

Confirmed the engineer's claim that the frontend was untouched by re-running
the existing suite (no source changes were made to `frontend/` by this
verification pass):

```
cd frontend
npm run test
```

Result: **37 passed, 0 failed, 6 test files** — exactly matches the cycle 1
frontend result (`api.test.ts` 7, `TodoList.test.tsx` 3, `useTodos.test.ts`
8, `TodoForm.test.tsx` 4, `App.test.tsx` 9, `TodoItem.test.tsx` 6). No
regressions; frontend confirmed untouched and unaffected by the backend-only
fix.

### Coverage summary against specs.md §3.5

| Requirement | Covered by | Result |
|---|---|---|
| Malformed id → 400 on GET | `GetTodo_WithMalformedId_ShouldReturn400PerSpec` | PASS |
| Malformed id → 400 on PUT | `UpdateTodo_WithMalformedId_ShouldReturn400PerSpec` | PASS |
| Malformed id → 400 on DELETE | `DeleteTodo_WithMalformedId_ShouldReturn400PerSpec` | PASS |
| Malformed id (whitespace) → 400 | `GetTodo_WithWhitespaceOnlyId_Returns400` (new) | PASS |
| Malformed id (long garbage) → 400 | `GetTodo_WithExtremelyLongGarbageId_Returns400` (new) | PASS |
| Well-formed but unknown id → 404 (unaffected by fix) | `GetTodo_WithUnknownId_Returns404` et al. | PASS |
| Valid id CRUD paths (unaffected by fix) | full existing 200/201/204 suite | PASS |
| Frontend unaffected | full existing 37-test suite | PASS |

### Result totals

- Backend: **29/29 passed** (27 pre-existing + 2 new hardening tests).
- Frontend: **37/37 passed**, no regressions, confirmed untouched.

### Gaps (unchanged from cycle 1, not related to this fix)

- No real Postgres / Azure DB integration test (Docker Desktop engine not
  running in this environment) — same limitation as cycle 1, not related to
  this fix, and not re-verified here since it's out of scope for this
  targeted re-verification pass.
- No Azure deployment smoke test — out of scope per spec §5.7 (manual `az`
  commands, human-run).

### Overall verdict for cycle 2: PASS

The cycle 1 blocking defect is fixed correctly and completely, verified
independently by direct code reading (not just trusting `changes.md`), by
reproducing the engineer's test results exactly, and by adding new edge-case
tests that also pass. No regressions in either backend or frontend. Nothing
else to report for the reviewer to re-check beyond this single fix.
