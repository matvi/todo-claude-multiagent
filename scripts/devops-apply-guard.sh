#!/bin/bash
# Blocks the devops agent from actually provisioning real Azure resources.
# Allows read-only / dry-run commands (validate, what-if, plan) but blocks
# anything that would create, modify, or delete real cloud resources.
# Wired up as a PreToolUse hook on the Bash tool in devops.md.

INPUT=$(cat)
COMMAND=$(echo "$INPUT" | jq -r '.tool_input.command // empty')

if [ -z "$COMMAND" ]; then
  exit 0
fi

# Block real Azure provisioning/mutation commands.
if echo "$COMMAND" | grep -qiE '\baz\s+(deployment(\s+group)?\s+create|group\s+create|group\s+delete|resource\s+(create|delete)|deployment\s+sub\s+create)\b'; then
  echo "Blocked: devops agent may not run commands that provision or modify real Azure resources ($COMMAND). Use 'az deployment group validate' or 'az deployment group what-if' instead, and leave actual apply to a human." >&2
  exit 2
fi

# Block identity/permission-granting commands -- more sensitive than
# resource provisioning, since these create standing credentials/access.
if echo "$COMMAND" | grep -qiE '\baz\s+(ad\s+app\s+create|ad\s+app\s+federated-credential\s+create|ad\s+sp\s+create|ad\s+sp\s+create-for-rbac|role\s+assignment\s+create)\b'; then
  echo "Blocked: devops agent may not create AAD apps, service principals, federated credentials, or role assignments ($COMMAND). Document the exact commands needed in infra.md for a human to run -- identity/permission grants are not something an agent should self-provision." >&2
  exit 2
fi

# Block terraform apply/destroy (plan is fine).
if echo "$COMMAND" | grep -qiE '\bterraform\s+(apply|destroy)\b'; then
  echo "Blocked: devops agent may not run 'terraform apply/destroy' ($COMMAND). Use 'terraform plan' instead." >&2
  exit 2
fi

exit 0
