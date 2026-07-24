#!/bin/bash
# Blocks the reviewer subagent from writing anywhere except .pipeline/review.md
# Wired up as a PreToolUse hook on the Write/Edit tools in reviewer.md.

INPUT=$(cat)
FILE_PATH=$(echo "$INPUT" | jq -r '.tool_input.file_path // empty')

if [ -z "$FILE_PATH" ]; then
  exit 0
fi

if [[ "$FILE_PATH" != *".pipeline/review.md" ]]; then
  echo "Blocked: reviewer agent is read-only except for .pipeline/review.md (attempted: $FILE_PATH)" >&2
  exit 2
fi

exit 0
