---
description: Run the full architect -> devops(infra) -> engineer -> tester -> devops(CI/CD) -> reviewer pipeline for a feature request, looping on rejection.
---

Run the software delivery pipeline for this request: $ARGUMENTS

Follow this exact sequence. Do not skip or reorder steps. Do not implement
anything yourself in the main conversation -- every step must be delegated to
the named subagent so each runs in its own isolated context.

1. Ensure `.pipeline/` exists in the project root (create it if not).
2. Create and check out a feature branch off the latest `main`, named
   something like `pipeline/<short-slug-of-the-request>`. All agent work in
   this run happens on this branch -- nothing is ever committed directly to
   `main`. If already on a non-main branch that looks like a prior run of
   this same request, reuse it instead of creating a new one.
3. Delegate to the `architect` subagent to produce `.pipeline/specs.md`
   (it will also read/update its persistent `.pipeline/architecture-memory.md`).
4. Delegate to the `devops` subagent in **INFRA-PROPOSE** mode. If it reports
   nothing new/changed to propose, skip straight to step 7. Otherwise it
   produces `.pipeline/infra-proposal.md` and stops without touching any
   infra files.
5. **Stop and show me the full contents of `.pipeline/infra-proposal.md`
   in this conversation, then wait for my reply before continuing.** Do not
   proceed to step 6 on your own, do not assume a default choice, and do
   not re-delegate to devops until I've responded with either approved SKU
   choices, requested changes to the options, or a rejection. This is a
   real pause in the pipeline, not a formality -- treat my response as
   required input.
6. Delegate to the `devops` subagent in **INFRA-APPLY** mode, passing along
   the SKU choices I approved in step 5 (or, if step 4 found nothing to
   propose, just proceed). It authors/updates IaC only (no CI/CD yet) and
   validates it via dry-run, producing a partial `.pipeline/infra.md`. Show
   me the one-time identity-setup and infra-apply commands it reports --
   these are mine to run if I want real resources available while the
   engineer works, but I decide that, you don't chase it.
7. Delegate to the `engineer` subagent to implement the app based on
   `.pipeline/specs.md`, producing `.pipeline/changes.md`.
8. Delegate to the `tester` subagent to write and run tests based on
   `.pipeline/specs.md` and `.pipeline/changes.md`, producing
   `.pipeline/tests.md`. On PASS it will also commit, push the branch, and
   open/update a PR against `main` -- it is hook-blocked from pushing to
   `main` directly.
9. Delegate to the `devops` subagent in **PIPELINE-APPLY** mode. It reads
   the real build/lint/test commands from changes.md/tests.md, authors the
   GitHub Actions CI/CD workflows (CD deploying to Azure Container Apps only
   on a merged PR into main) and `azure-pipelines.yml`, checks branch
   protection on `main`, and finalizes `.pipeline/infra.md` (filling in the
   CI/CD section on top of what INFRA-APPLY already wrote). It must not
   provision real resources or create identities/credentials -- only author
   and dry-run-validate pipeline YAML.
10. Delegate to the `reviewer` subagent to read all pipeline files plus the
    actual diff and IaC/pipeline files, producing `.pipeline/review.md` with
    a verdict.
11. Report the verdict to me.
    - If APPROVED: stop, summarize what was built, confirm the PR is open,
      and tell me the exact one-time identity-setup and infra-apply commands
      from infra.md I need to run myself if I haven't already -- do not run
      them yourself. Remind me that merging the PR is what will trigger the
      Container Apps deploy.
    - If REJECTED -- needs re-architecture: go back to step 3, passing the
      reviewer's feedback to the architect. Since specs.md may now call for
      different/changed services, re-enter step 4 (INFRA-PROPOSE) afterward
      rather than jumping straight to the engineer.
    - If REJECTED -- needs re-implementation: go back to step 7, passing the
      reviewer's feedback to the engineer. After tester passes again, re-run
      step 9 (devops PIPELINE-APPLY) since build/test commands may have
      changed, then step 10.
    - If REJECTED -- needs more tests: go back to step 8, passing the
      reviewer's feedback to the tester. After it passes, re-run step 9
      (devops PIPELINE-APPLY), then step 10.
    - If REJECTED -- needs devops rework: figure out which pass needs the
      rework from the reviewer's feedback. If it's about resources/SKU/IaC,
      re-enter step 4 (INFRA-PROPOSE, or step 6 directly if the SKU choice
      itself isn't in question). If it's about CI/CD workflows or branch
      protection, re-enter step 9 (PIPELINE-APPLY) directly.
    - If REJECTED -- security/PCI DSS issue: go back to step 7, passing the
      reviewer's specific security findings to the engineer. Treat this as
      higher priority than a normal re-implementation pass -- do not let it
      get bundled with unrelated feedback. After tester passes again, re-run
      step 9, then step 10.
    - Cap this at 3 total review cycles. If still not APPROVED after 3
      cycles, stop and report the unresolved issues to me instead of looping
      forever.
