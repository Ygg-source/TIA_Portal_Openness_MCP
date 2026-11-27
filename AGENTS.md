# Repository Instructions

## Pull request review and automatic merge

- Every pull request targeting `master` is eligible for automatic merge. Do not require the user to approve individual PRs, and do not create risk-category exceptions that require manual approval.
- Use GitHub Auto-merge with **Squash** as the only intended merge method. Never bypass branch protection or use an immediate merge to work around unmet checks.
- Before enabling Auto-merge, ensure the official Codex Code Review has completed and has no unresolved P0/P1 findings; if automatic review is not enabled, request it with `@codex review` and wait for the result.
- Perform a separate Codex agent review of the complete PR diff, repository conventions, security implications, compatibility, and validation evidence. Do not treat the official review as a substitute for project-specific verification.
- Wait for all applicable GitHub Actions checks to pass: `offline-tests`, `dead-tool-references`, and `validate-bundle`. A failed, missing, or pending required check blocks auto-merge.
- For changes involving Openness, tool registration, TIA project read/write, or runtime behavior, run the relevant local TIA Portal V20 build and regression checks. GitHub-hosted runners do not have TIA Portal or `Siemens.Engineering.dll` and cannot replace this local validation. Skip unrelated V20 checks for documentation-only changes.
- Once reviews and validations pass, enable GitHub Auto-merge using Squash. Confirm GitHub reports the PR as merged before cleaning up its branch. If auto-merge is unavailable or permissions/configuration block it, report the blocker; do not bypass protections.
- If review findings require code changes, fix them and rerun affected tests. Request another official review only after substantive code changes to avoid unnecessary review usage.

## Branch and remote safety

- `master` is the stable integration branch. Develop on short-lived `feature/*`, `fix/*`, or explicitly requested `sync/upstream-*` branches and submit PRs to `master`.
- Push only to the user's Fork remote `origin`. Never push to `upstream`.
- Upstream access and synchronization are strictly user-triggered. Do not fetch, inspect, compare, merge, rebase, cherry-pick, or otherwise access `upstream` unless the user explicitly asks in the current task or performs the operation manually.
- Even when the user explicitly requests upstream synchronization, use a dedicated `sync/upstream-*` branch and the same review, validation, and automatic-merge gates. Never push directly to `master`.
- Never force-push, reset shared branches, bypass required checks, or delete files to resolve conflicts without establishing the correct resolution.

## TIA version baseline

- TIA Portal V20 is the supported build and regression baseline. V21 is out of scope unless the user requests it.