// FilePathValidator.cs - Strict relative path validation for file transfer jail

using System;

namespace NeonCompanion.Runtime.Api.Hermes
{
    /// <summary>
    /// Pure path validation helpers (no Unity dependencies).
    /// </summary>
    public static class FilePathValidator
    {
        public static bool TryValidateRelativePath(string relativePath, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                error = "empty path";
                return false;
            }

            if (relativePath.IndexOf('\0') >= 0)
            {
                error = "NUL byte in path";
                return false;
            }

            string normalized = relativePath.Replace('\\', '/');

            if (normalized.StartsWith("/"))
            {
                error = "absolute path";
                return false;
            }

            if (normalized.StartsWith("//"))
            {
                error = "UNC path";
                return false;
            }

            if (normalized.Length >= 2 && normalized[1] == ':')
            {
                error = "drive-qualified path";
                return false;
            }

            string[] segments = normalized.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                if (string.IsNullOrEmpty(segment))
                {
                    if (i > 0 && i < segments.Length - 1)
                    {
                        error = "empty path segment";
                        return false;
                    }
                    continue;
                }

                if (segment == "..")
                {
                    error = "path traversal";
                    return false;
                }

                if (segment.IndexOf(':') >= 0)
                {
                    error = "alternate data stream";
                    return false;
                }

                if (segment.EndsWith(".") || segment.EndsWith(" "))
                {
                    error = "trailing dot or space in segment";
                    return false;
                }
            }

            return true;
        }
    }
}