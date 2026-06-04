# Neon Companion — Refactoring Plan: Oversized Files

**Generated:** 2026-06-03  
**Scope:** Six files totalling ~16K lines of actionable logic. Plan only — no code changed.

---

## Orientation

### Sub-controllers that already exist (MainViewController delegates to these)

| Controller | File |
|---|---|
| NavigationController | `UI/UITK/NavigationController.cs` |
| LayoutController | `UI/UITK/LayoutController.cs` |
| SessionHistoryController | `UI/UITK/SessionHistoryController.cs` |
| VoiceController | `UI/UITK/VoiceController.cs` |
| SettingsController | `UI/UITK/SettingsController.cs` |
| ProvidersController | `UI/UITK/ProvidersController.cs` |
| AvatarGalleryController | `UI/UITK/AvatarGalleryController.cs` |

MainViewController is already well-decomposed at the top level; its remaining bulk comes from avatar-motion wiring and model-picker bootstrapping that hasn't yet been pushed down.

### Inner-class locations (no cross-file duplication found)

| Type | Canonical home |
|---|---|
| `ChatCompletionRequest`, `RequestRoutingInfo`, `SseParseState`, `StreamingToolCallAccumulator`, `ContentArrayParseResult` | `OpenAiCompatibleClient.cs` |
| `CodeSeg` | `SelectableMarkdownElement.cs` |
| `QueuedMessage`, `ClipboardImageData` | `ChatController.cs` (unique) |

---

## Priority Order and Rationale

| # | File | Why this order |
|---|---|---|
| 1 | **ChatController.cs** | User-designated most urgent; biggest file; all new sub-classes can be private to the same assembly so public API stays frozen |
| 2 | **SelectableMarkdownElement.cs** | Fully self-contained (no outbound UI deps), safest extraction; splitting it first makes `ChatController`'s rendering logic easier to hand off |
| 3 | **OpenAiCompatibleClient.cs** | Pure data/HTTP layer, zero UI deps; clean boundaries make it safe to extract without touching callers |
| 4 | **ProvidersController.cs** | Isolated panel; split after the HTTP client it calls is clean |
| 5 | **AvatarGalleryController.cs** | Isolated panel; split after enums stabilise between it and MainViewController |
| 6 | **MainViewController.cs** | Orchestrator — split last, once every sub-controller it delegates to is already clean |

---

## 1. ChatController.cs → 9 extracted classes

`ChatController` keeps its existing public interface exactly: `SetDeps`, `SetVoiceRecording`, `SetChatSubtitle`, `SetSessionSearchQuery`, `ShowSystemMessage`, `RegisterCallbacks`, `UnregisterCallbacks`, `InitState`, `SendCurrentMessageAsync`, and the four properties. All new classes live in the same namespace and are `internal`.

### 1.1 `ChatInputManager`
**File:** `UI/UITK/Chat/ChatInputManager.cs`

**Responsibility:** Everything about the composer text field — no network, no scroll.

| Method to move | Notes |
|---|---|
| `HandleEnterKey` | |
| `AdjustComposerHeight` | |
| `OnMessageInput` (event handler) | |
| `HandleSlashCommand` and all `/model`, `/help`, `/clear`, `/new`, `/system`, `/temp`, `/tokens` branches | |
| `SetVoiceRecording` | delegates to this manager |
| `BuildAttachmentTokens` | token-string build only; preview management stays in AttachmentManager |

**Exposes to ChatController:**
```csharp
internal string CurrentText { get; }
internal void Clear();
internal void SetFocus();
internal event Action<string> OnSubmit;   // fired when user confirms send
internal event Action<SlashCommand> OnCommand;
```

**Watch out for:** Height-adjustment callbacks reach into the ScrollView; pass `Action<float> onHeightChanged` at construction.

---

### 1.2 `ChatAttachmentManager`
**File:** `UI/UITK/Chat/ChatAttachmentManager.cs`

**Responsibility:** All file attachment surface — drag-and-drop, file picker, clipboard image, preview strip.

| Method to move | Notes |
|---|---|
| `OnFileDrop`, `OnDragEnter`, `OnDragLeave` | IFileDropService wiring |
| `OpenFilePicker` | IFilePickerService call |
| `ShowAttachmentPreview`, `RemoveAttachment`, `ClearAttachments` | |
| Clipboard image extraction (`ClipboardImageData` inner class moves here) | Windows `#if` block |
| `BuildAttachmentPayload` | produces the list of `MessageAttachment` to add to the request |

**Exposes to ChatController:**
```csharp
internal IReadOnlyList<MessageAttachment> CurrentAttachments { get; }
internal void Clear();
internal event Action OnAttachmentsChanged;
```

**Watch out for:** `ClipboardImageData` is `#if UNITY_STANDALONE_WIN` — keep the conditional compile directive intact in the new file.

---

### 1.3 `ChatStreamingCoordinator`
**File:** `UI/UITK/Chat/ChatStreamingCoordinator.cs`

**Responsibility:** Everything that happens _while_ a response streams in.

| Method to move | Notes |
|---|---|
| `AddStreamingBubble` / `RemoveStreamingBubble` | |
| `OnToken` callback passed to client | accumulation logic |
| `UpdateStatsLabel` / `BuildStatsText` | token-count footer |
| `StartTypingDots` / `StopTypingDots` | inline animator |
| `IsStreamingResponse` property | re-expose as delegate from `ChatController` |

**Exposes to ChatController:**
```csharp
internal bool IsStreaming { get; }
internal Task StreamAsync(Func<Action<string>, Task> streamFn, CancellationToken ct);
internal void Abort();
```

**Watch out for:** Typing dots animate via `IVisualElementScheduler` — inject the `VisualElement` root at construction, not at call time.

---

### 1.4 `ChatSearchController`
**File:** `UI/UITK/Chat/ChatSearchController.cs`

**Responsibility:** In-session search bar and match navigation.

| Method to move | Notes |
|---|---|
| `CreateSearchBar` / `BuildSearchOverlay` | |
| `FindMatches`, `HighlightMatches`, `ClearHighlights` | |
| `NavigateToNextMatch`, `NavigateToPreviousMatch` | |
| `ScrollToMatch` | |
| `SetSessionSearchQuery` | becomes a pass-through on `ChatController` |

**Exposes to ChatController:**
```csharp
internal void SetQuery(string query);
internal void Show();
internal void Hide();
```

**Watch out for:** Highlight overlays reference the same `SelectableMarkdownElement` instances rendered by `ChatMessageListRenderer` — both classes need a shared reference to the message container VisualElement, not copies.

---

### 1.5 `ChatMessageEditController`
**File:** `UI/UITK/Chat/ChatMessageEditController.cs`

**Responsibility:** Inline edit mode for individual messages.

| Method to move | Notes |
|---|---|
| `BeginEditMessage(messageId)` | |
| `CommitEdit` | triggers re-send logic via event |
| `CancelEdit` | |
| `ShowEditOverlay`, `HideEditOverlay` | |
| Edit `TextField` setup and save/cancel button wiring | |

**Exposes to ChatController:**
```csharp
internal event Action<string, string> OnEditCommitted; // (messageId, newText)
internal bool IsEditing { get; }
```

---

### 1.6 `ChatSelectionManager`
**File:** `UI/UITK/Chat/ChatSelectionManager.cs`

**Responsibility:** Multi-select mode and bulk actions (U-31/U-32).

| Method to move | Notes |
|---|---|
| `EnterSelectionMode` / `ExitSelectionMode` | |
| `ToggleMessageSelection(messageId)` | |
| `GetSelectedMessages` | |
| `DeleteSelectedMessages` | fires event; ChatController drives the actual service call |
| `ForwardSelectedMessages` | |
| Selection count label update | |

**Exposes to ChatController:**
```csharp
internal bool IsSelecting { get; }
internal IReadOnlyList<string> SelectedIds { get; }
internal event Action<IReadOnlyList<string>> OnBulkDelete;
internal event Action<IReadOnlyList<string>> OnBulkForward;
```

---

### 1.7 `ToolCallApprovalController`
**File:** `UI/UITK/Chat/ToolCallApprovalController.cs`

**Responsibility:** Tool-call approval prompts and progress display.

| Method to move | Notes |
|---|---|
| `ShowApprovalPrompt`, `HideApprovalPrompt` | |
| `ApproveToolCall`, `RejectToolCall` | event-driven; actual execution stays in streaming coordinator |
| `ShowToolProgress`, `UpdateToolProgress` | |
| `IsStreamingToolCall` detection helper | |
| `RequestRoutingInfo` usage / wrapping | the type itself stays in OpenAiCompatibleClient |

**Exposes to ChatController:**
```csharp
internal event Action<string> OnApproved;   // toolCallId
internal event Action<string> OnRejected;
internal void ShowProgress(string toolName, string status);
internal void HideProgress();
```

**Watch out for:** Approval prompts block streaming; the coordinator must hold the continuation `TaskCompletionSource` and this class signals it.

---

### 1.8 `ChatNotificationManager`
**File:** `UI/UITK/Chat/ChatNotificationManager.cs`

**Responsibility:** Notification badge, sounds, unread tracking.

| Method to move | Notes |
|---|---|
| `ShowBadge`, `HideBadge`, `UpdateBadgeCount` | |
| `PlayNotificationSound` | |
| `IsUnread`, `MarkAsRead` | |

**Exposes to ChatController:**
```csharp
internal void NotifyNewMessage();
internal void MarkRead();
internal bool HasUnread { get; }
```

---

### 1.9 `ChatMessageListRenderer`
**File:** `UI/UITK/Chat/ChatMessageListRenderer.cs`

**Responsibility:** ScrollView population, bubble construction, context menus.

| Method to move | Notes |
|---|---|
| `RenderMessages` | main render callback |
| `BuildMessageBubble(message)` | per-message element factory |
| `ScrollToBottom`, `ScrollToMessage(messageId)` | |
| `ShowMessageContextMenu` | |
| `BuildContextMenuItems` and all `OnContextMenu*` handlers | |

**Exposes to ChatController:**
```csharp
internal void Render(IReadOnlyList<AiChatMessage> messages);
internal void ScrollToBottom();
internal event Action<string> OnMessageContextMenuDelete;
internal event Action<string> OnMessageContextMenuCopy;
// etc.
```

**Watch out for:** `SelectableMarkdownElement` instances are created here and also referenced by `ChatSearchController` for highlight overlays — surface the container `VisualElement` rather than individual element references.

---

### Data model extractions (ChatController)

| Type | New file | Notes |
|---|---|---|
| `QueuedMessage` | `Models/Chat/QueuedMessage.cs` | Simple DTO, no logic |
| `ClipboardImageData` | Inline in `ChatAttachmentManager.cs` | Keep `#if UNITY_STANDALONE_WIN` guard |

---

## 2. SelectableMarkdownElement.cs → 3 extracted classes

`SelectableMarkdownElement` retains its public API: `SetMarkdown(string)`, `SetDiff(string)`, `PlainText`, `StripAnsi(string)`. All extracted classes are `internal`.

### 2.1 `MarkdownParser`
**File:** `UI/UITK/Markdown/MarkdownParser.cs`

**Responsibility:** Pure parsing — no VisualElements, no Unity API calls.

| Method to move |
|---|
| `ParseMarkdown` |
| `ParseInline` |
| `ParseTable` |
| `ParseDiff` |
| `ReconcileBlocks` |
| `FlushParagraph` |
| `FinalizeBlock` |
| All `Is*` and `Try*` helpers: `GetHeadingLevel`, `TryParseListItem`, `IsHorizontalRule`, `IsPotentialTableStart`, `IsTableSeparatorRow`, `NormalizeTableLine`, `ParseTableCells`, `UnescapeMarkdownSyntax`, `GetDiffLineStyle`, `IsDiffLanguage`, `IsHighlightLanguage` |

**Model types that move with it** (currently inner classes, now top-level in same file or `Models/Markdown/`):
- `BlockKind` (enum)
- `InlineRun`
- `TableRow`
- `Block`

**Exposes:**
```csharp
internal static List<Block> Parse(string markdown);
internal static List<Block> ParseDiff(string text);
internal static List<Block> Reconcile(List<Block> oldBlocks, List<Block> newBlocks);
```

**Watch out for:** `ReconcileBlocks` compares block signatures — the signature algorithm must be stable across the split; keep it in `Block` itself.

---

### 2.2 `MarkdownBlockBuilder`
**File:** `UI/UITK/Markdown/MarkdownBlockBuilder.cs`

**Responsibility:** Convert `Block` list → Unity `VisualElement` tree. All UITK construction lives here.

| Method to move |
|---|
| `BuildBlockElement` |
| `BuildInlineContainer` |
| `BuildListItem` |
| `BuildCodeBlock` |
| `BuildRule` |
| `BuildTable` |
| `BuildParagraph` |
| `BuildDiffLine` |
| `AddInlineTokens` |
| `AddWordTokens` |
| `EmitWord` |
| `EmitCodeChunks` |
| `AddToken` |

**Model types that move with it:**
- `PlacedToken`
- `VisualTextLine`

**Exposes:**
```csharp
internal List<PlacedToken> Build(List<Block> blocks, VisualElement container);
```

**Watch out for:** `AddToken` creates `Label` elements that `SelectableMarkdownElement` later traverses for geometry — the `PlacedToken` list is the shared contract between builder and selection logic. Do not let either side hold direct element references beyond what `PlacedToken` exposes.

---

### 2.3 `SyntaxHighlighter`
**File:** `UI/UITK/Markdown/SyntaxHighlighter.cs`

**Responsibility:** Lightweight syntax coloring and diff styling. No UITK dependency — pure string → `CodeSeg[]`.

| Method to move |
|---|
| `HighlightLine` |
| All keyword/string/number/comment detection helpers |
| `GetDiffLineStyle` (move from MarkdownParser if it produces colours) |

**Model types:**
- `CodeSeg` struct

**Exposes:**
```csharp
internal static CodeSeg[] Highlight(string line, string language);
```

**Watch out for:** `SelectableMarkdownElement` currently calls `HighlightLine` directly from `BuildCodeBlock`. After the split, `MarkdownBlockBuilder` calls `SyntaxHighlighter.Highlight` and applies the resulting `CodeSeg[]` colors to labels.

---

## 3. OpenAiCompatibleClient.cs → 4 extracted classes

Public API (`SendMessageAsync`, `SendMessageStreamAsync`, `TestConnectionAsync`, `ApplySessionModelAsync`, `LastStreamUsage`, `StripAnsi`) stays on `OpenAiCompatibleClient`.

### 3.1 `ChatPayloadBuilder`
**File:** `Api/ChatPayloadBuilder.cs`

**Responsibility:** JSON serialization of outgoing requests — no HTTP, no parsing.

| Method to move |
|---|
| `AppendMessagesJson` |
| Image attachment Base64 encoding helpers |
| Any method that writes JSON strings for requests |

**Model types:**
- `ChatCompletionRequest` (stays here as a plain `[Serializable]` DTO)

**Exposes:**
```csharp
internal static string Build(ChatCompletionRequest request);
```

---

### 3.2 `SseStreamParser`
**File:** `Api/SseStreamParser.cs`

**Responsibility:** SSE framing — split byte stream into `data:` lines, reassemble partial chunks.

| Method to move |
|---|
| SSE line-parsing loop currently inside `SendMessageStreamAsync` |
| Chunk reassembly and `SseParseState` machine |

**Model types:**
- `SseParseState` (moves here)

**Exposes:**
```csharp
internal static IEnumerable<string> ParseLines(string rawChunk, SseParseState state);
```

**Watch out for:** `SseParseState` is mutable across calls (carries partial-line buffer) — expose it as an `out` parameter or wrap it in the parser class as instance state.

---

### 3.3 `OpenAiResponseParser`
**File:** `Api/OpenAiResponseParser.cs`

**Responsibility:** Deserialize non-streaming responses and extract tool calls from streaming deltas.

| Method to move |
|---|
| `ExtractContentFromStreamingPayload` |
| `ExtractToolCalls` |
| Non-streaming JSON extraction from `SendMessageAsync` response body |

**Model types that move with it:**
- `OpenAiResponseEnvelope`
- `OpenAiChoice`
- `OpenAiMessage`
- `OpenAiToolCall`
- `OpenAiToolCallFunction`
- `OpenAiError`
- `OpenAiErrorResponse`
- `ContentArrayParseResult`
- `StreamingToolCallAccumulator` (inner class `ToolCallState` stays nested here)

**Exposes:**
```csharp
internal static string ExtractContent(string json);
internal static List<OpenAiToolCall> ExtractToolCalls(string json);
internal static OpenAiResponseEnvelope Deserialize(string json);
```

---

### 3.4 `OpenAiCompatibleClient` (residual core)
Keeps: HTTP transport via `UnityWebRequest`, `TestConnectionAsync`, high-level `SendMessage*` orchestration, `ApplySessionModelAsync`. Delegates JSON building to `ChatPayloadBuilder`, SSE parsing to `SseStreamParser`, response parsing to `OpenAiResponseParser`.

**Watch out for:** `StripAnsi` is currently on this class but also copied into `SelectableMarkdownElement`. After the SelectableMarkdownElement split, consolidate in a single static `TextUtility.cs` and have both callers reference it.

---

## 4. ProvidersController.cs → 3 extracted classes

### 4.1 `ModelPickerController`
**File:** `UI/UITK/Providers/ModelPickerController.cs`

| Method to move |
|---|
| Model picker overlay creation and show/hide |
| Model grouping by provider |
| Model group collapse/expand |
| Current model highlighting |
| `OpenModelPickerAsync`, `ApplyModelSelectionAsync` |
| `ShowTopbarModelPicker`, `HideTopbarModelPicker` |

**Exposes:**
```csharp
internal Task<string> PickModelAsync(CancellationToken ct);
internal void SetCurrentModel(string modelId, string providerName);
```

---

### 4.2 `ProviderConnectionTester`
**File:** `UI/UITK/Providers/ProviderConnectionTester.cs`

| Method to move |
|---|
| HTTP connection test, Hermes WebSocket test |
| Latency measurement and display |
| Model discovery on URL change |
| `UpdateEditorStatus` (the status-badge logic) |

**Exposes:**
```csharp
internal Task<ConnectionTestResult> TestAsync(ProviderConfig provider, CancellationToken ct);
internal void ShowResult(ConnectionTestResult result, VisualElement statusEl);
```

---

### 4.3 `ProvidersController` (residual core)
Keeps: provider list rendering, CRUD, activation/enable toggle, backend mode switch, import/export, unsaved-changes guard, `CanLeaveProviderEditor`, `HasUnsavedChanges`, all static helpers (`CloneProvider`, `BuildProviderShort`, `BuildModelPresets`).

**Watch out for:** `BuildModelPresets` is referenced by both the editor form and the picker — keep it on the core class as `internal static`.

---

## 5. AvatarGalleryController.cs → 4 extracted classes

**Note:** `AvatarMotionState` and `AvatarViewMode` enums appear in both `AvatarGalleryController` and `MainViewController`. Consolidate them in a single `AvatarTypes.cs` file before any split begins. Both controllers import from there.

### 5.1 `AvatarCustomizationController`
**File:** `UI/UITK/Avatar/AvatarCustomizationController.cs`

| Method to move |
|---|
| Color picker (primary/secondary/halo) |
| Saturation, brightness, halo intensity sliders |
| Emoji overlay picker |
| Frame selection |
| Any `Apply*` method that mutates `AvatarCustomizationData` |

---

### 5.2 `AvatarUploadController`
**File:** `UI/UITK/Avatar/AvatarUploadController.cs`

| Method to move |
|---|
| File picker (`IFilePickerService`) |
| Crop editor launch (`AvatarCropEditor`) |
| Bake pipeline (`AvatarCropBaker`) |
| Avatar transform persistence |
| Custom avatar deletion and texture cleanup |

---

### 5.3 `AvatarAnimationController`
**File:** `UI/UITK/Avatar/AvatarAnimationController.cs`

| Method to move |
|---|
| 2D sprite sheet animator setup (`SpriteSheetAnimator`) |
| 3D renderer setup (`Avatar3DRenderer`, `Avatar3DService`, `Avatar3DLoader`) |
| 3D model parent hierarchy and transform management |
| `SetAvatarMotionState`, `RefreshAvatarMotionState` |
| `TriggerAvatarSmile`, `TriggerAvatarConfused` |
| `StartTypingAnimation`, `StopTypingAnimation` |
| `GetAvatarAnimatorInstance` |
| `SetAvatarViewModeFromSetting`, `AvatarViewModeSetting` |

---

### 5.4 `PersonaEditorController`
**File:** `UI/UITK/Avatar/PersonaEditorController.cs`

| Method to move |
|---|
| Persona editor form (name, system-prompt fields) |
| `UpdatePersonaStateUi` |
| Persona save/reset |
| Override detection (built-in vs. custom fallback) |
| `AvatarPersonaText`, `AvatarDisplayName`, `AvatarStyleTag` helpers |

---

### 5.5 `AvatarGalleryController` (residual core)
Keeps: gallery grid and tile rendering, filter chips (`AvatarFilter` enum), `ApplyAvatarFilter`, `SyncGallerySelection`, `RefreshBuiltInAvatarTileLabels`, `RefreshCustomAvatarGallery`, `GetAvatarTotalCount`, `UpdateAvatarFilterCounts`, `UpdateAvatarActionButtons`.

---

## 6. MainViewController.cs — defer until others are done

The file already delegates to the sub-controllers listed above. After all five files above are split:

- Move `BuiltInAvatarMeta` static dictionary (8 entries) → `AvatarGalleryController` (it belongs there)
- Move `ModelPreset` struct → `ProvidersController` or `ChatPayloadBuilder`
- Move `AvatarMotionState` / `AvatarViewMode` enums → `AvatarTypes.cs` (prerequisite above)
- The remaining `MainViewController` bulk is mostly initialization and event-wiring boilerplate; a `MainViewBootstrapper` split may help but is lower priority

---

## Cross-cutting: `.meta` file requirement

Every new `.cs` file needs a `.meta` sibling with a freshly generated GUID. Script to generate at commit time (bash):

```bash
python3 -c "import uuid; print(uuid.uuid4().hex)"
```

Template:
```yaml
fileFormatVersion: 2
guid: <32-hex-char UUID without dashes>
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData:
  assetBundleVariant:
```

---

## Risk register

| Risk | Mitigation |
|---|---|
| `ChatController` public API broken by split | All extracted classes are `internal`; `ChatController` keeps every existing public member as a thin delegate |
| `MarkdownParser` / `MarkdownBlockBuilder` coupling through `PlacedToken` | Define `PlacedToken` as a standalone class in `Models/Markdown/` before splitting either |
| `AvatarMotionState` enum defined in two places | Create `AvatarTypes.cs` before splitting `AvatarGalleryController` or `MainViewController` |
| `.meta` GUID collisions | Generate fresh GUIDs; never copy-paste from another `.meta` |
| `StripAnsi` duplicated across `SelectableMarkdownElement` and `OpenAiCompatibleClient` | Consolidate in `TextUtility.cs` as step 0 of SelectableMarkdownElement split |
| `SseParseState` is mutable reference across loop iterations | Verify the state machine is instance-safe before extracting `SseStreamParser`; wrap in a class not a struct |
| Unity asset database not finding new files | Import each new `.cs` + `.meta` pair via `AssetDatabase.Refresh()` in Editor mode before running tests |

---

## Suggested execution sequence (for ChatController — step 1)

1. Extract `QueuedMessage` → `Models/Chat/QueuedMessage.cs` (trivial DTO, zero logic, safe warmup)
2. Extract `ChatNotificationManager` (smallest, no ScrollView coupling)
3. Extract `ChatInputManager` (no streaming coupling)
4. Extract `ChatAttachmentManager` (isolate `ClipboardImageData`)
5. Extract `ChatSearchController`
6. Extract `ChatMessageEditController`
7. Extract `ChatSelectionManager`
8. Extract `ToolCallApprovalController`
9. Extract `ChatStreamingCoordinator` (most coupling; do last)
10. Extract `ChatMessageListRenderer`
11. `ChatController` core is now ~400–600 lines of orchestration

Each step: extract → compile → run existing tests → commit. Do not batch multiple extractions into one commit.
