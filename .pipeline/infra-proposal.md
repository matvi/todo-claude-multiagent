# Infrastructure proposal — 2026-08-06

Branch: `pipeline/entra-passwordless-connection-string`. Scope: specs.md **§14**
(Entra-ONLY Postgres auth for the application), specifically **§14.8** — the
devops-stage work item that the engineer and tester both deferred.

This proposal **supersedes** the prior `.pipeline/infra-proposal.md` (2026-07-28,
App Insights / managed-identity cycle), whose decisions were approved and are
already applied in `infra/`.

---

## Headline: there is no SKU or service choice to make this cycle

**No new Azure resource. No SKU change. No region change. No server-auth change.
No role assignment. Cost delta: exactly $0.** specs §14.1/§14.8/§14.11 all state
this, and I verified it against the live subscription (read-only, below) — nothing
in this cycle touches a billable dimension.

So the usual "Option A / B / C + monthly cost" tables do not apply to any service.
What follows is instead: (1) what I verified live, (2) the concrete file-level
changes I propose to make in APPLY mode, and (3) the **three genuine judgement
calls** that do need your sign-off — all of them config/process decisions, none of
them cost-bearing.

---

## 1. Live state verified this session (read-only `az`, 2026-08-06)

I re-derived these rather than trusting §14.2, since the cutover depends on them.

| Fact | Verified value | Matches §14.2? |
|---|---|---|
| Subscription / tenant | `9ba6633f-…ef26` / `6ce2ff72-…c03f5` | yes |
| `todo-api` `ConnectionStrings__TodoDb` | `secretRef: todo-db-connection` (still the SQL-auth string) | yes |
| `todo-api` `Postgres__UseEntraAuth` | `"false"` | yes |
| `todo-api` Container Apps secrets | exactly one: `todo-db-connection` | yes |
| `todo-api` identity principalId | `05c60b63-dda9-4a3e-be87-ac8350600b79` | yes |
| `todo-api` running image | `acrtododemo3bkqjv23abtua.azurecr.io/todo-api:bb8786d…` | — |
| `pg-todo-demo-cus01` | Burstable `Standard_B1ms`, PG **16**, Central US | yes |
| Postgres `authConfig` | `activeDirectoryAuth: Enabled` **+ `passwordAuth: Enabled`** | yes — **stays this way** |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | present as a plain env var | yes |

**Not verifiable from here, and deliberately not re-proposed as work:** the
in-database `pgaadauth` role for `todo-api`. It has no ARM surface, so it cannot be
read with `az`; it requires a `psql` session against the `postgres` database as the
Entra admin, from a firewall-allowed IP (lessons §4a). Per
`.pipeline/architecture-memory.md` (2026-08-06) and `.pipeline/infra.md` ("Live
cutover status — 2026-07-28") it **already exists and is granted**. I am treating it
as **done**, and proposing only that the runbook make
`select * from pgaadauth_list_principals(false);` an explicit **verify-first**
pre-check (§14.7), with `pgaadauth_create_principal_with_oid(...)` documented as the
by-OID repair path used *only if* the row is missing or its `objectid` ≠
`05c60b63-…`. No re-creation is being proposed.

---

## 2. Proposed changes, file by file (nothing written yet — PROPOSE mode)

### `infra/main.bicep` — 1 deletion + 2 comment corrections

- **Delete the `Postgres__UseEntraAuth` env var** from the `todoApi` module's
  `envVars` array (currently lines 236–239). The app deleted the key entirely, and a
  tester-pinned test asserts the flag is dead — so this is dead config that
  actively misleads the next reader, not a live defect.
- **Correct the comment block above the `todoApi` module** (lines ~205–212): it
  still advertises `Postgres__UseEntraAuth=true: backend takes its Entra-token DB
  path` as one of three config values.
- **Correct the comment above `todoDbConnectionString`** (lines ~169–172) to the
  §14.8 invariant wording: passwordless + non-secret, `Username=` must equal the
  `pgaadauth` role name (== Container App name == MI name), and the app *strips* any
  `Password=` it is handed and will not fall back.

The `todoDbConnectionString` value itself is **already correct** (passwordless,
`Username=${todoApiName}`) and the `todo-db-connection` secret is **already absent**
from Bicep — both landed in the 2026-07-28 cycle. No value change needed.

### `infra/modules/postgres.bicep`, `infra/main.parameters.json` — NO CHANGE

Per §14.8(2)/§14.12: dual auth, `todoadmin`, the `@secure() postgresAdminPassword`
param and the `PGADMIN_PASSWORD` CI secret all stay. I will add a "do not
'helpfully' disable this" note in the README so a future reader does not undo it.

### `.github/workflows/cd.yml` and `azure-pipelines.yml` — NO CHANGE

I verified by grep that **neither** pipeline ever writes `ConnectionStrings__TodoDb`
or `Postgres__UseEntraAuth`. `cd.yml` only (a) sets the ACR registry identity,
(b) `az containerapp update --image`, and (c) `--set-env-vars
Cors__AllowedOrigins__0=…`, which replaces only that one variable and leaves the
others untouched. So a CD run after the cutover will **not** revert the new
passwordless connection string. §14.8 says explicitly "no CD workflow change";
that matches what the files actually do. Nothing to author here.

### `infra/README.md` — the bulk of the real work

Currently stale in five specific places:

1. **Files table (line 15)** says `main.bicep` "assembles the DB connection string
   as a backend Container Apps **secret**" — untrue since 2026-07-28; it is a plain
   env var.
2. **Phase 4 cutover step 4 (line ~219)** still instructs setting
   `Postgres__UseEntraAuth=true`. The variable no longer exists.
3. **Same step** says the `todo-db-connection` secret must be **DROPPED** at
   cutover. §14.9 changes this: it is **kept** until the Entra connection is verified
   live (it is part of the rollback revision), then deleted as a separate cleanup
   (step 6).
4. **No imperative cutover runbook exists.** This is the important one: CD does not
   apply Bicep (lessons §2c), so the Bicep edit above changes *nothing* on the live
   app. A Bicep-only fix is not a fix. The README must carry the §14.9
   `az containerapp update --set-env-vars … --remove-env-vars
   Postgres__UseEntraAuth` sequence, including capturing `PREV_REV` **before** the
   cutover so rollback is one command.
5. **The "⚠️ First time applying" callout (lines ~311–317)** describes the old
   flag-flip cutover and needs re-pointing.

Plus the two additions §14.8(3) asks for: `pgaadauth_list_principals` as
verify-first, and the "server keeps password auth for humans, intentionally" note.

### `.pipeline/infra.md`

Overwritten in APPLY mode with the §14 delta, the approved decisions below, the
`az bicep build` + `az bicep lint` validation output, and a re-checked branch
protection status on `main` (a real remote now exists — `matvi/…`, per the merged
PRs #6/#7 — so unlike the last two cycles the `gh api …/branches/main/protection`
check should actually be runnable this time; I will run it read-only and record the
true answer rather than repeating "could not be verified").

---

## 3. Decisions that need your sign-off (no cost impact on any of them)

### 3a. When to delete the `todo-db-connection` secret (spec's own open question Q1)

| Option | Rollback safety | Secret exposure window |
|---|---|---|
| Delete during the cutover | **Loses the clean revision rollback** — the previous revision's `secretRef` would dangle | shortest |
| **Delete after ≥1 healthy revision on Entra auth** | Full revision rollback stays available throughout | short (hours–days) |
| Keep indefinitely | Full | indefinite — a real `todoadmin` password sits in ACA config forever |

**Recommendation: option 2** — matches §14.9 step 6 and the spec's own Q1 answer.
The secret is the *only* thing making `az containerapp revision activate $PREV_REV`
work, and §14.9 has no in-app fallback by design, so deleting it in the same change
as the cutover removes the escape hatch precisely when it is most likely to be
needed. I will write it into the runbook as an explicitly separate, later step.

### 3b. `Trust Server Certificate=true` in the passwordless connection string

The cutover string in §14.9 (and the Bicep) is
`…;Ssl Mode=Require;Trust Server Certificate=true`. That encrypts but **skips
certificate verification** (MITM-susceptible) — flagged as devops-owned in
`changes.md` for two cycles now.

| Option | Effect | Risk |
|---|---|---|
| **Keep `Ssl Mode=Require;Trust Server Certificate=true`** (spec-literal) | No behavior change; §14.5 explicitly forbids the normalizer touching it | Residual MITM caveat carries forward, unclosed |
| Move to `Ssl Mode=VerifyFull` | Real cert validation against the Azure Postgres CA | Contradicts §14.5 step 4 / §14.8's "minimal, no other change"; needs a live re-test of the cutover; would turn a 3-line cycle into a risk-bearing one |

**Recommendation: keep it as-is this cycle** and carry it as an explicit follow-up
item rather than silently bundling a TLS-behavior change into a cutover that
already has no fallback. Say the word if you'd rather close it now and I'll fold
it into the same change — it is a one-token edit, but it deserves its own live
verification rather than riding along.

### 3c. Where the cutover runbook lives

| Option | Pros | Cons |
|---|---|---|
| **New "Phase 5" section in `infra/README.md`** + surgical corrections to Phase 4 | Keeps the historical Phase 4 record honest; one file to read | README grows |
| Rewrite Phase 4 in place | Shorter file | Erases the record of what was actually done on 2026-07-28 |
| Committed script `scripts/cutover-entra-connection.sh` | Copy-paste-proof | A script that performs a live, irreversible-ish cutover is exactly the thing this pipeline says a human should run deliberately, step by step, checking output between steps |

**Recommendation: option 1** (new Phase 5 + targeted Phase 4 corrections, no
script). The cutover has verification gates between steps (`curl /api/todos`, the
log check) that a script would encourage skipping.

---

## 4. Two things to flag, not to fix

- **`.pipeline/deployment-lessons-learned.md` is not on this branch.** §14.9 says
  the cutover outcome "must be written back into … §5a", and §5a is currently marked
  UNRESOLVED. The file lives only on the `docs/deployment-lessons-learned` branch
  (commit `011da3d`). Whoever runs the cutover needs to know where to write, or the
  close-out will be lost. Not proposing to move or merge it — just naming it.
- **Standing CD-trigger deviation (pre-existing, out of scope).** `cd.yml` triggers
  on `push: [main]`, not the `pull_request: closed` + `merged == true` form this
  pipeline normally standardizes on. That was a deliberate, documented choice in
  `.pipeline/infra.md` (specs §11.5 is explicit about `push`, and with branch
  protection requiring PRs the two are equivalent). §14.8 forbids a CD change this
  cycle, so I am not touching it — noting it so it isn't mistaken for drift I
  introduced.

---

## 5. Cost

**Delta: $0/month.** Removing an env var, editing comments, changing a Container App
env var from a `secretRef` to a plain value, and (later) deleting one Container Apps
secret are all free operations on resources that already exist. No SKU, replica
count, storage size, retention setting, or region changes.

For context, the standing estimate is unchanged from `.pipeline/infra.md`
(ESTIMATE ONLY, region/usage-dependent, not a quote): **~$25–37/month**, dominated
by the always-on Burstable `Standard_B1ms` Postgres (~$16–18) and the `todo-web`
min-1 replica (~$4–12), plus ACR Basic (~$5).

---

## Status

**Waiting on approval of 3a, 3b and 3c.** No file under `infra/`,
`.github/workflows/`, or `azure-pipelines.yml` has been created or modified, and
`.pipeline/infra.md` has not been updated — all of that happens in APPLY mode, after
sign-off. Nothing was provisioned; the only Azure commands run this session were
read-only `az account show` / `az resource show`.
