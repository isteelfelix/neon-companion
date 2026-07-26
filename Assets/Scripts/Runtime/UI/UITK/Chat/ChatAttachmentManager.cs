using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Models.Chat;
using NeonCompanion.Runtime.Platform;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK.Chat
{
    /// <summary>
    /// Manages file attachments in the composer — drag-and-drop, file picker,
    /// clipboard image paste, preview strip, and lightbox. Extracted from ChatController.
    /// </summary>
    internal class ChatAttachmentManager
    {
        private readonly VisualElement _composer;
        private readonly TextField _messageInput;
        private readonly Func<Task<CompanionApp>> _getAppAsync;
        private readonly Action<string> _showSystemMessage;
        private readonly Action<string> _showImageLightbox;

        private readonly List<ChatAttachment> _pendingComposerAttachments = new List<ChatAttachment>();
        private VisualElement _composerPreviews;
        private IFileDropService _fileDropService;
#if UNITY_EDITOR
        private bool _isDragOver;
#endif
        private bool _callbacksRegistered;

        /// <summary>Current pending attachments in the composer.</summary>
        public IReadOnlyList<ChatAttachment> CurrentAttachments => _pendingComposerAttachments;

        /// <summary>Fired whenever the attachment list changes.</summary>
        public event Action OnAttachmentsChanged;

        public ChatAttachmentManager(
            VisualElement composer,
            TextField messageInput,
            Func<Task<CompanionApp>> getAppAsync,
            Action<string> showSystemMessage,
            Action<string> showImageLightbox)
        {
            _composer = composer;
            _messageInput = messageInput;
            _getAppAsync = getAppAsync;
            _showSystemMessage = showSystemMessage;
            _showImageLightbox = showImageLightbox;
        }

        // ===== Public API =====

        /// <summary>Clears all pending attachments and re-renders the preview strip.</summary>
        public void Clear()
        {
            _pendingComposerAttachments.Clear();
            RenderComposerPreviews();
            OnAttachmentsChanged?.Invoke();
        }

        /// <summary>Returns a shallow clone of the current attachments without modifying state.</summary>
        public List<ChatAttachment> CloneCurrent()
        {
            return CloneAttachments(_pendingComposerAttachments);
        }

        /// <summary>Restores attachments from a previously cloned list (e.g. draft restoration).</summary>
        public void Restore(IReadOnlyList<ChatAttachment> attachments)
        {
            _pendingComposerAttachments.Clear();
            var restored = CloneAttachments(attachments);
            for (int i = 0; i < restored.Count; i++)
                _pendingComposerAttachments.Add(restored[i]);
            RenderComposerPreviews();
            OnAttachmentsChanged?.Invoke();
        }

        public void RestoreDraft(string message, IReadOnlyList<ChatAttachment> attachments, Action queueComposerHeightUpdate)
        {
            if (_messageInput != null)
                _messageInput.value = message ?? string.Empty;
            Restore(attachments);
            queueComposerHeightUpdate?.Invoke();
        }

        public static void RenderQueueIndicator(Queue<QueuedMessage> messageQueue, Label queueIndicator)
        {
            if (queueIndicator == null || messageQueue == null)
                return;

            if (messageQueue.Count > 0)
            {
                queueIndicator.style.display = DisplayStyle.Flex;
                queueIndicator.text = LocalizationExtensions.Get("chat.queue.pending", "Очередь: {0}")
                    .Replace("{0}", messageQueue.Count.ToString());
            }
            else
            {
                queueIndicator.style.display = DisplayStyle.None;
            }
        }

        // ===== Callback registration =====

        public void RegisterCallbacks(VisualElement chatMain)
        {
            _callbacksRegistered = true;

            if (_messageInput != null)
            {
                _messageInput.RegisterCallback<KeyDownEvent>(OnComposerKeyDownForPaste, TrickleDown.TrickleDown);
            }

            if (_composer != null)
            {
                _composerPreviews = _composer.Q<VisualElement>("composer-previews");
                if (_composerPreviews == null)
                {
                    _composerPreviews = new VisualElement();
                    _composerPreviews.name = "composer-previews";
                    _composerPreviews.AddToClassList("composer__previews");
                    _composer.Insert(0, _composerPreviews);
                }
                _composerPreviews.style.display = DisplayStyle.None;
            }

            // Drag-and-drop file support (U-44)
#if UNITY_EDITOR
            if (chatMain != null)
            {
                chatMain.RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
                chatMain.RegisterCallback<DragPerformEvent>(OnDragPerform);
                chatMain.RegisterCallback<DragLeaveEvent>(OnDragLeave);
            }
#endif
            _ = BindRuntimeFileDropAsync();
        }

        public void UnregisterCallbacks(VisualElement chatMain)
        {
            _callbacksRegistered = false;

            if (_messageInput != null)
            {
                _messageInput.UnregisterCallback<KeyDownEvent>(OnComposerKeyDownForPaste, TrickleDown.TrickleDown);
            }

#if UNITY_EDITOR
            if (chatMain != null)
            {
                chatMain.UnregisterCallback<DragUpdatedEvent>(OnDragUpdated);
                chatMain.UnregisterCallback<DragPerformEvent>(OnDragPerform);
                chatMain.UnregisterCallback<DragLeaveEvent>(OnDragLeave);
            }
#endif
            UnbindRuntimeFileDrop();
        }

        // ===== Paste handling (Ctrl+V) =====

        private void OnComposerKeyDownForPaste(KeyDownEvent evt)
        {
            if (evt == null)
                return;

            bool hasCtrl = evt.ctrlKey || evt.commandKey;

            // Ctrl+V — three cases (U-42):
            // A) clipboard text is a path to image file → attach as image
            // B) clipboard has bitmap data (CF_DIB) regardless of text → extract image
            // C) clipboard has only text → fall through so UITK TextField handles natively
            if (hasCtrl && evt.keyCode == KeyCode.V)
            {
                string clip = GUIUtility.systemCopyBuffer;
                if (!string.IsNullOrEmpty(clip) && IsImageFilePath(clip) && System.IO.File.Exists(clip))
                {
                    evt.StopPropagation();
                    _ = PasteImageFromClipboardAsync();
                    return;
                }
                if (ClipboardHasBitmapData())
                {
                    evt.StopPropagation();
                    _ = PasteWindowsClipboardImageAsync();
                    return;
                }
                // Text content: do not intercept — UITK handles paste natively.
            }
        }

        private Task PasteImageFromClipboardAsync()
        {
            try
            {
                string clipboard = GUIUtility.systemCopyBuffer;
                if (string.IsNullOrEmpty(clipboard))
                    return Task.CompletedTask;

                // Check if clipboard contains a file path to an image
                string[] imageExts = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
                string ext = System.IO.Path.GetExtension(clipboard)?.ToLowerInvariant();
                if (string.IsNullOrEmpty(ext))
                    return Task.CompletedTask;

                bool isImage = false;
                for (int i = 0; i < imageExts.Length; i++)
                {
                    if (ext == imageExts[i]) { isImage = true; break; }
                }
                if (!isImage)
                    return Task.CompletedTask;

                // Check file exists
                if (!System.IO.File.Exists(clipboard))
                    return Task.CompletedTask;
                if (!ResponsesAttachmentPayloadBuilder.IsSupportedPath(clipboard))
                    return Task.CompletedTask;

                string fileName = System.IO.Path.GetFileName(clipboard);
                _pendingComposerAttachments.Add(new ChatAttachment
                {
                    kind = "image",
                    name = fileName,
                    path = clipboard,
                    mediaType = GuessImageMediaType(clipboard)
                });

                RenderComposerPreviews();
                _messageInput?.Focus();
                OnAttachmentsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError("Paste image failed: " + ex.ToString());
            }
            return Task.CompletedTask;
        }

        private async Task PasteWindowsClipboardImageAsync()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            // Read image data on an STA thread (P/Invoke only), then convert DIB on main thread if needed.
            ClipboardImageData imageData = await GetClipboardImageDataAsync();
            if (imageData == null || imageData.Bytes == null || imageData.Bytes.Length == 0)
            {
                _showSystemMessage(LocalizationExtensions.Get("chat.paste.image_failed", "Не удалось извлечь изображение из буфера."));
                return;
            }

            string tempPath = imageData.IsDib
                ? DibToPngFile(imageData.Bytes)
                : WriteClipboardImageFile(imageData.Bytes, imageData.Extension);
            if (string.IsNullOrEmpty(tempPath))
            {
                _showSystemMessage(LocalizationExtensions.Get("chat.paste.image_failed", "Не удалось извлечь изображение из буфера."));
                return;
            }
            if (!ResponsesAttachmentPayloadBuilder.IsSupportedPath(tempPath))
            {
                try { System.IO.File.Delete(tempPath); } catch { }
                _showSystemMessage(LocalizationExtensions.Get("chat.paste.image_failed", "Не удалось извлечь изображение из буфера."));
                return;
            }

            string fileName = "clipboard-" + DateTime.Now.ToString("HHmmss") + ".png";
            _pendingComposerAttachments.Add(new ChatAttachment
            {
                kind = "image",
                name = fileName,
                path = tempPath,
                mediaType = string.IsNullOrEmpty(imageData.MediaType) ? "image/png" : imageData.MediaType
            });
            RenderComposerPreviews();
            _messageInput?.Focus();
            OnAttachmentsChanged?.Invoke();
#else
            await Task.CompletedTask;
#endif
        }

        // ===== File picker (attach button) =====

        public void OpenFilePicker()
        {
            _ = AttachImageTokenAsync();
        }

        private async Task AttachImageTokenAsync()
        {
            try
            {
                var app = await _getAppAsync();
                if (app == null || _messageInput == null) return;

                var filePicker = app.Services.GetRequired<IFilePickerService>();
                string path = await filePicker.PickImagePathAsync();
                if (string.IsNullOrEmpty(path)) return;
                if (!ResponsesAttachmentPayloadBuilder.IsSupportedPath(path))
                {
                    _showSystemMessage(LocalizationExtensions.Get("system.chat.attachment_failed", "Не удалось добавить вложение к сообщению."));
                    return;
                }

                string fileName = System.IO.Path.GetFileName(path);
                _pendingComposerAttachments.Add(new ChatAttachment
                {
                    kind = "image",
                    name = fileName,
                    path = path,
                    mediaType = GuessImageMediaType(path)
                });

                RenderComposerPreviews();
                _messageInput.Focus();
                OnAttachmentsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _showSystemMessage(LocalizationExtensions.Get("system.chat.attachment_failed", "Не удалось добавить вложение к сообщению."));
                NeonLogger.LogError(ex.ToString());
            }
        }

        // ===== Drag-and-drop =====

        private async Task BindRuntimeFileDropAsync()
        {
            if (_fileDropService != null)
                return;

            try
            {
                var app = await _getAppAsync();
                if (!_callbacksRegistered || app == null)
                    return;

                IFileDropService fileDrop = null;
                if (!app.Services.TryGet<IFileDropService>(out fileDrop) || fileDrop == null || !fileDrop.IsAvailable)
                    return;

                _fileDropService = fileDrop;
                _fileDropService.FilesDropped += OnRuntimeFilesDropped;
                _fileDropService.Start();
            }
            catch (Exception ex)
            {
                NeonLogger.LogWarning("File drop binding failed: " + ex.Message);
            }
        }

        private void UnbindRuntimeFileDrop()
        {
            if (_fileDropService == null)
                return;

            _fileDropService.FilesDropped -= OnRuntimeFilesDropped;
            _fileDropService.Stop();
            _fileDropService = null;
        }

        private void OnRuntimeFilesDropped(IReadOnlyList<string> paths)
        {
#if UNITY_EDITOR
            _isDragOver = false;
#endif
            _composer?.parent?.RemoveFromClassList("chat-main--drag-over");

            int added = AddPendingAttachmentsFromPaths(paths);
            if (added > 0)
            {
                RenderComposerPreviews();
                _messageInput?.Focus();
                OnAttachmentsChanged?.Invoke();
                return;
            }

            _showSystemMessage?.Invoke(LocalizationExtensions.Get(
                "chat.drop.no_supported_files",
                "Не удалось добавить файлы: поддерживаются изображения и текстовые документы."));
        }

#if UNITY_EDITOR
        private void OnDragUpdated(DragUpdatedEvent evt)
        {
            if (!HasValidDragFiles(evt))
                return;

            SetDragCopyVisualMode();
            if (!_isDragOver)
            {
                _isDragOver = true;
                _composer?.parent?.AddToClassList("chat-main--drag-over");
            }
            evt.StopPropagation();
        }

        private void OnDragPerform(DragPerformEvent evt)
        {
            _isDragOver = false;
            _composer?.parent?.RemoveFromClassList("chat-main--drag-over");

            if (!HasValidDragFiles(evt))
                return;

            string[] paths = GetDraggedPaths();
            if (paths == null || paths.Length == 0)
                return;

            int added = AddPendingAttachmentsFromPaths(paths);
            if (added > 0)
            {
                RenderComposerPreviews();
                OnAttachmentsChanged?.Invoke();
            }
            evt.StopPropagation();
        }

        private void OnDragLeave(DragLeaveEvent evt)
        {
            _isDragOver = false;
            _composer?.parent?.RemoveFromClassList("chat-main--drag-over");
            evt.StopPropagation();
        }

        private static bool HasValidDragFiles(DragUpdatedEvent evt)
        {
            string[] paths = GetDraggedPaths();
            return paths != null && paths.Length > 0;
        }

        private static bool HasValidDragFiles(DragPerformEvent evt)
        {
            string[] paths = GetDraggedPaths();
            return paths != null && paths.Length > 0;
        }
#endif

        private int AddPendingAttachmentsFromPaths(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0)
                return 0;

            int added = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (string.IsNullOrEmpty(path))
                    continue;

                if (!System.IO.File.Exists(path))
                    continue;

                if (!ResponsesAttachmentPayloadBuilder.IsSupportedPath(path))
                    continue;

                string ext = System.IO.Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
                bool isImage = IsImageExtension(ext);
                _pendingComposerAttachments.Add(new ChatAttachment
                {
                    kind = isImage ? "image" : "file",
                    name = System.IO.Path.GetFileName(path),
                    path = path,
                    mediaType = isImage ? GuessImageMediaType(path) : GuessFileMediaType(path)
                });
                added++;
            }

            return added;
        }

        // ===== Composer preview strip =====

        private void RenderComposerPreviews()
        {
            if (_composerPreviews == null) return;
            _composerPreviews.Clear();

            if (_pendingComposerAttachments.Count == 0)
            {
                _composerPreviews.style.display = DisplayStyle.None;
                return;
            }

            _composerPreviews.style.display = DisplayStyle.Flex;

            for (int i = 0; i < _pendingComposerAttachments.Count; i++)
            {
                var attachment = _pendingComposerAttachments[i];
                if (attachment == null) continue;

                int index = i; // capture for closure
                var thumb = new VisualElement();
                thumb.AddToClassList("composer__preview-thumb");

                if (!string.IsNullOrEmpty(attachment.path) && System.IO.File.Exists(attachment.path))
                {
                    if (attachment.kind == "image" || IsImageFile(attachment.path))
                    {
                        var img = new Image();
                        img.AddToClassList("composer__preview-img");
                        img.scaleMode = ScaleMode.ScaleAndCrop;
                        img.schedule.Execute(() => ChatMessageListRenderer.LoadImageAsync(img, attachment.path));
                        string previewPath = attachment.path; // capture for closure
                        img.RegisterCallback<ClickEvent>(evt =>
                        {
                            _showImageLightbox?.Invoke(previewPath);
                            evt.StopPropagation();
                        });
                        thumb.Add(img);
                    }
                    else
                    {
                        var fileLabel = new Label(GetAttachmentDisplayName(attachment));
                        fileLabel.AddToClassList("composer__preview-file");
                        thumb.Add(fileLabel);
                    }
                }

                var removeBtn = new Button(() => RemovePendingAttachment(index));
                removeBtn.text = "\u00d7";
                removeBtn.AddToClassList("composer__preview-remove");
                removeBtn.tooltip = LocalizationExtensions.Get("chat.preview.remove", "Убрать");
                thumb.Add(removeBtn);

                _composerPreviews.Add(thumb);
            }
        }

        private void RemovePendingAttachment(int index)
        {
            if (index < 0 || index >= _pendingComposerAttachments.Count) return;
            _pendingComposerAttachments.RemoveAt(index);

            // Keep the composer text clean if it still contains legacy attachment tokens.
            string text = _messageInput?.value ?? string.Empty;
            string rebuilt = BuildComposerTextWithAttachments(text, _pendingComposerAttachments);
            if (_messageInput != null)
                _messageInput.value = rebuilt;

            RenderComposerPreviews();
            OnAttachmentsChanged?.Invoke();
        }

        // ===== Static helpers =====

        /// <summary>
        /// Copies selected files to app-owned storage before they enter persisted chat history.
        /// Callers should send and persist the returned list, never the original arbitrary paths.
        /// </summary>
        internal static bool TryPersistAttachments(IReadOnlyList<ChatAttachment> attachments, out List<ChatAttachment> persisted, out string error)
        {
            persisted = new List<ChatAttachment>();
            error = null;
            if (attachments == null)
                return true;

            var createdPaths = new List<string>();
            for (int i = 0; i < attachments.Count; i++)
            {
                var attachment = attachments[i];
                if (attachment == null)
                    continue;

                string copiedPath;
                if (!ResponsesAttachmentPayloadBuilder.TryPersist(attachment.path, attachment.name, Application.persistentDataPath, out copiedPath, out error))
                {
                    for (int j = 0; j < createdPaths.Count; j++)
                    {
                        try { System.IO.File.Delete(createdPaths[j]); } catch { }
                    }
                    persisted.Clear();
                    return false;
                }

                if (!string.Equals(
                        System.IO.Path.GetFullPath(attachment.path),
                        System.IO.Path.GetFullPath(copiedPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    createdPaths.Add(copiedPath);
                }
                persisted.Add(new ChatAttachment
                {
                    kind = attachment.kind,
                    name = attachment.name,
                    path = copiedPath,
                    mediaType = attachment.mediaType
                });
            }
            return true;
        }

        /// <summary>
        /// Strips attachment tokens from composer text based on the given attachment list.
        /// </summary>
        public static string StripAttachmentTokens(string composerText, IReadOnlyList<ChatAttachment> attachments)
        {
            string text = composerText ?? string.Empty;
            if (attachments != null)
            {
                for (int i = 0; i < attachments.Count; i++)
                {
                    string name = attachments[i]?.name;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    string token = $"[attachment: {name}]";
                    text = text.Replace(token, string.Empty);
                }
            }

            return CollapseWhitespace(text).Trim();
        }

        private static string BuildComposerTextWithAttachments(string composerText, IReadOnlyList<ChatAttachment> attachments)
        {
            return StripAllAttachmentTokens(composerText ?? string.Empty).Trim();
        }

        private static string StripAllAttachmentTokens(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            int idx;
            while ((idx = text.IndexOf("[attachment: ", StringComparison.Ordinal)) >= 0)
            {
                int end = text.IndexOf(']', idx + 13);
                if (end < 0)
                    break;
                text = text.Remove(idx, end - idx + 1);
            }
            return text;
        }

        internal static string CollapseWhitespace(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var sb = new StringBuilder(value.Length);
            bool previousWasInlineWhitespace = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\r')
                    continue;

                if (c == '\n')
                {
                    sb.Append('\n');
                    previousWasInlineWhitespace = false;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    if (!previousWasInlineWhitespace)
                        sb.Append(' ');
                    previousWasInlineWhitespace = true;
                }
                else
                {
                    sb.Append(c);
                    previousWasInlineWhitespace = false;
                }
            }

            return sb.ToString();
        }

        internal static List<ChatAttachment> CloneAttachments(IReadOnlyList<ChatAttachment> attachments)
        {
            if (attachments == null || attachments.Count == 0)
                return new List<ChatAttachment>();

            var clone = new List<ChatAttachment>(attachments.Count);
            for (int i = 0; i < attachments.Count; i++)
            {
                var src = attachments[i];
                if (src == null) { clone.Add(null); continue; }
                clone.Add(new ChatAttachment
                {
                    kind = src.kind,
                    name = src.name,
                    path = src.path,
                    mediaType = src.mediaType
                });
            }
            return clone;
        }

        internal static string GetAttachmentDisplayName(ChatAttachment attachment)
        {
            if (attachment == null)
                return string.Empty;
            return !string.IsNullOrWhiteSpace(attachment.name) ? attachment.name : "image";
        }

        // ===== Image loading and layout =====

        internal static bool IsImageFile(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            string ext = System.IO.Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
                return false;
            return string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".gif", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".webp", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsImageFilePath(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 512)
                return false;
            // A real file path is a single line with no illegal path characters. Guard before
            // Path.GetExtension, which throws ArgumentException ("Illegal characters in path")
            // on control chars such as the newlines/tabs present in pasted multi-line markdown.
            if (text.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
                return false;
            string ext;
            try
            {
                ext = System.IO.Path.GetExtension(text)?.ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return false;
            }
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg"
                || ext == ".gif" || ext == ".webp" || ext == ".bmp";
        }

        // ===== File type helpers =====

        private static string GuessFileMediaType(string path)
        {
            string ext = System.IO.Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
            if (ext == ".pdf") return "application/pdf";
            if (ext == ".json") return "application/json";
            if (ext == ".xml") return "application/xml";
            if (ext == ".csv") return "text/csv";
            if (ext == ".md") return "text/markdown";
            if (ext == ".txt") return "text/plain";
            if (ext == ".cs" || ext == ".py" || ext == ".js" || ext == ".ts" || ext == ".java" || ext == ".cpp" || ext == ".h" || ext == ".csproj")
                return "text/plain";
            return "application/octet-stream";
        }

        private static bool IsImageExtension(string ext)
        {
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".bmp" || ext == ".webp";
        }

        internal static string GuessImageMediaType(string path)
        {
            string extension = System.IO.Path.GetExtension(path)?.ToLowerInvariant();
            switch (extension)
            {
                case ".png": return "image/png";
                case ".jpg": return "image/jpeg";
                case ".jpeg": return "image/jpeg";
                case ".webp": return "image/webp";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                default: return "application/octet-stream";
            }
        }

        internal static string MessageCountText(int count)
        {
            int mod100 = count % 100;
            int mod10 = count % 10;
            string word;

            if (mod100 >= 11 && mod100 <= 14)
                word = LocalizationExtensions.Get("chat.messages.many", "сообщений");
            else if (mod10 == 1)
                word = LocalizationExtensions.Get("chat.messages.one", "сообщение");
            else if (mod10 >= 2 && mod10 <= 4)
                word = LocalizationExtensions.Get("chat.messages.few", "сообщения");
            else
                word = LocalizationExtensions.Get("chat.messages.many", "сообщений");

            return $"{count} {word}";
        }

        internal static void SetDisplay(VisualElement element, DisplayStyle display)
        {
            if (element != null)
                element.style.display = display;
        }

        // ── Windows clipboard bitmap support (PNG/JFIF/CF_DIB via user32 / kernel32 P/Invoke) ──
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private const uint ClipboardFormatDib = 8;
        private const uint ClipboardFormatDibV5 = 17;

        private sealed class ClipboardImageData
        {
            public byte[] Bytes;
            public bool IsDib;
            public string Extension;
            public string MediaType;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint RegisterClipboardFormat(string lpszFormat);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool OpenClipboard(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool CloseClipboard();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint format);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern int GlobalSize(IntPtr hMem);

        private static bool ClipboardHasBitmapData()
        {
            try
            {
                if (IsClipboardFormatAvailable(ClipboardFormatDib) ||
                    IsClipboardFormatAvailable(ClipboardFormatDibV5))
                    return true;

                uint pngFormat = RegisterClipboardFormat("PNG");
                uint jfifFormat = RegisterClipboardFormat("JFIF");
                return (pngFormat != 0 && IsClipboardFormatAvailable(pngFormat)) ||
                       (jfifFormat != 0 && IsClipboardFormatAvailable(jfifFormat));
            }
            catch
            {
                return false;
            }
        }

        // Reads encoded image bytes first, then raw DIB bytes, on an STA thread.
        private static Task<ClipboardImageData> GetClipboardImageDataAsync()
        {
            var tcs = new TaskCompletionSource<ClipboardImageData>();
            var t = new System.Threading.Thread(() =>
            {
                if (!OpenClipboard(IntPtr.Zero)) { tcs.TrySetResult(null); return; }
                try
                {
                    uint pngFormat = RegisterClipboardFormat("PNG");
                    byte[] png = GetClipboardBytes(pngFormat);
                    if (png != null && png.Length > 0)
                    {
                        tcs.TrySetResult(new ClipboardImageData
                        {
                            Bytes = png,
                            Extension = ".png",
                            MediaType = "image/png",
                            IsDib = false
                        });
                        return;
                    }

                    uint jfifFormat = RegisterClipboardFormat("JFIF");
                    byte[] jfif = GetClipboardBytes(jfifFormat);
                    if (jfif != null && jfif.Length > 0)
                    {
                        tcs.TrySetResult(new ClipboardImageData
                        {
                            Bytes = jfif,
                            Extension = ".jpg",
                            MediaType = "image/jpeg",
                            IsDib = false
                        });
                        return;
                    }

                    byte[] dib = GetClipboardBytes(ClipboardFormatDibV5);
                    if (dib == null || dib.Length == 0)
                        dib = GetClipboardBytes(ClipboardFormatDib);

                    if (dib != null && dib.Length > 0)
                    {
                        tcs.TrySetResult(new ClipboardImageData
                        {
                            Bytes = dib,
                            Extension = ".png",
                            MediaType = "image/png",
                            IsDib = true
                        });
                        return;
                    }

                    tcs.TrySetResult(null);
                }
                finally { CloseClipboard(); }
            });
            try { t.SetApartmentState(System.Threading.ApartmentState.STA); t.IsBackground = true; t.Start(); }
            catch { tcs.TrySetResult(null); }
            return tcs.Task;
        }

        private static byte[] GetClipboardBytes(uint format)
        {
            if (format == 0 || !IsClipboardFormatAvailable(format))
                return null;

            IntPtr hData = GetClipboardData(format);
            if (hData == IntPtr.Zero)
                return null;

            int size = GlobalSize(hData);
            if (size <= 0)
                return null;

            IntPtr ptr = GlobalLock(hData);
            if (ptr == IntPtr.Zero)
                return null;

            try
            {
                byte[] bytes = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(ptr, bytes, 0, size);
                return bytes;
            }
            finally
            {
                GlobalUnlock(hData);
            }
        }

        // Converts raw DIB bytes to a PNG temp file using Unity's Texture2D.
        // Must be called from the main thread (Texture2D API requirement).
        private static string DibToPngFile(byte[] dib)
        {
            if (dib == null || dib.Length < 40) return null;
            try
            {
                int headerSize  = BitConverter.ToInt32(dib, 0);
                int width       = BitConverter.ToInt32(dib, 4);
                int height      = BitConverter.ToInt32(dib, 8);
                short bpp       = BitConverter.ToInt16(dib, 14);
                int compression = BitConverter.ToInt32(dib, 16);
                int clrUsed     = BitConverter.ToInt32(dib, 32);

                if (width <= 0 || height == 0 ||
                    (compression != 0 && compression != 3) ||
                    (bpp != 24 && bpp != 32))
                    return null; // only uncompressed 24/32 bpp supported

                bool bottomUp = height > 0;
                if (height < 0) height = -height;

                int extraHeaderBytes = 0;
                if (compression == 3 && headerSize == 40)
                    extraHeaderBytes = bpp == 32 ? 16 : 12; // BI_BITFIELDS masks after BITMAPINFOHEADER

                int colorTableSize = bpp <= 8 ? (clrUsed > 0 ? clrUsed : (1 << bpp)) * 4 : 0;
                int pixelOffset = headerSize + extraHeaderBytes + colorTableSize;
                if (pixelOffset >= dib.Length) return null;

                int bytesPerPixel = bpp / 8;
                int stride = bpp == 32 ? width * 4 : ((width * 3 + 3) / 4) * 4;

                var colors = new Color32[width * height];
                for (int y = 0; y < height; y++)
                {
                    int srcY = bottomUp ? y : (height - 1 - y);
                    int rowBase = pixelOffset + srcY * stride;
                    for (int x = 0; x < width; x++)
                    {
                        int off = rowBase + x * bytesPerPixel;
                        if (off + 2 >= dib.Length) break;
                        byte b = dib[off];
                        byte g = dib[off + 1];
                        byte r = dib[off + 2];
                        byte a = (bpp == 32 && off + 3 < dib.Length) ? dib[off + 3] : (byte)255;
                        colors[y * width + x] = new Color32(r, g, b, a);
                    }
                }

                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.SetPixels32(colors);
                tex.Apply();
                byte[] png = tex.EncodeToPNG();
                UnityEngine.Object.Destroy(tex);

                if (png == null || png.Length == 0) return null;

                string path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "neon-paste-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
                System.IO.File.WriteAllBytes(path, png);
                return path;
            }
            catch (Exception ex)
            {
                NeonLogger.LogWarning("DIB→PNG failed: " + ex.Message);
                return null;
            }
        }

        private static string WriteClipboardImageFile(byte[] bytes, string extension)
        {
            if (bytes == null || bytes.Length == 0)
                return null;

            string ext = string.IsNullOrEmpty(extension) ? ".png" : extension;
            if (!ext.StartsWith(".", StringComparison.Ordinal))
                ext = "." + ext;

            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "neon-paste-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ext);
            System.IO.File.WriteAllBytes(path, bytes);
            return path;
        }
#else
        private static bool ClipboardHasBitmapData() { return false; }
#endif

#if UNITY_EDITOR
        private static void SetDragCopyVisualMode()
        {
            UnityEditor.DragAndDrop.visualMode = UnityEditor.DragAndDropVisualMode.Copy;
        }

        private static string[] GetDraggedPaths()
        {
            return UnityEditor.DragAndDrop.paths;
        }
#else
        private static string[] GetDraggedPaths()
        {
            return null;
        }
#endif
    }
}
