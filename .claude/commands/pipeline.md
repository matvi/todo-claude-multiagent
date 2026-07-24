---
description: Run the full architect -> engineer -> tester -> devops -> reviewer pipeline for a feature request, looping on rejection.
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
4. Delegate to the `engineer` subagent to implement the app based on
   `.pipeline/specs.md`, producing `.pipeline/changes.md`.
5. Delegate to the `tester` subagent to write and run tests based on
   `.pipeline/specs.md` and `.pipeline/changes.md`, producing
   `.pipeline/tests.md`. On PASS it will also commit, push the branch, and
   open/update a PR against `main` -- it is hook-blocked from pushing to
   `main` directly.
6. Delegate to the `devops` subagent in PROPOSE mode. If it reports nothing
   new/changed to propose, skip straight to step 8. Otherwise it produces
   `.pipeline/infra-proposal.md` and stops without touching any
   infra/pipeline files.
7. **Stop and show me the full contents of `.pipeline/infra-proposal.md`
   in this conversation, then wait for my reply before continuing.** Do not
   proceed to step 8 on your own, do not assume a default choice, and do
   not re-delegate to devops until I've responded with either approved SKU
   choices, requested changes to the options, or a rejection. This is a
   real pause in the pipeline, not a formality -- treat my response as
   required input, not a formality to skip past.
8. Delegate to the `devops` subagent in APPLY mode, passing along the SKU
   choices I approved in step 7 (or, if step 6 found nothing to propose,
   just proceed). It authors/updates IaC, the GitHub Actions CI/CD
   workflows (CD deploying to Azure Container Apps only on a merged PR into
   main), and `azure-pipelines.yml`, producing `.pipeline/infra.md`. It
   must not provision real resources or create identities/credentials --
   only author and dry-run-validate IaC and pipeline YAML.
9. Delegate to the `reviewer` subagent to read all pipeline files plus the
   actual diff and IaC/pipeline files, producing `.pipeline/review.md` with
   a verdict.
10. Report the verdict to me.
    - If APPROVED: stop, summarize what was built, confirm the PR is open,
      and tell me the exact one-time identity-setup and infra-apply commands
      from infra.md I need to run myself -- do not run them yourself. Remind
      me that merging the PR is what will trigger the Container Apps deploy.
    - If REJECTED -- needs re-architecture: go back to step 3, passing the
      reviewer's feedback to the architect.
    - If REJECTED -- needs re-implementation: go back to step 4, passing the
      reviewer's feedback to the engineer.
    - If REJECTED -- needs more tests: go back to step 5, passing the
      reviewer's feedback to the tester.
    - If REJECTED -- needs devops rework: go back to step 6, passing the
      reviewer's feedback to the devops agent (re-enter PROPOSE mode if the
      feedback implies a different SKU/service choice, otherwise go
      straight to APPLY mode with a fix).
    - If REJECTED -- security/PCI DSS issue: go back to step 4, passing the
      reviewer's specific security findings to the engineer. Treat this as
      higher priority than a normal re-implementation pass -- do not let it
      get bundled with unrelated feedback.
    - Cap this at 3 total review cycles. If still not APPROVED after 3
      cycles, stop and report the unresolved issues to me instead of looping
      forever.
