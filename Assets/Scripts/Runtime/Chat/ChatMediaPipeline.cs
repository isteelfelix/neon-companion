using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api.Models;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using UnityEngine;
using UnityEngine.Networking;

namespace NeonCompanion.Runtime.Chat
{
    /// <summary>
    /// The single media pipeline for assistant-emitted <c>MEDIA:&lt;path&gt;</c> markers (and bare
    /// markdown image links): parse the marker out of the reply text, resolve it to something this
    /// client can actually fetch, download it, and hand back a <see cref="ChatAttachment"/> the
    /// transcript renders as an inline image or as a file chip.
    ///
    /// Both backends go through here so they behave alike: the OpenAI-compatible HTTP path
    /// (ChatViewModel) and the Hermes WebSocket path (ChatService). Remote Hermes paths such as
    /// <c>/home/hermes/hermes/images/x.png</c> live on the gateway's disk, never on this client's,
    /// so they are fetched over the gateway's authenticated <c>GET /api/files/download?path=…</c>
    /// route — the same contract Desktop's <c>mediaExternalUrl</c> uses.
    /// </summary>
    public static class ChatMediaPipeline
    {
        public const string ImageKind = "image";
        public const string FileKind = "file";

        // Remote URL -> cached local copy. Reloading a Hermes session replays the whole history
        // through this pipeline, so without it every reload re-downloaded every media marker.
        private static readonly Dictionary<string, string> s_DownloadCache = new Dictionary<string, string>();

        /// <summary>
        /// Pull every media marker out of <paramref name="message"/> (content + text segments),
        /// download what they point at, and append the results to the message's attachments.
        /// When nothing could be fetched the original text is restored, so a broken marker still
        /// shows its path instead of silently vanishing.
        /// </summary>
        public static async Task ApplyMediaMarkersAsync(
            ChatMessage message,
            ProviderConfig provider,
            List<AiChatAttachment> extraIncoming = null)
        {
            if (message == null)
                return;

            string originalContent = message.content;
            List<string> originalSegmentText = SnapshotTextSegments(message);

            var incoming = new List<AiChatAttachment>();
            if (extraIncoming != null)
            {
                for (int i = 0; i < extraIncoming.Count; i++)
                {
                    if (extraIncoming[i] != null)
                        incoming.Add(extraIncoming[i]);
                }
            }

            int mediaMarkerStart = incoming.Count;
            ExtractMediaMarkerAttachments(message, provider, incoming);
            int mediaMarkerCount = incoming.Count - mediaMarkerStart;
            if (incoming.Count == 0)
                return;

            var localAtts = new List<ChatAttachment>();
            if (message.attachments != null && message.attachments.Count > 0)
                localAtts.AddRange(CloneAttachments(message.attachments));

            bool downloadedMediaMarker = false;
            for (int i = 0; i < incoming.Count; i++)
            {
                ChatAttachment cached = await DownloadAndCacheAttachmentAsync(incoming[i]);
                if (cached == null)
                    continue;

                localAtts.Add(cached);
                if (i >= mediaMarkerStart)
                    downloadedMediaMarker = true;
            }

            message.attachments = localAtts;
            if (mediaMarkerCount > 0 && !downloadedMediaMarker)
                RestoreTextSegments(message, originalContent, originalSegmentText);
        }

        // === Marker parsing ===

        public static void ExtractMediaMarkerAttachments(
            ChatMessage message,
            ProviderConfig provider,
            List<AiChatAttachment> attachments)
        {
            if (message == null || attachments == null)
                return;

            message.content = ExtractMediaMarkersFromText(message.content, provider, attachments);

            if (message.segments == null)
                return;

            for (int i = 0; i < message.segments.Count; i++)
            {
                ChatMessageSegment segment = message.segments[i];
                if (segment == null || !string.Equals(segment.kind, ChatMessageSegment.TextKind, StringComparison.OrdinalIgnoreCase))
                    continue;

                segment.text = ExtractMediaMarkersFromText(segment.text, provider, attachments);
            }
        }

        public static string ExtractMediaMarkersFromText(
            string text,
            ProviderConfig provider,
            List<AiChatAttachment> attachments)
        {
            if (string.IsNullOrEmpty(text) || attachments == null)
                return text ?? string.Empty;

            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            var kept = new List<string>(lines.Length);
            bool changed = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string mediaPath;
                if (TryReadMediaMarker(line, out mediaPath))
                {
                    AiChatAttachment attachment = CreateMediaMarkerAttachment(mediaPath, provider);
                    if (attachment != null && !ContainsAttachmentPath(attachments, attachment.path))
                        attachments.Add(attachment);
                    changed = true;
                    continue;
                }

                kept.Add(line);
            }

            if (!changed)
                return text;

            return TrimBlankEdges(string.Join("\n", kept.ToArray()));
        }

        /// <summary>
        /// Drop media marker lines from text without producing attachments. Used where the text is
        /// consumed as prose (TTS, avatar) — a file path read aloud helps nobody.
        /// </summary>
        public static string StripMediaMarkers(string text)
        {
            return ExtractMediaMarkersFromText(text, null, new List<AiChatAttachment>());
        }

        public static bool TryReadMediaMarker(string line, out string mediaPath)
        {
            mediaPath = null;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = line.Trim();
            const string marker = "MEDIA:";
            bool isMediaMarker = trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase);
            if (!isMediaMarker)
                return TryReadMarkdownImageMarker(trimmed, out mediaPath);

            string value = Unquote(trimmed.Substring(marker.Length).Trim());
            if (string.IsNullOrWhiteSpace(value))
                return false;

            mediaPath = value;
            return true;
        }

        private static bool TryReadMarkdownImageMarker(string trimmedLine, out string mediaPath)
        {
            mediaPath = null;
            if (string.IsNullOrWhiteSpace(trimmedLine))
                return false;

            if (!trimmedLine.StartsWith("![", StringComparison.Ordinal))
                return false;

            int labelEnd = trimmedLine.IndexOf("](", StringComparison.Ordinal);
            if (labelEnd < 0)
                return false;

            int pathStart = labelEnd + 2;
            int pathEnd = trimmedLine.LastIndexOf(')');
            if (pathEnd <= pathStart)
                return false;

            string value = Unquote(trimmedLine.Substring(pathStart, pathEnd - pathStart).Trim());
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (!LooksLikeIncomingMediaPath(value))
                return false;

            mediaPath = value;
            return true;
        }

        private static string Unquote(string value)
        {
            if (value == null || value.Length < 2)
                return value;

            bool quoted = (value[0] == '"' && value[value.Length - 1] == '"') ||
                          (value[0] == '\'' && value[value.Length - 1] == '\'') ||
                          (value[0] == '`' && value[value.Length - 1] == '`');
            return quoted ? value.Substring(1, value.Length - 2).Trim() : value;
        }

        private static bool LooksLikeIncomingMediaPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (value.StartsWith("MEDIA:", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("/root/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("root/", StringComparison.OrdinalIgnoreCase))
                return true;

            // A bare markdown link is only treated as media when it names an image — anything
            // else is ordinary prose linking to a document and must stay as text.
            return IsImageExtension(GetFileExtensionFromUrl(value));
        }

        private static AiChatAttachment CreateMediaMarkerAttachment(string mediaPath, ProviderConfig provider)
        {
            if (string.IsNullOrWhiteSpace(mediaPath))
                return null;

            // Kind drives the whole downstream render (inline image vs file chip), so decide it
            // from the marker itself rather than forcing every marker to "image" the way the
            // pre-pipeline code did.
            bool isDataImage = mediaPath.TrimStart().StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
            string ext = isDataImage ? ".png" : GetFileExtensionFromUrl(mediaPath);
            bool isImage = isDataImage || IsImageExtension(ext);

            var attachment = new AiChatAttachment();
            attachment.kind = isImage ? ImageKind : FileKind;
            attachment.name = isDataImage ? "image" + ext : DeriveFileNameFromUrl(mediaPath, ext);
            attachment.path = ResolveMediaMarkerPath(mediaPath, provider);
            attachment.mediaType = isDataImage ? null : GuessMediaTypeFromExtension(ext);
            return attachment;
        }

        private static bool ContainsAttachmentPath(List<AiChatAttachment> attachments, string path)
        {
            if (attachments == null || string.IsNullOrWhiteSpace(path))
                return false;

            for (int i = 0; i < attachments.Count; i++)
            {
                AiChatAttachment attachment = attachments[i];
                if (attachment != null && string.Equals(attachment.path, path, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        // === Path resolution ===

        /// <summary>
        /// Turn a marker path into something fetchable: an existing local file / data URL / http
        /// URL is used as-is; a gateway-local path becomes an authenticated gateway download URL.
        /// </summary>
        public static string ResolveMediaMarkerPath(string mediaPath, ProviderConfig provider)
        {
            string path = (mediaPath ?? string.Empty).Trim();
            const string mediaPrefix = "MEDIA:";
            if (path.StartsWith(mediaPrefix, StringComparison.OrdinalIgnoreCase))
                path = path.Substring(mediaPrefix.Length).Trim();
            if (string.IsNullOrEmpty(path))
                return path;

            if (path.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(path))
            {
                return path;
            }

            string normalizedPath = path.Replace('\\', '/');

            // Hermes: the file is on the gateway machine. Desktop fetches these over
            // GET /api/files/download?path=<abs path>; do the same instead of guessing a static
            // media route that only ever existed for the OpenAI-compatible gateways below.
            // `/assets/…` keeps the static-origin mapping: it is a web path, not a disk path.
            if (!normalizedPath.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
            {
                string gatewayUrl = HermesFileDownloadUrl(path, provider);
                if (!string.IsNullOrEmpty(gatewayUrl))
                    return gatewayUrl;
            }

            const string hermesRoot = "/root/hermes";
            const string hermesHiddenRoot = "/root/.hermes";
            if (normalizedPath.StartsWith(hermesRoot, StringComparison.OrdinalIgnoreCase))
                normalizedPath = normalizedPath.Substring(hermesRoot.Length);
            else if (normalizedPath.StartsWith(hermesHiddenRoot, StringComparison.OrdinalIgnoreCase))
                normalizedPath = normalizedPath.Substring(hermesHiddenRoot.Length);
            else if (normalizedPath.StartsWith("root/hermes/", StringComparison.OrdinalIgnoreCase))
                normalizedPath = "/" + normalizedPath.Substring("root/hermes/".Length);
            else if (normalizedPath.StartsWith("root/.hermes/", StringComparison.OrdinalIgnoreCase))
                normalizedPath = "/" + normalizedPath.Substring("root/.hermes/".Length);

            string baseUrl = normalizedPath.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)
                ? GetProviderOriginUrl(provider)
                : GetProviderMediaBaseUrl(provider);
            if (string.IsNullOrWhiteSpace(baseUrl))
                return path;

            if (!normalizedPath.StartsWith("/", StringComparison.Ordinal))
                normalizedPath = "/" + normalizedPath;

            return baseUrl.TrimEnd('/') + normalizedPath;
        }

        /// <summary>
        /// Authenticated gateway download URL for a Hermes-local absolute path, or null when the
        /// active backend is not Hermes / has no REST endpoint configured.
        /// </summary>
        public static string HermesFileDownloadUrl(string absolutePath, ProviderConfig provider)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
                return null;
            if (!IsHermesBackend(provider))
                return null;

            string restUrl = HermesRestBaseUrl();
            if (string.IsNullOrEmpty(restUrl))
                return null;

            // The gateway requires an absolute path (relative ones are rejected unless a managed
            // root is locked); a Windows-style path never belongs to a POSIX gateway.
            string path = absolutePath.Trim();
            if (!path.StartsWith("/", StringComparison.Ordinal) && !path.StartsWith("~", StringComparison.Ordinal))
                return null;

            return restUrl + "/api/files/download?path=" + UnityWebRequest.EscapeURL(path);
        }

        /// <summary>True when <paramref name="url"/> targets the configured Hermes gateway.</summary>
        public static bool IsHermesGatewayUrl(string url)
        {
            string restUrl = HermesRestBaseUrl();
            return !string.IsNullOrEmpty(restUrl) &&
                   !string.IsNullOrEmpty(url) &&
                   url.StartsWith(restUrl, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHermesBackend(ProviderConfig provider)
        {
            if (provider != null && !string.IsNullOrWhiteSpace(provider.backendType) &&
                string.Equals(provider.backendType, "hermes", StringComparison.OrdinalIgnoreCase))
                return true;

            GlobalBackendSelector selector = GlobalBackendSelector.Instance;
            return selector != null && selector.CurrentMode == BackendMode.Hermes;
        }

        private static string HermesRestBaseUrl()
        {
            GlobalBackendSelector selector = GlobalBackendSelector.Instance;
            string restUrl = selector != null ? selector.HermesRestUrl : null;
            return string.IsNullOrWhiteSpace(restUrl) ? string.Empty : restUrl.Trim().TrimEnd('/');
        }

        private static string GetProviderMediaBaseUrl(ProviderConfig provider)
        {
            string baseUrl = (provider != null ? provider.baseUrl : null) ?? string.Empty;
            baseUrl = baseUrl.Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl))
                return string.Empty;

            if (baseUrl.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
                baseUrl = baseUrl.Substring(0, baseUrl.Length - "/responses".Length).TrimEnd('/');
            if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                baseUrl = baseUrl.Substring(0, baseUrl.Length - 3).TrimEnd('/');

            return baseUrl;
        }

        private static string GetProviderOriginUrl(ProviderConfig provider)
        {
            string baseUrl = GetProviderMediaBaseUrl(provider);
            if (string.IsNullOrEmpty(baseUrl))
                return baseUrl;

            try
            {
                var uri = new Uri(baseUrl);
                return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            }
            catch
            {
                return baseUrl;
            }
        }

        // === Download ===

        /// <summary>
        /// Fetch an incoming attachment into app-owned storage. Images are validated as images
        /// (a 404 HTML body must never render as a broken picture); every other kind is accepted
        /// as opaque bytes so a file chip can open it later.
        /// </summary>
        public static async Task<ChatAttachment> DownloadAndCacheAttachmentAsync(AiChatAttachment aiAtt)
        {
            if (aiAtt == null || string.IsNullOrWhiteSpace(aiAtt.path))
                return null;

            string url = aiAtt.path.Trim();
            bool wantsImage = !string.Equals(aiAtt.kind, FileKind, StringComparison.OrdinalIgnoreCase);

            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return CacheDataUrlAttachment(aiAtt, url);

            string cachedPath;
            if (s_DownloadCache.TryGetValue(url, out cachedPath) && File.Exists(cachedPath))
                url = cachedPath;

            // Already on this disk (local gateway, or an attachment we cached earlier).
            if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || File.Exists(url))
            {
                var local = new ChatAttachment();
                local.kind = string.IsNullOrWhiteSpace(aiAtt.kind) ? ImageKind : aiAtt.kind;
                local.name = !string.IsNullOrWhiteSpace(aiAtt.name) ? aiAtt.name : "attachment";
                local.path = url;
                local.mediaType = aiAtt.mediaType;
                return local;
            }

            try
            {
                // The gateway download URL carries the real path in its query string, so the URL
                // itself has no extension — fall back to the marker-derived name, which does.
                // The cached copy must keep it: the OS opens a file chip by extension.
                string ext = GetFileExtensionFromUrl(url);
                if (string.IsNullOrEmpty(ext))
                    ext = GetFileExtensionFromUrl(aiAtt.name);
                if (string.IsNullOrEmpty(ext) && wantsImage)
                    ext = ".png";

                string dir = Path.Combine(Application.persistentDataPath, "Attachments");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                // Non-images are opened by the OS from this cached copy, so keep the assistant's
                // file name visible instead of showing the user a bare GUID.
                string cacheName = Guid.NewGuid().ToString("N") + ext;
                if (!wantsImage)
                    cacheName = Guid.NewGuid().ToString("N") + "_" + SafeCacheFileName(aiAtt.name, ext);
                string localPath = Path.Combine(dir, cacheName);

                using (UnityWebRequest req = UnityWebRequest.Get(url))
                {
                    ApplyGatewayAuth(req, url);
                    UnityWebRequestAsyncOperation operation = req.SendWebRequest();
                    while (!operation.isDone)
                        await Task.Yield();

                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        NeonLogger.LogWarning("Failed to download incoming attachment from " + url + ": " + (req.error ?? "unknown error"));
                        return null;
                    }

                    byte[] data = req.downloadHandler != null ? req.downloadHandler.data : null;
                    if (data == null || data.Length == 0)
                    {
                        NeonLogger.LogWarning("Downloaded attachment has no data from " + url);
                        return null;
                    }

                    string mediaType = !string.IsNullOrWhiteSpace(aiAtt.mediaType) ? aiAtt.mediaType : GuessMediaTypeFromExtension(ext);
                    string ct = req.GetResponseHeader("Content-Type");
                    if (string.IsNullOrWhiteSpace(aiAtt.mediaType) && !string.IsNullOrWhiteSpace(ct))
                    {
                        int semi = ct.IndexOf(';');
                        mediaType = semi > 0 ? ct.Substring(0, semi).Trim() : ct.Trim();
                    }

                    if (wantsImage && !IsSupportedImagePayload(data, mediaType, ext))
                    {
                        NeonLogger.LogWarning("Incoming attachment did not look like an image from " + url + " (Content-Type: " + (ct ?? "<none>") + ")");
                        return null;
                    }

                    File.WriteAllBytes(localPath, data);
                    s_DownloadCache[url] = localPath;

                    var cached = new ChatAttachment();
                    cached.kind = wantsImage ? ImageKind : FileKind;
                    cached.name = !string.IsNullOrWhiteSpace(aiAtt.name) ? aiAtt.name : DeriveFileNameFromUrl(url, ext);
                    cached.path = localPath;
                    cached.mediaType = mediaType;
                    return cached;
                }
            }
            catch (Exception ex)
            {
                NeonLogger.LogWarning("DownloadAndCacheAttachment failed for " + url + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Authenticate a gateway fetch exactly like HermesRestClient does: the OAuth session
        /// cookie in remote/gated mode, the legacy bearer token otherwise. Non-gateway URLs are
        /// left untouched so third-party media hosts never see our credentials.
        /// </summary>
        private static void ApplyGatewayAuth(UnityWebRequest request, string url)
        {
            if (request == null || !IsHermesGatewayUrl(url))
                return;

            GlobalBackendSelector selector = GlobalBackendSelector.Instance;
            if (selector == null)
                return;

            string cookie = selector.RemoteAuthCookieHeader;
            if (!string.IsNullOrEmpty(cookie))
            {
                request.SetRequestHeader("Cookie", cookie);
                return;
            }

            if (!string.IsNullOrEmpty(selector.HermesToken))
                request.SetRequestHeader("Authorization", "Bearer " + selector.HermesToken);
        }

        private static ChatAttachment CacheDataUrlAttachment(AiChatAttachment aiAtt, string dataUrl)
        {
            if (string.IsNullOrWhiteSpace(dataUrl))
                return null;

            try
            {
                int comma = dataUrl.IndexOf(',');
                if (comma < 0)
                    return null;

                string meta = dataUrl.Substring(0, comma);
                string payload = dataUrl.Substring(comma + 1);
                if (meta.IndexOf(";base64", StringComparison.OrdinalIgnoreCase) < 0)
                    return null;

                string mediaType = aiAtt != null && !string.IsNullOrWhiteSpace(aiAtt.mediaType)
                    ? aiAtt.mediaType
                    : ExtractDataUrlMediaType(meta);
                string ext = GetExtensionFromMediaType(mediaType);
                byte[] data = Convert.FromBase64String(payload);
                if (!IsSupportedImagePayload(data, mediaType, ext))
                    return null;

                string dir = Path.Combine(Application.persistentDataPath, "Attachments");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string localPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
                File.WriteAllBytes(localPath, data);

                var cached = new ChatAttachment();
                cached.kind = ImageKind;
                cached.name = aiAtt != null && !string.IsNullOrWhiteSpace(aiAtt.name) ? aiAtt.name : "image" + ext;
                cached.path = localPath;
                cached.mediaType = mediaType;
                return cached;
            }
            catch (Exception ex)
            {
                NeonLogger.LogWarning("CacheDataUrlAttachment failed: " + ex.Message);
                return null;
            }
        }

        // === Kind / naming helpers ===

        public static bool IsImageExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext))
                return false;
            string e = ext.ToLowerInvariant();
            return e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".gif" ||
                   e == ".webp" || e == ".bmp" || e == ".svg";
        }

        public static string GetFileExtensionFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;
            string clean = url;
            int q = clean.IndexOf('?');
            if (q >= 0)
                clean = clean.Substring(0, q);
            int h = clean.IndexOf('#');
            if (h >= 0)
                clean = clean.Substring(0, h);

            string ext;
            try
            {
                ext = Path.GetExtension(clean);
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }

            if (string.IsNullOrEmpty(ext) || ext.Length > 6)
                return string.Empty;
            return ext.ToLowerInvariant();
        }

        public static string DeriveFileNameFromUrl(string url, string ext)
        {
            string fallback = "attachment" + (string.IsNullOrEmpty(ext) ? string.Empty : ext);
            if (string.IsNullOrWhiteSpace(url))
                return fallback;
            try
            {
                string clean = url;
                int q = clean.IndexOf('?');
                if (q >= 0)
                    clean = clean.Substring(0, q);
                int h = clean.IndexOf('#');
                if (h >= 0)
                    clean = clean.Substring(0, h);
                string fname = Path.GetFileName(clean.Replace('\\', '/'));
                if (!string.IsNullOrEmpty(fname) && fname.IndexOf('.') > 0)
                    return fname;
            }
            catch { }
            return fallback;
        }

        private static string SafeCacheFileName(string name, string ext)
        {
            string candidate = string.IsNullOrWhiteSpace(name) ? "attachment" + ext : name;
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalid.Length; i++)
                candidate = candidate.Replace(invalid[i], '_');
            candidate = candidate.Trim();
            if (candidate.Length == 0)
                candidate = "attachment" + ext;
            if (candidate.Length > 96)
                candidate = candidate.Substring(candidate.Length - 96);
            return candidate;
        }

        public static string GuessMediaTypeFromExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext))
                return "application/octet-stream";
            string e = ext.ToLowerInvariant();
            if (e == ".png")
                return "image/png";
            if (e == ".jpg" || e == ".jpeg")
                return "image/jpeg";
            if (e == ".webp")
                return "image/webp";
            if (e == ".gif")
                return "image/gif";
            if (e == ".bmp")
                return "image/bmp";
            if (e == ".svg")
                return "image/svg+xml";
            if (e == ".pdf")
                return "application/pdf";
            if (e == ".json")
                return "application/json";
            if (e == ".xml")
                return "application/xml";
            if (e == ".csv")
                return "text/csv";
            if (e == ".md")
                return "text/markdown";
            if (e == ".txt" || e == ".log")
                return "text/plain";
            if (e == ".zip")
                return "application/zip";
            return "application/octet-stream";
        }

        private static string ExtractDataUrlMediaType(string meta)
        {
            if (string.IsNullOrWhiteSpace(meta))
                return "image/png";

            const string prefix = "data:";
            if (!meta.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return "image/png";

            string mediaType = meta.Substring(prefix.Length);
            int semi = mediaType.IndexOf(';');
            if (semi >= 0)
                mediaType = mediaType.Substring(0, semi);

            return string.IsNullOrWhiteSpace(mediaType) ? "image/png" : mediaType.Trim();
        }

        private static string GetExtensionFromMediaType(string mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType))
                return ".png";

            string mt = mediaType.Trim().ToLowerInvariant();
            if (mt == "image/png")
                return ".png";
            if (mt == "image/jpeg" || mt == "image/jpg")
                return ".jpg";
            if (mt == "image/webp")
                return ".webp";
            if (mt == "image/gif")
                return ".gif";
            if (mt == "image/bmp")
                return ".bmp";
            if (mt == "image/svg+xml")
                return ".svg";
            return ".png";
        }

        private static bool IsSupportedImagePayload(byte[] data, string mediaType, string ext)
        {
            if (data == null || data.Length < 4)
                return false;

            if (HasImageMagic(data))
                return true;

            if (!string.IsNullOrWhiteSpace(mediaType) &&
                !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return false;

            string e = (ext ?? string.Empty).ToLowerInvariant();
            return e == ".svg";
        }

        private static bool HasImageMagic(byte[] data)
        {
            if (data == null || data.Length < 4)
                return false;

            if (data.Length >= 8 &&
                data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
                data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
                return true;

            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return true;

            if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38)
                return true;

            if (data[0] == 0x42 && data[1] == 0x4D)
                return true;

            if (data.Length >= 12 &&
                data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
                data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
                return true;

            return false;
        }

        // === Text helpers ===

        private static List<string> SnapshotTextSegments(ChatMessage message)
        {
            var snapshot = new List<string>();
            if (message == null || message.segments == null)
                return snapshot;

            for (int i = 0; i < message.segments.Count; i++)
            {
                ChatMessageSegment segment = message.segments[i];
                if (segment != null && string.Equals(segment.kind, ChatMessageSegment.TextKind, StringComparison.OrdinalIgnoreCase))
                    snapshot.Add(segment.text);
            }

            return snapshot;
        }

        private static void RestoreTextSegments(ChatMessage message, string content, List<string> segmentTexts)
        {
            if (message == null)
                return;

            message.content = content ?? string.Empty;
            if (message.segments == null || segmentTexts == null)
                return;

            int textIndex = 0;
            for (int i = 0; i < message.segments.Count; i++)
            {
                ChatMessageSegment segment = message.segments[i];
                if (segment == null || !string.Equals(segment.kind, ChatMessageSegment.TextKind, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (textIndex < segmentTexts.Count)
                    segment.text = segmentTexts[textIndex];
                textIndex++;
            }
        }

        private static List<ChatAttachment> CloneAttachments(IReadOnlyList<ChatAttachment> attachments)
        {
            var clone = new List<ChatAttachment>();
            if (attachments == null)
                return clone;

            for (int i = 0; i < attachments.Count; i++)
            {
                ChatAttachment attachment = attachments[i];
                if (attachment == null)
                    continue;

                var copy = new ChatAttachment();
                copy.kind = string.IsNullOrWhiteSpace(attachment.kind) ? ImageKind : attachment.kind;
                copy.name = attachment.name;
                copy.path = attachment.path;
                copy.mediaType = attachment.mediaType;
                clone.Add(copy);
            }

            return clone;
        }

        private static string TrimBlankEdges(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            int start = 0;
            int end = value.Length - 1;

            while (start <= end && (value[start] == '\n' || value[start] == ' ' || value[start] == '\t'))
                start++;

            while (end >= start && (value[end] == '\n' || value[end] == ' ' || value[end] == '\t'))
                end--;

            if (start > end)
                return string.Empty;

            return value.Substring(start, end - start + 1);
        }
    }
}
