#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

client=Assets/Scripts/Runtime/Api/OpenAiCompatibleClient.cs
view_model=Assets/Scripts/Runtime/UI/Chat/ChatViewModel.cs
chat_service=Assets/Scripts/Runtime/Chat/ChatService.cs

grep -q 'BuildResponsesPayloadJson(request, false, omitTemperature)' "$client"
grep -q 'BuildResponsesPayloadJson(request, true, omitTemperature)' "$client"
test "$(grep -c 'BuildResponsesPayloadJson(' "$client")" -eq 3
test "$(grep -c 'sb.Append(",\\"temperature\\":")' "$client")" -eq 1
grep -B1 'sb.Append(",\\"temperature\\":")' "$client" | grep -q 'if (!omitTemperature)'
grep -q 'capabilities.RequiresTemperatureOmission' "$client"
grep -q 'webRequest.responseCode != 400' "$client"
grep -q 'error.param, "temperature"' "$client"
grep -q '!emittedAnyToken && ShouldRetryWithoutTemperature' "$client"

grep -q 'Responses selects its model per request' "$client"
grep -q 'Task.FromResult(new ModelSwitchResult(true, targetModel.Trim()' "$client"
grep -q 'requestMessages.Add(new AiChatMessage' "$view_model"
grep -q 'model = string.IsNullOrWhiteSpace(SelectedModel) ? _provider.defaultModel : SelectedModel' "$view_model"
grep -q '_currentChatViewModel.SelectedModel = requestedModel' "$chat_service"
grep -q '_currentSession.selectedModel = requestedModel' "$chat_service"
grep -q '_currentSession.messages = new List<ChatMessage>(_currentChatViewModel.Messages)' "$chat_service"
grep -q '_sessionRepository.SaveAll(sessions)' "$chat_service"

if grep -nE '=\s*[A-Za-z0-9_.\)]+\s+switch\s*\{|\bis\s+not\s+(null|[A-Z(\{])|\bis\s+null\b|(=|\breturn|\(|,)\s*new\s*\(' "$client"; then
  echo "Unsupported C# syntax found." >&2
  exit 1
fi

git diff --check
test -z "$(git diff --name-only -- '*.meta')"

echo "Responses temperature and model-switch static checks passed."
