# Review — Dev environment + dev→prod promotion pipeline (specs.md §15)

**Branch:** `pipeline/dev-environment` · **Reviewed:** 2026-08-11 · **Reviewer:** read-only gate
**Scope:** specs.md §15, changes.md "Implementation Changes — Dev environment + dev→prod
promotion (specs §15, 2026-08-06)", tests.md (PASS), infra.md (APPLY, 2026-08-07).
This file supersedes the previous review in full.

---

## VERDICT: **APPROVED**

Approved for **commit + push + PR**, with the subscription quota blocker carried as a
documented follow-up (it is an external constraint, not a defect in this cycle's work),
and with **two merge-sequencing conditions** listed below that a human must satisfy
*before* this lands on `main`. Those conditions are process/ordering, not artifact
defects — nothing in the authored files needs to be rewritten.

Every claim in `changes.md`, `tests.md` and `infra.md` that I could independently
re-derive, I re-derived. All of them held. I found no security, secrets, Entra-auth or
PCI finding of any severity, and no false claim.

---

## Rationale — what I verified independently, and how

### 1. Production IaC is genuinely byte-for-byte unchanged (infra.md §3, §10; §15.16 crit. 2)

Not taken on trust. `git status --porcelain infra/` reports only `M infra/README.md`
plus the two untracked new files; `git diff main -- infra/main.bicep
infra/main.parameters.json infra/modules/` and `git diff HEAD` on the same paths both
return **empty**. All six `infra/modules/*.bicep` and both production template files are
untouched at the byte level. Claim verified.

### 2. Bicep compiles and lints clean — re-run, not trusted (infra.md §4a)

I ran all four commands myself with the same Azure CLI 2.63.0 present in this
environment:

```
az bicep build --file infra/main.dev.bicep  → exit 0
az bicep lint  --file infra/main.dev.bicep  → exit 0, no diagnostics
az bicep build --file infra/main.bicep      → exit 0
az bicep lint  --file infra/main.bicep      → exit 0, no diagnostics
```

I then compiled `main.dev.bicep` to ARM JSON and inspected the output directly rather
than reading the Bicep source alone. Module parameter/output contracts all line up:
`postgres.bicep` (`name`/`location`/`administratorLogin`/`administratorPassword`/
`databaseName`/4× `entraAdmin*` → `fqdn`,`serverName`,`databaseName`),
`appinsights.bicep` (→ `connectionString`,`id`,`name`), `acaEnvironment.bicep`
(→ `id`,`name`), `containerApp.bicep` (→ `fqdn`,`principalId`,`name`). Every output
`main.dev.bicep` declares resolves.

### 3. The quota blocker is real, external, and honestly reported (infra.md §1)

I re-checked it read-only against ARM myself:

```
subscriptionPolicies.quotaId               → FreeTrial_2014-09-01  (spendingLimit: On)
Microsoft.App/locations/eastus/usages
  ManagedEnvironmentCount                  → currentValue 1, limit 1
```

`cae-todo-demo` (prod) occupies the single slot. `cae-todo-demo-dev` cannot be created
on this subscription. infra.md §1 and `infra/README.md`'s "⛔ KNOWN BLOCKER" section
state this accurately, including that the cap is subscription-**global** (so a region
change does not help) and that it is a quota, not a template defect. The three
resolution paths are ranked correctly, and the "share `cae-todo-demo`" option is
correctly marked as needing a superseding `architecture-memory.md` entry rather than a
silent adoption.

**This is correctly classified as blocked-and-flagged, not as falsely-validated.** The
one resource that could not be dry-run is the only thing the report claims is unproven.

### 4. Nothing was provisioned (infra.md §10; §15.16 crit. 11) — verified against live Azure

- `az deployment group list -g rg-todo-demo` shows **no** `main-dev` deployment; the
  newest entries are from 2026-07-27/28, all pre-dating this cycle.
- `az resource list -g rg-todo-demo --query "[?contains(name,'dev')]"` returns
  **empty** — no `cae-todo-demo-dev`, no `pg-todo-demo-dev-cus01`, no dev apps.
- `ManagedEnvironmentCount` is still 1, which independently corroborates that no second
  environment was attempted-and-rolled-back.

The scratch what-if described in infra.md §4c (dev CAE swapped for an `existing`
reference, template unchanged, file deleted) is a legitimate diagnostic technique: it
isolates the one blocked resource and proves the remaining seven creates are all
`-dev`-scoped with every production resource `Ignore`. The committed template still
targets `cae-todo-demo-dev`, which I confirmed by reading `infra/main.dev.bicep:154-161`
and the compiled ARM. The report does not claim the dev CAE itself was validated.

### 5. Secrets / Entra-auth — the new explicit check, applied rigorously

**No credential value of any kind exists anywhere in the repo.** I grepped all 88
tracked files (excluding `node_modules`/`bin`/`obj`) for `Password=`, `Passfile`,
`pwd=`, `AccountKey=`, `SharedAccessKey=`, `client_secret`, `api_key`, SAS tokens and
PEM headers. Every hit is a *placeholder*, a *prohibition*, or *historical prose*:
`README.dev.md:110` is `'<a-strong-password>'`; the `.pipeline/*.md` hits are §14's
record of the password path being deleted; `infra/main.bicep:169` and
`infra/main.dev.bicep:216-217` are comments asserting the absence.

On the **compiled** dev ARM template (not the source, so nothing can hide behind a
Bicep expression):

| Probe | Count |
|---|---|
| `Password=` | **0** |
| `Passfile=` | **0** |
| `UseEntraAuth` | **0** |
| `securestring` params | exactly one: `postgresAdminPassword` |
| Container Apps `secrets` passed to either app module | **none** (module default `{}`) |

The dev connection string resolves to
`Host={pgDevFqdn};Port=5432;Database=tododb;Username=todo-api-dev;Ssl Mode=Require;Trust Server Certificate=true`
— passwordless, `Username=` matching the Container App name / MI name / future
`pgaadauth` role, delivered as a **plain** env var (`main.dev.bicep:287-300`), exactly
as §15.6 and §14 require. `Postgres__UseEntraAuth` appears nowhere in `backend/src`,
`infra/` or the workflows — only in `.pipeline/*.md` as history. tests.md §1a reached
the same conclusion; I re-derived it rather than accepting it.

`PGADMIN_PASSWORD_DEV` is referenced only as (a) an `@secure()` Bicep parameter
(`main.dev.bicep:84-86`), (b) a `gh secret set` command with the value entered
interactively (`infra/README.md:521`), and (c) `read -rs PGPWD_DEV` shell prompts in the
runbook. It is never a literal, never in `main.dev.parameters.json`, never echoed. The
requirement that it **differ from production's** is stated in four places. This is
correctly treated as **the one standing user-approved exception** — server-level
password auth retained for human/admin `psql` access (`modules/postgres.bicep:71-77`
keeps `passwordAuth: 'Enabled'` alongside `activeDirectoryAuth: 'Enabled'`) — while the
app's own path stays Entra-only. **No second password/key-based path was introduced
anywhere.** Specifically checked and clean: app→Postgres (Entra token via MI),
app→App Insights (`DisableLocalAuth: true` + `Monitoring Metrics Publisher`),
apps→ACR (`--identity system` + `AcrPull`), CI/CD→Azure (OIDC federation, no stored
client secret), ADO→Azure (workload identity federation service connection).

No Key Vault is introduced, so the "vault connection must itself be Entra-authenticated"
rule has nothing to bite on. The one non-Entra credential in the whole stack — the Log
Analytics shared key for `appLogsConfiguration` — is pre-existing, read inline via
`law.listKeys()` inside `modules/acaEnvironment.bicep` (never a parameter, output or
committed value), is identical to production by design, and is recorded as follow-up Q4.
Correct call: making dev differ from prod here would defeat the purpose of dev.

The `postgresEntraAdmin*` values in `main.dev.parameters.json` (an object ID and a UPN)
are identity metadata, not credentials. Correctly not treated as secrets.

### 6. CI/CD — the job graph matches §15.9 exactly, and exactly one system auto-triggers

I parsed all three YAML files with a real YAML parser (all three parse clean) and read
the resulting graph programmatically rather than eyeballing indentation:

```
deploy-dev   env=dev         needs=None                  if=None                            concurrency={deploy-dev, cancel-in-progress:false}
verify-dev   env=None        needs=deploy-dev            if=None                            concurrency=None
deploy-prod  env=production  needs=[deploy-dev,verify-dev] if=github.ref=='refs/heads/main'  concurrency={deploy-prod, cancel-in-progress:false}
on: {push: {branches: [develop, main]}, workflow_dispatch: {}}
permissions: {id-token: write, contents: read}
```

That is §15.9.2/§15.9.3 verbatim, including the removal of workflow-level concurrency in
favour of job-level groups. Against §15.16:

| Criterion | Status |
|---|---|
| 4 — trigger both branches, `deploy-prod` needs both jobs + `if` main + `environment: production` | ✅ verified above |
| 5 — `deploy-prod` never rebuilds `todo-api` | ✅ `cd.yml:412-419` asserts `az acr repository show --image todo-api:<sha>` and fails closed; `cd.yml:434-442` only re-points. No backend `docker build` exists in that job. |
| 6 — `verify-dev` credential-free, blocks promotion on failure | ✅ `cd.yml:238-245` — no `environment:`, no `azure/login`, no `secrets`/`vars` reference. All four §15.9.3 checks present with retry/backoff (~120 s), each exiting non-zero on failure. |
| 7 — `ci.yml` on both branches, lints both templates, `guard-promotion-source`, credential-free | ✅ `ci.yml:19-20`, `ci.yml:111-123`, `ci.yml:145-164`. No `azure/login`, no `what-if`. |
| 8 — `azure-pipelines.yml` mirrors the three stages | ✅ `CI` → `DeployDev` → `VerifyDev` → `DeployProd`, same promotion assertion, `environment: production` for the approval check. |

**Exactly one system auto-triggers.** `ci.yml` (`pull_request`) and `cd.yml` (`push`)
are GitHub Actions; `azure-pipelines.yml` is `trigger: none` / `pr: none`. This is a
deliberate, user-approved deviation from §15.9.7's literal text, and it is the **correct**
resolution — two auto-triggering deployment systems would race two promotions onto the
same Container App revision and the same shared ACR tags. The reason and the
re-enable procedure (including "disable the GitHub workflows in the same change") are
documented at `azure-pipelines.yml:1-26`.

All build/test commands are the real ones (`dotnet restore/build/test TodoApi.sln`,
`npm ci` / `npm run build` / `npm test` → `vitest run`, `docker build` against the actual
Dockerfiles, `az bicep build`/`lint`). Nothing is invented or stubbed.

The `docker build` + `docker push` workaround instead of `az acr build` is preserved with
its explanatory comment (`cd.yml:137-145`) — ACR Tasks is disabled on this same free-trial
subscription. Consistent with, and caused by, the same root constraint as the quota
blocker.

**Approval gate is present and real:** `deploy-prod` carries `environment: production`,
which pauses the run before any step executes once required reviewers are configured
(`infra/README.md` E3). The design's dependence on that configuration — and on branch
protection — is stated plainly rather than assumed (infra.md §5b, §7).

### 7. Branch-protection honesty check

infra.md §7 says branch protection on `main` is **still unverified** because `gh` is not
installed. I checked: `command -v gh` → **not installed**. The claim is honest, and the
response (elevating branch protection to Phase E1, first step of the first phase, with
the exact `gh api -X PUT` payload inline) is the right one. The five `contexts` strings in
that payload match the five `name:` fields in `ci.yml` character-for-character — I
compared them; the "GitHub matches on check-run name, not job id" warning is correct and
the deliberate non-renaming of the `iac` job (`ci.yml:92-98`) is a genuinely good catch.

### 8. Engineer's "zero app-code changes" conclusion — independently correct

`git diff main` touches nothing under `backend/`, `frontend/`, and — for the app — nothing
at all. I confirmed the substance rather than the diff: `Cors:AllowedOrigins` is bound
from `IConfiguration` as an array (so `Cors__AllowedOrigins__0` per environment works
with N=1, deny-by-default on empty), `Username=` flows entirely from
`ConnectionStrings:TodoDb` (which is what makes §15.3's four-way identity invariant hold
twice from one binary), and `APPLICATIONINSIGHTS_CONNECTION_STRING` is the only literal
in the telemetry registration. §15.14 puts app changes out of scope and §15.7/§15.8
declare no data-model or API impact — the expected and verified answers agree. The
engineer explicitly declined to invent changes, and flagged the one cosmetic
`todo-api`-mentioning exception-message string rather than silently editing it. Correct
on both counts.

### 9. tests.md PASS is earned, not asserted

tests.md is a genuine independent re-derivation (source reads, a repo-wide grep, and
UTF-16LE string extraction from the pinned `Azure.Monitor.OpenTelemetry.AspNetCore`
1.6.0 assembly to confirm the ACA resource detector exists) plus a full regression run
(81/81 backend, 37/37 frontend, clean build). For an infra-only cycle with no new
application behavior, "no new unit tests, but here is the audit and the regression
baseline" is the right answer — inventing synthetic tests against non-existent code
would have been worse. The gaps it declares (live dev environment absent, runtime
`cloud_RoleName` unobservable, Bicep/YAML validity out of its scope) are accurate and
are exactly the gaps I closed above.

### 10. PCI DSS / OWASP

Not applicable and correctly so. No PAN, CVV, track data, cardholder data, payment
processor, or tokenization flow exists anywhere in the system — I grepped
`backend/src`, `frontend/src`, `infra/`, `.github/` and `azure-pipelines.yml` for
card-data identifiers and the only hits are unrelated substrings inside two build-time
binaries. The data model is `id/title/description/is_completed/created_at/updated_at`.
No payment-data logging path exists because no payment data exists.

The dev environment's *added* attack surface (a second publicly-reachable
unauthenticated CRUD API, since the app has no auth by design per §7/§8) is correctly
identified in `changes.md` and correctly bounded: dev holds only synthetic data, dev CORS
lists only the dev origin, dev has its own Postgres server, and the dev MI is not a
`pgaadauth` principal on the production server at all. I agree with accepting it.

---

## Blocking issues

**None.** Nothing in this cycle needs to be redone by the architect, engineer, tester or
devops agent.

---

## Conditions a human must satisfy BEFORE merging to `main`

These are not defects in the authored work — they are consequences of it that nobody has
written down, and both will bite on the very first merge if unread.

1. **Merging this branch to `main` will stop production CD until the dev stack exists —
   and the dev stack is quota-blocked.**
   Today `main`'s `cd.yml` is `on: push: branches: [main]` and deploys production
   directly. The new `cd.yml` makes `deploy-prod` `needs: [deploy-dev, verify-dev]`. With
   `cae-todo-demo-dev`, `todo-api-dev` and `todo-web-dev` non-existent (I confirmed:
   `az resource list` returns no `-dev` resources), `deploy-dev` will fail at its first
   `az containerapp registry set`, or earlier at `azure/login` because the
   `…:environment:dev` federated credential does not exist yet (infra.md §6). Both
   downstream jobs then never run. **Net effect: production becomes undeployable via CD
   for as long as the Free Trial quota blocks Phase A.**
   I grepped `infra.md`, `infra/README.md`, `cd.yml` and `infra-proposal.md` — this
   consequence is documented **nowhere**. infra.md §1 says "Phase A cannot run today"
   but does not connect that to "and merging this freezes prod deploys."
   *Choose one, consciously:* (a) hold the merge to `main` until the quota is resolved
   and Phases A–E complete — consistent with the user's decision to PAUSE; or (b) merge
   now and accept that production is deployed by hand (`az containerapp update`) in the
   interim. Do not discover this on the first hotfix.

2. **This PR cannot target `main` without failing its own new guard.**
   `guard-promotion-source` (`ci.yml:145-164`) fails any PR into `main` whose head is not
   `develop` or `hotfix/*`. On a `pipeline/dev-environment` → `main` PR the job runs
   (base_ref is `main`), and fails — CI goes red on the PR that introduces the check.
   I also confirmed the `develop` branch **does not exist** yet, locally or on `origin`.
   The spec's own model (§15.9.1) says `pipeline/*` branches target `develop`, so the
   intended path is: create `develop` off `main` (README Phase E / §15.9.1), PR this
   branch into `develop`, and let the eventual `develop` → `main` PR be the promotion.

---

## Non-blocking suggestions

1. **Record the two conditions above in `infra.md` and `infra/README.md`** so the next
   person to touch this does not have to rediscover them. A short "merge sequencing"
   note directly under the ⛔ KNOWN BLOCKER heading is the natural home.
2. **§15.16 criterion 10 is satisfied in the wrong file.** The +$19–25/month cost delta,
   the shared-log-cap risk and the Plan A/Plan B decision live in `infra.md` §0/§9/§7 and
   `infra-proposal.md`, not in `changes.md` as the spec asks. Substantively fine — the
   user approved the cost in chat on 2026-08-07 and it is recorded — but §15.9.5's "the
   engineer must record in `changes.md` which plan was implemented and why" is unmet in
   letter. One paragraph appended to `changes.md` closes it.
3. **Plan A is implemented but its precondition (E0) is still unverified.** `gh` is
   genuinely unavailable, so nobody has confirmed the repo is public or on a paid plan.
   If E0 reveals private-on-Free, both branch protection (E1) and the `production`
   required-reviewer rule (E3) fail, and Plan B (§15.9.5) is required — a real workflow
   rewrite, not a config tweak. Run `gh repo view --json visibility,isPrivate` **first**,
   as README E0 already instructs.
4. **`az acr repository show` as the promotion assertion proves the tag exists, not that
   it is the tag `verify-dev` exercised.** In practice they coincide because both derive
   from `github.sha` in the same run, but a stronger form would capture the digest in
   `deploy-dev`'s outputs and assert digest equality in `deploy-prod`. Cheap hardening,
   not needed now.
5. **Frontend rebuild-instead-of-promote (§15.9.4 / infra.md §5d)** remains the one honest
   residual risk. It is disclosed in three places and correctly deferred to Q3 (a runtime
   `config.js` refactor is an application change). Worth scheduling as the next
   application cycle — it also removes the backend-before-frontend ordering constraint
   that has shaped every pipeline in this project.
6. **`infra/README.md`'s ad-hoc dev deploy snippet (~line 766)** omits
   `containerTargetPort=80`, unlike Phase A. The surrounding prose explains why
   (steady-state uses 8080; `=80` is bootstrap-only), so this is correct as written —
   just verify a reader following the quick-reference section first isn't misled on a
   fresh bootstrap.
7. **Follow-up Q4** (moving both ACA environments from `destination: log-analytics` to
   `azure-monitor`, eliminating the last shared-key dependency) is the only remaining
   non-Entra path in the stack. Do it for **both** environments together so dev keeps
   matching prod.
