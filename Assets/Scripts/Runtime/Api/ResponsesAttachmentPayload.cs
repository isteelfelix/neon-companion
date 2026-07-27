using System;
using System.IO;
using NeonCompanion.Runtime.Api.Models;

namespace NeonCompanion.Runtime.Api
{
    /// <summary>One Responses API content item prepared from a local attachment.</summary>
    internal sealed class ResponsesAttachmentPayload
    {
        public string type;
        public string image_url;
        public string file_data;
        public string filename;
    }

    /// <summary>Shared validation and encoding for input_image and input_file.</summary>
    internal static class ResponsesAttachmentPayloadBuilder
    {
        internal const long MaxAttachmentBytes = 10 * 1024 * 1024;

        /// <summary>
        /// Comma-separated extension list for the OS file dialog. Must stay in sync with
        /// <see cref="IsAttachableExtension"/>, otherwise the picker offers files the composer
        /// then rejects.
        /// </summary>
        internal const string PickerExtensionFilter =
            "png,jpg,jpeg,gif,webp,bmp," +
            "pdf,txt,md,json,xml,csv,cs,py,js,ts,java,cpp,h,csproj," +
            "zip,tar,gz,tgz,7z,rar,bz2,xz";

        internal static bool TryBuild(AiChatAttachment attachment, out ResponsesAttachmentPayload payload, out string error)
        {
            payload = null;
            error = null;
            if (attachment == null || string.IsNullOrWhiteSpace(attachment.path))
            {
                error = "Attachment has no local path.";
                return false;
            }
            if (!File.Exists(attachment.path))
            {
                error = "Attachment file was not found: " + attachment.name;
                return false;
            }

            string extension = Path.GetExtension(attachment.path).ToLowerInvariant();
            bool image = IsImageExtension(extension);
            if (!image && !IsFileExtension(extension))
            {
                // Archives can be attached (Hermes stages them as plain files), but the Responses
                // API only accepts documents as input_file — say so instead of sending bytes it
                // will reject with an opaque 400.
                if (IsArchiveExtension(extension))
                {
                    error = "Archives (" + extension + ") can only be attached on a Hermes backend: " + attachment.name;
                    return false;
                }
                error = "Unsupported attachment type: " + extension;
                return false;
            }

            var info = new FileInfo(attachment.path);
            if (info.Length > MaxAttachmentBytes)
            {
                error = "Attachment exceeds the 10 MB limit: " + attachment.name;
                return false;
            }

            byte[] bytes = File.ReadAllBytes(attachment.path);
            string base64 = Convert.ToBase64String(bytes);
            payload = new ResponsesAttachmentPayload();
            if (image)
            {
                payload.type = "input_image";
                payload.image_url = "data:" + GuessImageMediaType(extension, attachment.mediaType) + ";base64," + base64;
            }
            else
            {
                payload.type = "input_file";
                payload.file_data = base64;
                payload.filename = SafeFileName(attachment.name, attachment.path);
            }
            return true;
        }

        /// <summary>Copies a selected attachment into app-owned storage before session persistence.</summary>
        internal static bool TryPersist(string sourcePath, string originalName, string persistentDataPath, out string persistedPath, out string error)
        {
            persistedPath = null;
            error = null;
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                error = "Attachment file was not found.";
                return false;
            }

            string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (!IsAttachableExtension(extension) || new FileInfo(sourcePath).Length > MaxAttachmentBytes)
            {
                error = "Attachment is unsupported or exceeds the 10 MB limit.";
                return false;
            }

            string root = Path.GetFullPath(Path.Combine(persistentDataPath ?? string.Empty, "Attachments"));
            Directory.CreateDirectory(root);
            string fullSourcePath = Path.GetFullPath(sourcePath);
            string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (fullSourcePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                persistedPath = fullSourcePath;
                return true;
            }
            string name = SafeFileName(originalName, sourcePath);
            persistedPath = Path.Combine(root, Guid.NewGuid().ToString("N") + "_" + name);
            File.Copy(sourcePath, persistedPath, false);
            return true;
        }

        internal static bool IsSupportedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return IsAttachableExtension(extension) && new FileInfo(path).Length <= MaxAttachmentBytes;
        }

        /// <summary>
        /// Everything the composer accepts. Wider than what the Responses API can encode: archives
        /// ride the Hermes file-staging path, which takes opaque bytes.
        /// </summary>
        internal static bool IsAttachableExtension(string extension)
        {
            return IsImageExtension(extension) || IsFileExtension(extension) || IsArchiveExtension(extension);
        }

        private static string SafeFileName(string name, string path)
        {
            string result = Path.GetFileName(string.IsNullOrWhiteSpace(name) ? path : name);
            return string.IsNullOrWhiteSpace(result) ? "attachment" : result;
        }

        private static bool IsImageExtension(string extension)
        {
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg" ||
                   extension == ".gif" || extension == ".webp" || extension == ".bmp";
        }

        private static bool IsFileExtension(string extension)
        {
            return extension == ".pdf" || extension == ".txt" || extension == ".md" ||
                   extension == ".json" || extension == ".xml" || extension == ".csv" ||
                   extension == ".cs" || extension == ".py" || extension == ".js" ||
                   extension == ".ts" || extension == ".java" || extension == ".cpp" ||
                   extension == ".h" || extension == ".csproj";
        }

        /// <summary>Archives Desktop already accepts; staged as opaque files, never as documents.</summary>
        private static bool IsArchiveExtension(string extension)
        {
            return extension == ".zip" || extension == ".tar" || extension == ".gz" ||
                   extension == ".tgz" || extension == ".7z" || extension == ".rar" ||
                   extension == ".bz2" || extension == ".xz";
        }

        private static string GuessImageMediaType(string extension, string supplied)
        {
            if (!string.IsNullOrWhiteSpace(supplied) && supplied.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return supplied;
            if (extension == ".png") return "image/png";
            if (extension == ".jpg" || extension == ".jpeg") return "image/jpeg";
            if (extension == ".gif") return "image/gif";
            if (extension == ".webp") return "image/webp";
            return "image/bmp";
        }
    }
}
