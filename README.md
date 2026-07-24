# 4-agent pipeline for Claude Code

## Install (Windows 11)

Claude Code on Windows runs shell commands through Git Bash (bundled with
Claude Code / Git for Windows) unless you've opted into the newer PowerShell
tool. The hook script here is Bash and calls itself via `bash scripts\...`
explicitly, so it works either way without needing `chmod`.

1. Unzip `pipeline-setup.zip`. `.claude` is a **hidden folder** — enable
   "Show hidden items" in File Explorer's View menu, or just use the copy
   commands below, which don't care about hidden-folder visibility.

2. Copy the folders into your project root. In PowerShell:
   ```powershell
   Copy-Item -Recurse -Force "C:\path\to\pipeline-setup\.claude" "C:\path\to\your-project\.claude"
   Copy-Item -Recurse -Force "C:\path\to\pipeline-setup\scripts" "C:\path\to\your-project\scripts"
   ```
   Or if you're using Git Bash:
   ```bash
   cp -r pipeline-setup/.claude your-project/
   cp -r pipeline-setup/scripts your-project/
   ```

3. No `chmod` step needed — the hook in `reviewer.md` invokes the script as
   `bash scripts/reviewer-write-guard.sh`, so the executable bit is never
   consulted.

4. Make sure the Azure CLI (`az`) is installed and `az login` has run in the
   same shell Claude Code will use, since the architect agent shells out to
   it for read-only checks.

5. Restart Claude Code if `.claude/agents/` didn't exist in this project
   before (the file watcher only picks up a new `agents` directory on
   startup, not mid-session).

6. Sanity-check line endings once: if you ever edit
   `scripts/reviewer-write-guard.sh` in a Windows editor, make sure it saves
   with LF line endings, not CRLF — a `.sh` file with CRLF endings fails
   silently under Git Bash. The version in this zip already uses LF, so this
   only matters if you modify it later. If you're in a git repo, add a
   `.gitattributes` line `*.sh text eol=lf` so git normalizes this
   automatically.

## Run it
```
/pipeline Build a REST API for booking search with pagination and filters
```
This runs architect → engineer → tester → reviewer in strict order, looping
back on rejection, capped at 3 cycles so it can't loop forever.

You can also invoke any single stage manually, e.g. after tweaking specs.md
by hand:
```
Use the engineer subagent to implement based on the current specs.md
```

## Where the design deviates from what you asked, and why

**Agents don't each get exactly one file.** An agent that can only *read*
its own file can't read its *input*. Each agent reads what it needs and
writes to its own output file:

| Agent | Reads | Writes | Tools |
|---|---|---|---|
| architect | `specs.md`, `architecture-memory.md` (persistent), `review.md` (if revising) | `specs.md`, appends to `architecture-memory.md` | Read, Write, Grep, Glob, Bash (az cli, read-only use), WebSearch/WebFetch |
| engineer | `specs.md`, `review.md` | `changes.md` + actual code | Read, Write, Edit, Bash, Grep, Glob |
| tester | `specs.md`, `changes.md` | `tests.md` + test files + a commit/push/PR on PASS | Read, Write, Edit, Bash (push-guarded), Grep, Glob |
| devops | `specs.md`, `changes.md`, `tests.md`, existing `infra/`, `review.md` (if revising) | PROPOSE: `infra-proposal.md` only. APPLY: `infra.md` + IaC files + `.github/workflows/` + `azure-pipelines.yml` | Read, Write, Edit, Bash (apply-guarded) |
| reviewer | everything | `review.md` only (hook-enforced) | Read, Grep, Glob, Bash, Write (write-guarded) |

**Reviewer is enforced read-only by a hook, not just instructions.**
`tools`/`disallowedTools` can't scope *where* a tool writes, only *whether*
it's available at all — and the reviewer needs Write for review.md itself.
So it gets Write, but a `PreToolUse` hook (`reviewer-write-guard.sh`) blocks
any write outside `.pipeline/review.md`. This is the actual guarantee;
telling a model "don't write to code" in prose is a preference, not a
control. The same applies to the tester's Edit/Write scope (test files only)
— that one's currently prompt-enforced only; if you want it hard-enforced
too, add a similar hook matching on file path patterns for test directories.

**A slash command drives the sequence, not automatic delegation.**
Claude Code's automatic delegation picks a subagent based on your request
matching its `description`. That's great for one-off use, but for a strict
ordered pipeline where step N must not start until step N-1's output file
exists, relying on "Claude will probably call them in the right order" is
fragile. `/pipeline` makes the order explicit and gates each step. Each
agent's prompt also independently checks its required input files exist and
refuses to proceed (i.e., guessing) if they don't — defense in depth.

**Loop cap of 3.** Without a cap, a rejection loop can run indefinitely and
burn a lot of tokens. Adjust the number in `pipeline.md` if you want more or
fewer cycles.

## SKU approval is a real pause, not a formality

Subagents can't interactively wait mid-task for your reply — they run to
completion and hand a result back. So "ask what SKU is needed" is built as
two separate devops invocations with a genuine stop in between:

1. **PROPOSE**: devops diffs specs.md against whatever IaC already exists,
   and for anything new or changed, writes `infra-proposal.md` — 2–3 SKU
   options per service, a recommendation with reasoning, and a rough cost
   estimate. It touches no other file and does not proceed.
2. `/pipeline` shows you that file and **actually stops and waits** for
   your reply — this is written into the command explicitly as a required
   pause, not "feel free to ask if unsure."
3. **APPLY**: once you've replied with your choice, devops is re-invoked
   with your approved SKUs, and only then edits any Bicep file, runs
   what-if validation, and authors the CI/CD workflows.

If nothing about the infrastructure changed since the last run, devops
skips straight to APPLY with nothing to approve — you're only interrupted
when there's an actual decision to make.

## Architect's persistent memory

`.pipeline/architecture-memory.md` is the one pipeline file that's never
reset. Every other `.pipeline/*.md` file gets overwritten on each
`/pipeline` run for the current feature; this one is append-only and
persists across every run on the project, going back to the first feature
you ever ran through this pipeline. The architect reads it first and adds a
short entry after finalizing each spec — service inventory, key decisions,
constraints future features need to respect. Commit it to git like source
code; it's what keeps a fifth feature request consistent with decisions
made three features ago instead of re-deriving everything from scratch
each time.

## Security: SOLID, OWASP, and PCI DSS

Since this involves payment processing, the engineer agent's standards
section is no longer generic "write good code" — it has concrete,
checkable requirements:

- **SOLID** — each principle spelled out with what to actually look for
  (e.g. no `NotImplementedException` overrides violating Liskov), not just
  named and left as a vibe.
- **OWASP Top 10** — a working checklist (injection, broken auth, broken
  access control, etc.) the engineer verifies against before calling
  something done, and the reviewer independently re-checks rather than
  trusting the engineer's self-report.
- **PCI DSS** — the strong default is: don't handle raw cardholder data at
  all. Card entry goes through a processor's hosted fields / tokenization
  (Stripe Elements or equivalent) so your servers only ever see a token,
  which collapses your actual PCI scope. Full PAN/CVV/track data must never
  be stored, even encrypted. Encryption in transit (TLS 1.2+) and at rest
  for whatever account data you *are* allowed to keep. Least-privilege
  access to anything near the cardholder data environment. Audit logging
  of access — without ever logging the sensitive data itself.

The reviewer now has a dedicated `REJECTED — security/PCI DSS issue`
verdict, and `/pipeline` treats that as higher priority than a generic
re-implementation pass rather than letting it get bundled with unrelated
feedback.

**One thing worth knowing**: this setup gives you strong engineering
practice and scope-reduction guidance, but it does not make your
application PCI DSS certified by itself — actual compliance still requires
a qualified assessment (SAQ or QSA-led, depending on your processing
volume) against your real, deployed system. Treat the agent's output as a
strong starting point for that assessment, not a substitute for it.

## The devops agent never touches real infrastructure

`az deployment group create` and `terraform apply` aren't like a code
change — they're not reversible with `git revert`, they cost real money,
and in an Azure AD-joined org context they can touch shared resources. So
the devops agent is scoped to **author and dry-run-validate only**
(`az deployment group what-if`, `terraform plan`). A `PreToolUse` hook
(`devops-apply-guard.sh`) blocks the actual create/apply/destroy commands at
the tool level — same enforcement pattern as the reviewer's write guard, not
a prompt-only request. Applying the infra it authors is a deliberate step
you run yourself, using the exact commands it writes into `infra.md`.

## Two CI/CD systems, only one auto-triggers

Since you wanted both GitHub Actions and Azure Pipelines authored: the
devops agent writes both, but only lets **one** of them auto-trigger on
push/PR. The other is configured with a manual-only trigger
(`trigger: none` in Azure Pipelines, by default) and documented as ready to
flip on. Running both live at once means duplicate builds at best and two
systems racing to deploy the same environment at worst — that's worth
avoiding even though you asked for both to exist. `infra.md` states clearly
which one is currently live.

## Deploy triggers automatically on merge — by design, with one safety net left in

You asked for merge-to-main to trigger a Container Apps deployment, so
that's what `cd.yml` does — no manual approval click required. The safety
nets that remain: the merge itself is still a human decision (the tester
never pushes to main, only opens a PR against it), and the deploy job runs
under a `production` GitHub Environment, which gives you deployment history
and a one-line addition (a required-reviewer rule on that Environment) if
you later decide you want a manual gate *between* merge and deploy without
touching the workflow file itself.

## Required one-time repo setup: branch protection on `main`

Everything above — the tester never pushing to `main`, the PR-only flow,
merge-triggers-deploy — relies on a human actually reviewing the PR before
it merges. Nothing in the agent config *enforces* that on GitHub's side; it
just describes how the agents behave. Enforce it for real with a branch
protection rule, once, per repo:

**Settings → Branches → Add branch protection rule → `main`:**
- ✅ Require a pull request before merging
- ✅ Require approvals (at least 1)
- ✅ Require status checks to pass before merging → select the `ci.yml`
  job(s), so a red CI run can't be merged
- ✅ Do not allow bypass of the above (don't exempt admins if you want it to
  actually hold)

The devops agent checks this automatically (a read-only
`gh api repos/{owner}/{repo}/branches/main/protection` call) and reports the
current state in `infra.md` — if it's not set up yet, `infra.md` says so
plainly rather than assuming you already did it. Setting the rule itself is
a GitHub repo setting, not something in `.claude/`, so it's a one-time
per-repo thing, not something you redo when you update the agent files.

## Auto-commit and PR flow

The tester commits and pushes only when its own verdict is PASS, and only
ever to the feature branch created at the start of `/pipeline` — a
`PreToolUse` hook (`tester-push-guard.sh`) blocks any `git push` that
targets `main`/`master`, whether that's because the branch checked out is
main itself or because the push command explicitly names main as the
target ref. If tests FAIL, nothing is committed — the working tree is left
as-is so the engineer can see and fix the failing state on the next cycle.

This means: no commit ever lands on `main` except through a PR you (or your
team) actually merge. The pipeline creates the branch and the PR; merging
is still a deliberate human action, and merging is exactly what triggers
the Container Apps deploy below.

**Prerequisite**: the `gh` CLI must be installed and authenticated
(`gh auth login`) in whatever shell Claude Code runs in, since the tester
uses it to open/check PRs.

## Deploying to Azure Container Apps on merge

`cd.yml` only runs when a PR into `main` is closed *and* merged
(`github.event.pull_request.merged == true`) — not on every push, not on a
closed-but-not-merged PR. It builds the image, pushes it to ACR, and
updates the Container App, authenticating via OIDC (`azure/login` with a
federated credential) rather than a stored `client-secret` — no long-lived
Azure credential sits in GitHub at all.

Setting up that OIDC trust (the AAD app registration, federated credential,
and role assignment) is identity/permission work, which is more sensitive
than spinning up a resource — the devops agent is hook-blocked from doing
it and instead writes the exact one-time setup commands into `infra.md` for
a human with the right AAD privileges to run once, along with the
non-secret repo variables (`AZURE_CLIENT_ID` etc.) to set afterward.

If specs.md's architecture doesn't target Container Apps, devops flags that
mismatch in infra.md rather than quietly building a deploy workflow that
doesn't match the infra — the reviewer should catch this as a
re-architecture rejection.

- **Check `.pipeline/` and `.claude/agents/` into version control.** That's
  the whole point of project-scoped subagents — your team gets the same
  pipeline, and `.pipeline/*.md` becomes a reviewable audit trail of how a
  feature was designed, built, tested, and approved.
- **Add `.pipeline/` output to your PR description**, not just the diff —
  reviewers on your human team get the same context the reviewer agent had.
- **Don't let the architect or engineer run destructive `az`/`bash`
  commands.** I scoped the architect's Bash use to read-only Azure CLI calls
  in its prompt; if you want that hard-enforced rather than prompt-enforced,
  add a `PreToolUse` hook on its Bash tool that blocks `az * create|delete|
  update|set` the same way `reviewer-write-guard.sh` blocks writes.
- **Consider `isolation: worktree`** on the engineer/tester agents if you
  want each pipeline run to happen in a clean git worktree rather than your
  working checkout — useful if you want to run several feature pipelines
  concurrently without them stepping on each other.
- **For genuinely long-running or parallel work** (e.g. testing multiple
  services at once), Claude Code also has an "agent teams" feature where
  teammates run as independent sessions and message each other, rather than
  a single main thread delegating and waiting. Worth a look if this grows
  past a simple linear pipeline.
