// FilePathRootResolver.cs - Maps protocol roots to local filesystem directories

using System;
using System.IO;
using NeonCompanion.Runtime.Core;
using UnityEngine;

namespace NeonCompanion.Runtime.Api.Hermes
{
    public sealed class FilePathRootResolver
    {
        private string _workspaceOverride;

        public void SetWorkspace(string cwd)
        {
            _workspaceOverride = string.IsNullOrWhiteSpace(cwd) ? null : cwd;
        }

        public bool TryResolveRoot(string rootName, out string absoluteRoot, out string error)
        {
            absoluteRoot = null;
            error = null;

            if (string.IsNullOrWhiteSpace(rootName))
            {
                error = "missing root";
                return false;
            }

            string key = rootName.Trim().ToLowerInvariant();
            switch (key)
            {
                case FileTransferProtocol.RootDownloads:
                    absoluteRoot = Path.Combine(AppPaths.RootData, "Downloads");
                    break;
                case FileTransferProtocol.RootWorkspace:
                    absoluteRoot = !string.IsNullOrWhiteSpace(_workspaceOverride)
                        ? _workspaceOverride
                        : Path.Combine(AppPaths.RootData, "Workspace");
                    break;
                case FileTransferProtocol.RootTemp:
                    absoluteRoot = Path.Combine(Application.temporaryCachePath, "HermesTransfer");
                    break;
                case FileTransferProtocol.RootSession:
                    absoluteRoot = Path.Combine(AppPaths.RootData, "Session");
                    break;
                default:
                    error = "unknown root: " + rootName;
                    return false;
            }

            try
            {
                if (!Directory.Exists(absoluteRoot))
                    Directory.CreateDirectory(absoluteRoot);

                absoluteRoot = Path.GetFullPath(absoluteRoot);
            }
            catch (Exception ex)
            {
                error = "failed to prepare root: " + ex.Message;
                return false;
            }

            return true;
        }

        public bool TryResolveDestination(string rootName, string relativePath, out string absolutePath, out string error)
        {
            absolutePath = null;
            error = null;

            if (!FilePathValidator.TryValidateRelativePath(relativePath, out error))
                return false;

            if (!TryResolveRoot(rootName, out string rootDir, out error))
                return false;

            string combined = Path.Combine(rootDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string fullPath = Path.GetFullPath(combined);
            string rootFull = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!IsInsideRoot(fullPath, rootFull))
            {
                error = "resolved path escapes root";
                return false;
            }

            absolutePath = fullPath;
            return true;
        }

        private static bool IsInsideRoot(string fullPath, string rootFull)
        {
            if (string.Equals(fullPath, rootFull, StringComparison.OrdinalIgnoreCase))
                return true;

            string rootWithSeparator = rootFull + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                return true;

            if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
            {
                string altRootWithSeparator = rootFull + Path.AltDirectorySeparatorChar;
                if (fullPath.StartsWith(altRootWithSeparator, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}