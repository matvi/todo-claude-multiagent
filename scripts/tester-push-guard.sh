#!/bin/bash
# Blocks the tester agent from pushing directly to main/master.
# All work happens on a feature branch; main only changes via a reviewed,
# merged PR (which is also what triggers the CD deploy workflow).
# Wired up as a PreToolUse hook on the Bash tool in tester.md.

INPUT=$(cat)
COMMAND=$(echo "$INPUT" | jq -r '.tool_input.command // empty')

if [ -z "$COMMAND" ]; then
  exit 0
fi

if echo "$COMMAND" | grep -qE '\bgit[[:space:]]+push\b'; then
  CURRENT_BRANCH=$(git branch --show-current 2>/dev/null)

  if [ "$CURRENT_BRANCH" = "main" ] || [ "$CURRENT_BRANCH" = "master" ]; then
    echo "Blocked: currently on '$CURRENT_BRANCH' -- tester agent may not push directly to main/master. Work happens on a feature branch; main only changes via a merged PR." >&2
    exit 2
  fi

  # Also catch an explicit main/master target even if HEAD is elsewhere,
  # e.g. `git push origin HEAD:main` or `git push origin some-branch:main`.
  if echo "$COMMAND" | grep -qE '(:|[[:space:]])(main|master)([[:space:]]|$)'; then
    echo "Blocked: push command explicitly targets main/master ($COMMAND). Push to the feature branch instead." >&2
    exit 2
  fi
fi

exit 0
