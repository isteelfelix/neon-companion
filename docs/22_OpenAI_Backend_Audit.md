# OpenAI Backend Audit

Audit date: 2026-07-26. Scope: the live non-Hermes path from provider editing and
model discovery through `ChatService`, `ChatViewModel`, `OpenAiCompatibleClient`,
and the capability adapter. The implementation remains on Chat Completions.

Sources:

- OpenAI API reference: [Chat Completions](https://platform.openai.com/docs/api-reference/chat),
  [models](https://platform.openai.com/docs/api-reference/models),
  [authentication](https://platform.openai.com/docs/api-reference/authentication),
  [errors](https://platform.openai.com/docs/guides/error-codes/api-errors), and
  [request IDs](https://platform.openai.com/docs/api-reference/debugging-requests).
- OpenAI guides: [streaming](https://platform.openai.com/docs/guides/streaming-responses),
  [function calling](https://platform.openai.com/docs/guides/function-calling), and
  [vision](https://platform.openai.com/docs/guides/images-vision).
- Reference implementation: `lidge-jun/opencodex` commit
  `0ebb8fcebab1a84698e9d75c25848fb49ffffd39`, especially
  `src/adapters/openai-responses.ts`, `src/responses/schema.ts`, and
  `src/server/responses/core.ts`. It confirms that Responses is a distinct adapter
  with its own event, reasoning, tool, error, and retry semantics rather than a
  drop-in Chat Completions payload.

## Discrepancies

| Area | Current behavior / evidence | Expected contract | Severity | Resolution |
|---|---|---|---|---|
| Function tools | Every generic request included `ToolRegistry` schemas because `GenericOpenAiAdapter.cs:17` claimed support, but `ChatViewModel.cs:188-203` only stores returned calls; it never executes them or sends `role=tool` results. | A function-calling turn is incomplete until the application executes each call and supplies matching tool output. | High | Fixed: generic HTTP providers no longer advertise or send tools. Parsing remains for backward-compatible history/response handling. Intentionally unsupported until a complete execution loop and approval contract exist. |
| Empty streaming response | `OpenAiCompatibleClient.cs:1215-1256` retried a successful stream as a new non-stream request when no text/tool call was parsed. | A successful completion is one request. Retry policy must not blindly duplicate a completed request; empty content, refusal, or an unsupported event must be handled without a second billable generation. | High | Fixed: parse the original body only, then report the unsupported empty result. No automatic replay. |
| API errors | `OpenAiCompatibleClient.cs:353-369` returned only `error.message`; HTTP status, `error.type`, `error.code`, and `x-request-id` were discarded. | OpenAI errors are structured and request IDs are the support/debug correlation key. | Medium | Fixed: surface status, type, code, and request ID without exposing credentials or raw bodies. |
| Endpoint/auth | Provider default is `https://api.openai.com/v1` (`ProvidersController.cs:456-469`); client appends `/chat/completions` and `/models` (`OpenAiCompatibleClient.cs:330-350`) and sends `Authorization: Bearer` (`:57-61`, `:1171-1173`). Full endpoint URLs ending in `/chat/completions` are normalized. | OpenAI uses HTTPS `/v1/chat/completions`, `/v1/models`, and bearer API-key auth. | Low | Conformant. Empty keys remain allowed intentionally for local OpenAI-compatible servers. |
| Model discovery/selection | `GenericOpenAiAdapter.cs:35-48` parses standard `data[].id`; `ModelDiscoveryService.cs:22-49` caches by backend, URL, and key; `ChatService.cs:2436-2477` stores the selected model per session. | `/v1/models` returns `data[].id`; the chosen model is sent on each completion. | Low | Conformant. No fake model list or hard-coded current OpenAI catalog is maintained. |
| Messages | `OpenAiCompatibleClient.cs:37-48` prepends the configured prompt as `system`; `:385-457` preserves user/assistant/tool message fields. | Chat Completions accepts system, developer, user, assistant, and tool roles. | Low | System/user/assistant transport is conformant. There is no distinct developer-prompt UI field; intentionally unsupported rather than rewriting system prompts. |
| Request parameters | `OpenAiCompatibleClient.cs:628-673` sends temperature, `max_tokens`, streaming, and `stream_options.include_usage`. Generic capabilities cannot distinguish OpenAI model families. | Parameter support is model-dependent; current reasoning models favor `max_completion_tokens` and may restrict temperature. | Medium | Unresolved by design. Changing the generic adapter would regress Ollama, LM Studio, vLLM, OpenRouter, and older configured endpoints. Add an explicit OpenAI-only adapter/model capability source before changing this. |
| Streaming lifecycle | `OpenAiCompatibleClient.cs:1117-1294` consumes `data:` SSE, `[DONE]`, text deltas, usage, cancellation, HTTP failure, and fragmented tool-call deltas. | Chat Completions streams data-only SSE chunks and a terminal marker; usage may arrive in a final usage chunk. | Medium | Text/usage/cancellation are supported. Finish reasons and refusal/reasoning metadata are not represented in local models and are intentionally not advertised. Transport-level retries are unsupported. |
| Usage/reasoning | `OpenAiCompatibleClient.cs:1951-2002` captures prompt/completion/total usage for streams; UI reads it in `ChatController`. No reasoning-token field exists. | Usage may include cached/reasoning-token detail depending on model/API. | Low | Basic totals supported. Detailed reasoning/cached-token accounting intentionally unsupported; the UI has no presentation/storage contract for it. |
| Images/files | `OpenAiCompatibleClient.cs:385-493` sends local attachments as Chat Completions `image_url` data URLs; `ChatViewModel.cs:128-145` sends only the latest user image and replaces historical images with `[image]`. | Vision-capable Chat Completions models accept image URL/data URL content parts. Arbitrary files require a different supported input/API flow. | Medium | Image input is best-effort and model-dependent. Arbitrary file input and OpenAI Files/File Search are intentionally unsupported and must not be claimed as provider capabilities. |
| Cancellation/timeout/retry | Send loops abort on cancellation (`OpenAiCompatibleClient.cs:64-72`, `:1191-1202`); model discovery has an 8-second timeout (`ModelDiscoveryService.cs:123-140`). Chat requests have no configured timeout or retry. | Clients should bound network operations and retry selected transient failures with backoff while avoiding duplicate side effects. | Medium | Cancellation supported. Timeout/retry remain unsupported because provider policy and UI configuration are absent; no guessed global timeout was added. |
| Context limits/UI flags | `contextWindow = 0` means Auto. Resolution prefers LM Studio's loaded runtime context, then structured model metadata from the provider API, then an official-OpenAI-only model catalog; a positive manual value is validated as a cap. | OpenAI model limits vary by model and change over time; `/v1/models` commonly exposes identity without the context limit. | Low | Unknown is explicit when no source resolves the model. The chat indicator no longer invents a denominator from model-name heuristics. Registry entries must stay aligned with official model pages. |

## Design decision intentionally deferred

Responses versus Chat Completions is unresolved. Companion's persisted history,
stream callbacks, usage model, and tool-call types are Chat Completions-shaped.
The reference implementation treats Responses as a separate adapter and preserves
response items, event types, reasoning state, and tool outputs. Migrating only the
URL/payload would lose contract data. Keep Chat Completions until a dedicated
OpenAI provider type and Responses-domain models are designed; generic
OpenAI-compatible providers must remain on their existing endpoint.
