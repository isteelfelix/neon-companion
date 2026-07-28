#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

cs_files=(
  Assets/Scripts/Runtime/Api/Hermes/HermesGateway.cs
  Assets/Scripts/Runtime/Api/Hermes/HermesSessionManager.cs
  Assets/Scripts/Runtime/UI/UITK/Chat/ChatInputManager.cs
  Assets/Scripts/Runtime/UI/UITK/ChatController.cs
)

if grep -nE '=\s*[A-Za-z0-9_.\)]+\s+switch\s*\{|\bis\s+not\s+(null|[A-Z(\{])|\bis\s+null\b|(=|\breturn|\(|,)\s*new\s*\(' "${cs_files[@]}"; then
  echo "Unsupported C# syntax found." >&2
  exit 1
fi

jq empty Assets/Resources/Localization/en.json Assets/Resources/Localization/ru.json
git diff --check
test -z "$(git diff --name-only -- '*.meta')"

input=Assets/Scripts/Runtime/UI/UITK/Chat/ChatInputManager.cs
manager=Assets/Scripts/Runtime/Api/Hermes/HermesSessionManager.cs

for command in model help clear new system temp tokens; do
  grep -q "case \"/$command\":" "$input"
done

grep -q '!chat.IsHermesActive' "$input"
grep -q 'ExecuteSlashCommandAsync(sessionId, commandText)' "$input"
grep -q 'DispatchCommandAsync(sessionId, name, args)' "$input"
grep -q 'case "send":' "$input"
grep -q 'case "prefill":' "$input"
grep -q '_showSystemMessage?.Invoke' "$input"
grep -q 'RpcMethods.SlashExec' "$manager"
grep -q 'RpcMethods.CommandDispatch' "$manager"

echo "Hermes slash command static checks passed."
