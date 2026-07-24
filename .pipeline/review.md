# Review — Todo Application, Cycle 1

Reviewer: Review agent (read-only gate)
Date: 2026-07-23

## VERDICT: REJECTED — needs re-implementation

One confirmed, spec-violating defect blocks approval. It is a small, well-isolated
implementation bug in `TodosController` (route constraint), not an architectural
flaw and not a testing gap. Everything else in the delivery independently checks
out against the spec.

---

## What I verified independently (not just trusting the reports)

I read the actual source, not just `changes.md`/`tests.md`:

- **The defect is real and I reproduced the root cause by reading the code.**
  `backend/src/TodoApi/Controllers/TodosController.cs` declares the id-based
  actions with the `:guid` route constraint:
  - line 34: `[HttpGet("{id:guid}")]`
  - line 75: `[HttpPut("{id:guid}")]`
  - line 101: `[HttpDelete("{id:guid}")]`
  A URL segment that is not a valid GUID fails the `:guid` constraint, so ASP.NET
  Core routing reports "no endpoint matched" and returns a plain `404` — the
  action is never invoked, so no `400`/`ProblemDetails` is produced. This
  directly contradicts **spec §3.5**: *"`{id}` is a `Guid`; malformed ids return
  `400`."* The tester's root-cause analysis is accurate, and the three failing
  tests (`GetTodo/UpdateTodo/DeleteTodo_WithMalformedId_ShouldReturn400PerSpec`)
  correctly encode the spec requirement. The tests are not wrong — the code is.

- **Everything else matches the spec.** Cross-checked file-by-file:
  - **Data model (§2):** `Todo.cs`, `TodoDbContext.cs`, and the generated
    migration `Migrations/20260723074003_InitialCreate.cs` produce exactly the
    specified table — `uuid` PK, `varchar(200)` NOT NULL title, `varchar(2000)`
    nullable description, `boolean` default false, two `timestamptz` columns with
    `now()` defaults, and `ix_todos_created_at` DESC. Column names snake_case,
    migration named `InitialCreate`. Correct.
  - **API surface (§3.5):** all six endpoints present with the right verbs,
    status codes (200/201/204/400/404), `CreatedAtAction` Location header,
    newest-first ordering (`OrderByDescending(t => t.CreatedAt)`), and
    `[ApiController]` automatic validation. DTOs and data-annotation validation
    (`[Required]`, `[StringLength(200, MinimumLength=1)]`, `[StringLength(2000)]`)
    match §3.5. The `bool? IsCompleted` + `[Required]` decision (changes.md
    deviation #4) is correct reasoning and matches "required boolean".
  - **Config (§3.3):** connection-string key `TodoDb`, CORS policy `frontend`
    from `Cors:AllowedOrigins`, `/health` returning `{status:"ok"}` un-gated by
    CORS. `Program.cs` guards the missing-connection-string case and wraps
    `Database.Migrate()` in try/log (§3.4). Correct.
  - **Frontend (§4):** `api.ts` (fetch wrapper, throws `ApiError` on non-2xx,
    parses `ValidationProblemDetails`), `useTodos.ts` (update-on-success, newest-
    first prepend, toggleComplete issues PUT with flipped flag), and the three
    components all match §4.3. Dockerfile + `nginx.conf` (listen 8080, SPA
    fallback) match §4.4.
  - **Backend Dockerfile (§3.6):** multi-stage sdk→aspnet, port 8080, backend
    build context — matches the spec verbatim.
  - **Azure runbook (§5):** `scripts/deploy-azure.sh` follows §5.7 ordering
    (backend first, capture FQDN, build frontend with baked URL, then open CORS),
    uses the §5.6 scaling/sizing values (backend min 0/max 3, frontend min 1/
    max 2, 0.25 vCPU/0.5 GiB), `Ssl Mode=Require` (§5.4), and Container Apps
    secret for the connection string (§5.5). Not executed — correct per spec.

- **Security review: clean.** No hardcoded production secrets. The only committed
  credential is the throwaway local Postgres password (`todo`) in
  `appsettings.Development.json`, which is exactly the documented local-dev value
  in spec §6.3. The Azure runbook takes `PG_ADMIN_PASSWORD` from the environment
  and refuses to run if unset. CORS is origin-restricted, TLS to Postgres is
  required, and all DB access is via EF Core (parameterized, no raw SQL). No
  injection, auth-bypass, or secret-leak issues (auth is explicitly out of scope,
  §7).

- **changes.md claims match the diff.** The deviations it lists (Swashbuckle
  10.2.3, classic `.sln`, `bool?` IsCompleted, design-time factory, extra
  `launchSettings.json`/`vite-env.d.ts`) are all present in the actual code and
  are reasonable. No overstated claims found.

- **Tests genuinely cover the spec.** The 27 backend tests exercise each endpoint,
  validation boundaries (title 1/200/201 chars, description 2000/2001, whitespace-
  only title, missing isCompleted), 404 paths, ordering, CORS preflight, and
  health — real integration tests via `WebApplicationFactory<Program>`, not
  direct method calls. 37 frontend tests cover the api client, `useTodos`, and
  all components. Coverage is appropriate; the failure is a code bug the tests
  correctly caught, not a missing test.

---

## Blocking issues

1. **Malformed GUID returns 404 instead of 400 (spec §3.5).**
   `backend/src/TodoApi/Controllers/TodosController.cs` lines 34, 75, 101 use the
   `{id:guid}` route constraint on GET/PUT/DELETE by id. A non-GUID segment fails
   the constraint and falls through to a bare `404`, violating §3.5's "malformed
   ids return `400`". Reproduced by three failing tests. This is the sole reason
   for rejection.
   Suggested fix (engineer's call): drop the `:guid` constraint, bind `id` as
   `string`, `Guid.TryParse` it, and return `BadRequest(...)` (ideally a
   `ProblemDetails`) on parse failure; or install a routing/exception translation
   that converts the constraint miss into a 400. Keep the existing 404-when-not-
   found behavior for well-formed-but-unknown GUIDs (that path already passes).

Because this is a localized controller change — not a data-model, API-contract,
or infrastructure change — the correct stage to redo is **implementation**
(engineer), followed by a re-run of the existing test suite. No re-architecture
and no additional tests are required; the tests that must go green already exist.

---

## Non-blocking suggestions (do not block approval)

1. **No real Postgres / Docker E2E in this environment.** Tests substituted EF
   Core InMemory (backend) and a mocked API (frontend) because Docker Desktop's
   engine was not running. This is an environmental limitation, honestly
   disclosed by both engineer and tester, and consistent with the spec's testing
   guidance. It does mean the `InitialCreate` migration, `timestamptz`/`now()`
   defaults, `varchar` length enforcement, and `Ssl Mode=Require` behavior were
   verified by code inspection only, not exercised against a live server. Not a
   blocker for this cycle, but a human should run `docker compose up -d` +
   `dotnet run` once to confirm the schema and CRUD against real Postgres before
   any production use.
2. When fixing issue #1, consider returning a `ValidationProblemDetails`-shaped
   400 for the malformed id so the frontend's existing `extractErrorMessage`
   (which reads `.errors` / `.title`) surfaces a useful message consistently.
3. Frontend `npm audit` reports 2 dev-server-only advisories (esbuild/vite).
   Correctly scoped out of the nginx-served production bundle; fine to defer.

---

# Review — Todo Application, Cycle 2

Reviewer: Review agent (read-only gate)
Date: 2026-07-23

## VERDICT: APPROVED

The single blocking defect from cycle 1 (malformed GUID returning 404 instead of
the spec §3.5-mandated 400) is fixed correctly, completely, and without
regressing anything approved in cycle 1. Verified by reading the actual source,
not by trusting changes.md/tests.md.

---

## What I verified independently

- **The fix is in the code and is correct.**
  `backend/src/TodoApi/Controllers/TodosController.cs`:
  - `GetTodo` (line 34), `UpdateTodo` (line 81), `DeleteTodo` (line 112) now
    declare `[Http*("{id}")]` — the `:guid` route constraint is gone from all
    three. The parameter is `string id`. Each action's first statement is
    `if (!Guid.TryParse(id, out var todoId)) return MalformedId(id);` (lines
    40-43, 90-93, 118-121), so a non-GUID segment now reaches the action and
    produces a 400 instead of falling through to a routing 404.
  - The `MalformedId` helper (lines 143-147) returns
    `BadRequest(new ValidationProblemDetails(ModelState))` with an `errors`
    entry keyed by `"id"`. `ValidationProblemDetails` derives from
    `ProblemDetails`, so this satisfies spec §3.5's "Errors use `ProblemDetails`"
    requirement, and the `errors`/`title` shape is exactly what the frontend's
    existing `extractErrorMessage` reads (this also adopts cycle 1 non-blocking
    suggestion #2).
  - The well-formed-but-unknown-GUID path (`FindAsync` -> `NotFound()`) is
    unchanged, so 404 for a valid-shape-but-nonexistent id still works
    (lines 45-49, 95-99, 123-127). No regression to the 404 behavior cycle 1
    approved.
  - `CreateTodo`'s `CreatedAtAction(nameof(GetTodo), new { id = todo.Id }, ...)`
    (line 77) is untouched and still resolves against the now-unconstrained
    `{id}` template.

- **The fix is completely scoped.** I grepped `backend/src` for `:guid`: the
  only remaining occurrence is inside an explanatory code comment
  (`TodosController.cs:138`), not a route. No other id-based route was missed.
  The only other route is `/health`, which has no id segment.

- **Tests genuinely cover the requirement, not just exist.** In
  `backend/tests/TodoApi.Tests/TodosControllerTests.cs`, the three
  `*_WithMalformedId_ShouldReturn400PerSpec` tests (GET/PUT/DELETE, lines 239,
  277, 288) assert `HttpStatusCode.BadRequest`, and the two new cycle-2
  hardening tests — `GetTodo_WithWhitespaceOnlyId_Returns400` (line 253,
  `%20%20%20`) and `GetTodo_WithExtremelyLongGarbageId_Returns400` (line 266,
  2000-char garbage) — also assert `BadRequest`. These are real assertions on
  the exact status code the spec requires.

- **changes.md and tests.md claims match reality.** changes.md's Cycle 2 section
  accurately describes the route/param/`Guid.TryParse`/`MalformedId` changes and
  the before/after behavior table. tests.md's Cycle 2 section's claims (29/29
  backend, 37/37 frontend, new tests, no other `:guid` routes) are consistent
  with the source I read. No overstated claims found.

- **No new security issues.** The fix adds no secrets, no raw SQL, no new input
  sinks; it makes input validation stricter (malformed ids are now explicitly
  rejected). Interpolating the offending id into the ProblemDetails message is
  safe — it is JSON-encoded in the response body, not reflected as HTML/executed.

## Blocking issues

None.

## Non-blocking suggestions (carried forward, do not block approval)

1. No real Postgres / Docker E2E was possible in this environment (Docker Desktop
   engine not running); the `InitialCreate` migration, `timestamptz`/`now()`
   defaults, `varchar` length enforcement, and `Ssl Mode=Require` remain verified
   by code inspection only. A human should run `docker compose up -d` + `dotnet
   run` once against real Postgres before production use. Unchanged from cycle 1.
2. Frontend `npm audit` reports 2 dev-server-only advisories (esbuild/vite),
   correctly scoped out of the nginx-served production bundle. Fine to defer.
