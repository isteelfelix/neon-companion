using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Avatar3D;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar
{
    public sealed class AvatarAssetInspection
    {
        public bool success;
        public string errorCode;
        public string error;
        public string avatarType;
        public string sourcePath;
        public string displayName;
        public string previewImagePath;
        public long totalFileSizeBytes;
        public int imageWidth;
        public int imageHeight;
        public AvatarCapabilities capabilities = new AvatarCapabilities();
        public List<string> animationClips = new List<string>();
        internal readonly List<AvatarImportFile> files = new List<AvatarImportFile>();
        internal GameObject previewInstance;
    }

    public sealed class AvatarAssetImportResult
    {
        public bool success;
        public string error;
        public AvatarProfile profile;
        public string assetDirectory;
    }

    internal sealed class AvatarImportFile
    {
        public string sourcePath;
        public string relativePath;
    }

    public static class AvatarAssetImporter
    {
        public const long MaxImageFileBytes = 20L * 1024L * 1024L;
        public const long MaxAssetBundleBytes = 100L * 1024L * 1024L;
        public const int MaxImageDimension = 8192;
        public const long MaxSpritePixels = 64000000L;
        public const int MaxMotionClips = 24;

        public static async Task<AvatarAssetInspection> InspectAsync(string sourcePath, string avatarType)
        {
            var result = NewInspection(sourcePath, avatarType);
            if (!PrepareSource(result))
                return result;

            try
            {
                switch (avatarType)
                {
                    case AvatarProfileTypes.Static2D:
                        InspectStaticImage(result);
                        break;
                    case AvatarProfileTypes.SpriteSheet:
                        InspectMotionPack(result);
                        break;
                    case AvatarProfileTypes.Generic3D:
                        await InspectGeneric3DAsync(result);
                        break;
                    case AvatarProfileTypes.Vrm:
                        await InspectVrmAsync(result);
                        break;
                    default:
                        Fail(result, "unsupported_type", "Unsupported avatar type.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Fail(result, "inspection_failed", ex.Message);
            }

            return result;
        }

        public static AvatarAssetImportResult Import(
            AvatarAssetInspection inspection,
            Avatar3DStateClipMapping stateClipMapping)
        {
            var result = new AvatarAssetImportResult();
            if (inspection == null || !inspection.success)
            {
                result.error = inspection != null ? inspection.error : "Asset has not been validated.";
                return result;
            }

            string profileId = "custom_" + Guid.NewGuid().ToString("N");
            string assetDirectory = Path.Combine(AppPaths.AvatarAssetsDirectory, profileId);
            result.assetDirectory = assetDirectory;

            try
            {
                Directory.CreateDirectory(assetDirectory);
                for (int i = 0; i < inspection.files.Count; i++)
                {
                    AvatarImportFile file = inspection.files[i];
                    string destination = GetSafeDestination(assetDirectory, file.relativePath);
                    string directory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrWhiteSpace(directory))
                        Directory.CreateDirectory(directory);
                    File.Copy(file.sourcePath, destination, false);
                }

                AvatarImportFile primary = inspection.files[0];
                string primaryPath = GetSafeDestination(assetDirectory, primary.relativePath);
                string previewPath = string.IsNullOrWhiteSpace(inspection.previewImagePath)
                    ? string.Empty
                    : FindCopiedPath(inspection, assetDirectory, inspection.previewImagePath);

                var profile = new AvatarProfile
                {
                    contractVersion = AvatarProfile.CurrentContractVersion,
                    avatarType = inspection.avatarType,
                    id = profileId,
                    name = inspection.displayName,
                    isBuiltIn = false,
                    is3D = inspection.avatarType == AvatarProfileTypes.Generic3D ||
                           inspection.avatarType == AvatarProfileTypes.Vrm,
                    imagePath = inspection.avatarType == AvatarProfileTypes.Static2D ||
                                inspection.avatarType == AvatarProfileTypes.SpriteSheet
                        ? previewPath
                        : string.Empty,
                    modelPath = inspection.avatarType == AvatarProfileTypes.Generic3D ||
                                inspection.avatarType == AvatarProfileTypes.Vrm
                        ? primaryPath
                        : string.Empty,
                    motionPackManifestPath = inspection.avatarType == AvatarProfileTypes.SpriteSheet
                        ? primaryPath
                        : string.Empty,
                    modelAnimationClips = new List<string>(inspection.animationClips),
                    stateClipMapping = inspection.avatarType == AvatarProfileTypes.Generic3D ||
                                       inspection.avatarType == AvatarProfileTypes.Vrm
                        ? stateClipMapping
                        : null,
                    capabilities = inspection.capabilities,
                    diagnostic = inspection.avatarType == AvatarProfileTypes.Vrm &&
                                 inspection.capabilities.isRestricted
                        ? "vrm_restricted_features"
                        : string.Empty,
                    source = new AvatarAssetSource
                    {
                        relativePath = MakeRelativeToDataRoot(primaryPath),
                        originalFileName = Path.GetFileName(inspection.sourcePath),
                        extension = Path.GetExtension(inspection.sourcePath).ToLowerInvariant(),
                        fileSizeBytes = inspection.totalFileSizeBytes
                    }
                };

                if (inspection.avatarType == AvatarProfileTypes.SpriteSheet)
                {
                    AvatarMotionPackLoadResult load = AvatarMotionPackLoader.LoadFromPath(primaryPath);
                    if (!load.isValid)
                        throw new InvalidDataException("Copied motion pack failed validation: " + load.error);

                    AvatarProfileMotionResolution resolved =
                        AvatarMotionPackLoader.BuildRuntimeClips(load.manifest, load.manifestPath);
                    profile.animationClips = resolved.animationClips;
                    profile.lipsyncClip = resolved.lipsyncClip;
                }

                profile.NormalizeContract();
                result.profile = profile;
                result.success = true;
            }
            catch (Exception ex)
            {
                result.error = ex.Message;
                DeleteImportDirectory(assetDirectory);
            }

            return result;
        }

        public static void DeleteImportDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return;

            string directoryName = Path.GetFileName(
                path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!IsImportDirectoryName(directoryName))
                return;

            string root = Path.GetFullPath(AppPaths.AvatarAssetsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (candidate.StartsWith(root, StringComparison.Ordinal))
                Directory.Delete(path, true);
        }

        public static void DeleteImportedProfileAssets(AvatarProfile profile)
        {
            if (profile == null || profile.source == null ||
                string.IsNullOrWhiteSpace(profile.source.relativePath) ||
                Path.IsPathRooted(profile.source.relativePath))
                return;

            string sourcePath = Path.GetFullPath(
                Path.Combine(AppPaths.RootData, profile.source.relativePath));
            string directory = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrWhiteSpace(directory) ||
                !string.Equals(Path.GetFileName(directory), profile.id, StringComparison.Ordinal))
                return;
            DeleteImportDirectory(directory);
        }

        private static bool IsImportDirectoryName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length != 39 ||
                !name.StartsWith("custom_", StringComparison.Ordinal))
                return false;
            for (int i = 7; i < name.Length; i++)
            {
                if (!Uri.IsHexDigit(name[i]))
                    return false;
            }
            return true;
        }

        private static AvatarAssetInspection NewInspection(string sourcePath, string avatarType)
        {
            return new AvatarAssetInspection
            {
                sourcePath = sourcePath,
                avatarType = avatarType,
                displayName = string.IsNullOrWhiteSpace(sourcePath)
                    ? string.Empty
                    : Path.GetFileNameWithoutExtension(sourcePath)
            };
        }

        private static bool PrepareSource(AvatarAssetInspection result)
        {
            if (string.IsNullOrWhiteSpace(result.sourcePath))
            {
                Fail(result, "empty_path", "Choose a source file.");
                return false;
            }

            try
            {
                result.sourcePath = Path.GetFullPath(result.sourcePath);
            }
            catch (Exception ex)
            {
                Fail(result, "invalid_path", ex.Message);
                return false;
            }

            if (!File.Exists(result.sourcePath))
            {
                Fail(result, "file_missing", "The selected file no longer exists.");
                return false;
            }
            if (IsSymbolicLink(result.sourcePath))
            {
                Fail(result, "unsafe_asset_path", "Symbolic-link sources are not imported.");
                return false;
            }

            return true;
        }

        private static void InspectStaticImage(AvatarAssetInspection result)
        {
            string extension = Path.GetExtension(result.sourcePath).ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
            {
                Fail(result, "unsupported_format", "Static 2D supports PNG, JPG, and JPEG.");
                return;
            }

            long fileSize = new FileInfo(result.sourcePath).Length;
            if (fileSize > MaxImageFileBytes)
            {
                Fail(result, "file_too_large", "Image exceeds the 20 MB file limit.");
                return;
            }

            int width;
            int height;
            if (!TryReadImageHeaderSize(result.sourcePath, out width, out height, out string imageError))
            {
                Fail(result, "corrupt_image", imageError);
                return;
            }

            if (width > MaxImageDimension || height > MaxImageDimension)
            {
                Fail(result, "image_dimensions_exceeded", "Image exceeds the 8192 x 8192 pixel limit.");
                return;
            }

            if (!TryDecodeImage(result.sourcePath, out imageError))
            {
                Fail(result, "corrupt_image", imageError);
                return;
            }

            result.totalFileSizeBytes = fileSize;
            result.imageWidth = width;
            result.imageHeight = height;
            result.previewImagePath = result.sourcePath;
            result.files.Add(new AvatarImportFile
            {
                sourcePath = result.sourcePath,
                relativePath = "avatar.png"
            });
            result.capabilities.canRender = true;
            result.capabilities.isVerified = true;
            result.capabilities.isRuntimeSupported = true;
            result.capabilities.evidence.Add("decoded_image");
            result.success = true;
        }

        private static void InspectMotionPack(AvatarAssetInspection result)
        {
            if (!string.Equals(Path.GetExtension(result.sourcePath), ".json", StringComparison.OrdinalIgnoreCase))
            {
                Fail(result, "unsupported_format", "Sprite-sheet avatars require a motion_pack.json file.");
                return;
            }

            if (new FileInfo(result.sourcePath).Length > MaxImageFileBytes)
            {
                Fail(result, "file_too_large", "Motion pack manifest exceeds the 20 MB file limit.");
                return;
            }

            AvatarMotionPackLoadResult load = AvatarMotionPackLoader.LoadFromPath(result.sourcePath);
            if (!load.isValid)
            {
                Fail(result, "invalid_motion_pack", load.error ?? "Motion pack is invalid.");
                return;
            }

            if (load.manifest.clips.Count > MaxMotionClips)
            {
                Fail(result, "too_many_clips", "Motion pack exceeds the 24 clip limit.");
                return;
            }

            string sourceRoot = Path.GetDirectoryName(result.sourcePath) ?? string.Empty;
            result.files.Add(new AvatarImportFile
            {
                sourcePath = result.sourcePath,
                relativePath = Path.GetFileName(result.sourcePath)
            });

            long totalBytes = new FileInfo(result.sourcePath).Length;
            long totalPixels = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < load.manifest.clips.Count; i++)
            {
                AvatarMotionClipEntry clip = load.manifest.clips[i];
                if (!InspectMotionImage(result, sourceRoot, clip, seen, ref totalBytes, ref totalPixels))
                    return;
                result.animationClips.Add(clip.action);
            }

            if (load.manifest.lipsyncClip != null &&
                !string.IsNullOrWhiteSpace(load.manifest.lipsyncClip.spriteSheetPath))
            {
                if (!InspectMotionImage(
                    result, sourceRoot, load.manifest.lipsyncClip, seen, ref totalBytes, ref totalPixels))
                    return;
            }

            if (totalBytes > MaxAssetBundleBytes)
            {
                Fail(result, "bundle_too_large", "Motion pack exceeds the 100 MB bundle limit.");
                return;
            }

            if (totalPixels > MaxSpritePixels)
            {
                Fail(result, "sprite_pixels_exceeded", "Motion pack exceeds the 64 megapixel decoded-image limit.");
                return;
            }

            result.totalFileSizeBytes = totalBytes;
            result.capabilities.canRender = true;
            result.capabilities.isVerified = true;
            result.capabilities.canAnimate = true;
            result.capabilities.hasStateAnimations = true;
            result.capabilities.hasLipsync = load.manifest.lipsyncClip != null &&
                !string.IsNullOrWhiteSpace(load.manifest.lipsyncClip.spriteSheetPath);
            result.capabilities.isRuntimeSupported = true;
            result.capabilities.animationClipCount = load.manifest.clips.Count;
            result.capabilities.evidence.Add("validated_motion_pack_v1");
            result.previewImagePath = ResolveWithinRoot(
                sourceRoot, load.manifest.clips[0].spriteSheetPath);
            result.success = true;
        }

        private static bool InspectMotionImage(
            AvatarAssetInspection result,
            string sourceRoot,
            AvatarMotionClipEntry clip,
            HashSet<string> seen,
            ref long totalBytes,
            ref long totalPixels)
        {
            string path;
            try
            {
                path = ResolveWithinRoot(sourceRoot, clip.spriteSheetPath);
            }
            catch (Exception ex)
            {
                Fail(result, "unsafe_asset_path", ex.Message);
                return false;
            }

            if (!File.Exists(path))
            {
                Fail(result, "sprite_missing", "Sprite sheet is missing: " + clip.spriteSheetPath);
                return false;
            }
            if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            {
                Fail(result, "sprite_format", "Motion-pack sprite sheets must be PNG files.");
                return false;
            }
            if (IsSymbolicLink(path))
            {
                Fail(result, "unsafe_asset_path", "Symbolic-link sprite sheets are not imported.");
                return false;
            }

            int width;
            int height;
            if (!TryReadImageHeaderSize(path, out width, out height, out string imageError))
            {
                Fail(result, "corrupt_sprite", clip.spriteSheetPath + ": " + imageError);
                return false;
            }

            if (width > MaxImageDimension || height > MaxImageDimension ||
                width % clip.columns != 0 || height % clip.rows != 0)
            {
                Fail(result, "invalid_sprite_grid",
                    "Sprite sheet dimensions must fit its grid and stay within 8192 x 8192.");
                return false;
            }

            if (!TryDecodeImage(path, out imageError))
            {
                Fail(result, "corrupt_sprite", clip.spriteSheetPath + ": " + imageError);
                return false;
            }

            if (seen.Add(path))
            {
                if (result.imageWidth == 0)
                {
                    result.imageWidth = width;
                    result.imageHeight = height;
                }
                totalBytes += new FileInfo(path).Length;
                totalPixels += (long)width * height;
                result.files.Add(new AvatarImportFile
                {
                    sourcePath = path,
                    relativePath = MakeRelativeWithinRoot(sourceRoot, path)
                });
            }

            return true;
        }

        private static async Task InspectGeneric3DAsync(AvatarAssetInspection result)
        {
            string extension = Path.GetExtension(result.sourcePath).ToLowerInvariant();
            if (extension != ".glb" && extension != ".gltf")
            {
                Fail(result, "unsupported_format", "Generic 3D supports GLB and glTF.");
                return;
            }

            if (new FileInfo(result.sourcePath).Length > Avatar3DLoader.MaxModelFileBytes)
            {
                Fail(result, "file_too_large", "Model exceeds the 100 MB file limit.");
                return;
            }

            JObject document;
            if (!TryReadGltfDocument(result.sourcePath, out document, out string documentError))
            {
                Fail(result, "invalid_gltf", documentError);
                return;
            }

            if (HasVrmMetadata(document))
            {
                Fail(result, "vrm_type_required", "This file contains VRM metadata. Choose the VRM avatar type.");
                return;
            }

            if (!CollectGltfFiles(result, document))
                return;

            Avatar3DLoadResult load = await Avatar3DLoader.LoadAsync(result.sourcePath);
            if (!load.Success || load.Instance == null)
            {
                Fail(result, load.ErrorCode ?? "model_import_failed",
                    load.Error ?? "glTFast could not import this model.");
                return;
            }

            result.previewInstance = load.Instance;
            result.animationClips.AddRange(load.AnimationNames);
            result.capabilities.canRender = true;
            result.capabilities.isVerified = true;
            result.capabilities.canAnimate = load.AnimationNames.Count > 0;
            result.capabilities.hasStateAnimations = load.AnimationNames.Count > 0;
            result.capabilities.hasLipsync = false;
            result.capabilities.isRuntimeSupported = true;
            result.capabilities.animationClipCount = load.AnimationNames.Count;
            result.capabilities.sceneNodeCount = load.SceneNodeCount;
            result.capabilities.rendererCount = load.RendererCount;
            result.capabilities.triangleCount = load.TriangleCount;
            result.capabilities.evidence.Add("gltfast_scene_import");
            result.success = true;
        }

        private static async Task InspectVrmAsync(AvatarAssetInspection result)
        {
            if (!string.Equals(Path.GetExtension(result.sourcePath), ".vrm", StringComparison.OrdinalIgnoreCase))
            {
                Fail(result, "unsupported_format", "VRM avatars require a .vrm file.");
                return;
            }

            long fileSize = new FileInfo(result.sourcePath).Length;
            if (fileSize > Avatar3DLoader.MaxModelFileBytes)
            {
                Fail(result, "file_too_large", "VRM exceeds the 100 MB file limit.");
                return;
            }

            JObject document;
            if (!TryReadGltfDocument(result.sourcePath, out document, out string documentError))
            {
                Fail(result, "invalid_vrm", documentError);
                return;
            }

            if (!HasVrmMetadata(document))
            {
                Fail(result, "missing_vrm_metadata", "The file is GLB data but does not declare VRM metadata.");
                return;
            }

            int nodeCount = CountArray(document, "nodes");
            int rendererCount = CountArray(document, "meshes");
            if (nodeCount > Avatar3DLoader.MaxSceneNodes || rendererCount > Avatar3DLoader.MaxRenderers)
            {
                Fail(result, "scene_limit_exceeded", "VRM exceeds the 512 node or 128 mesh catalog limit.");
                return;
            }

            result.totalFileSizeBytes = fileSize;
            result.files.Add(new AvatarImportFile
            {
                sourcePath = result.sourcePath,
                relativePath = Path.GetFileName(result.sourcePath)
            });
            Avatar3DLoadResult load = await Avatar3DLoader.LoadAsync(result.sourcePath);
            if (!load.Success || load.Instance == null)
            {
                Fail(result, load.ErrorCode ?? "invalid_vrm",
                    load.Error ?? "UniVRM could not import this VRM file.");
                return;
            }

            result.previewInstance = load.Instance;
            result.capabilities = load.Capabilities;
            result.animationClips.AddRange(load.AnimationNames);
            result.success = true;
        }

        private static bool CollectGltfFiles(AvatarAssetInspection result, JObject document)
        {
            string sourceRoot = Path.GetDirectoryName(result.sourcePath) ?? string.Empty;
            result.files.Add(new AvatarImportFile
            {
                sourcePath = result.sourcePath,
                relativePath = Path.GetFileName(result.sourcePath)
            });

            long totalBytes = new FileInfo(result.sourcePath).Length;
            var uris = new List<string>();
            CollectUris(document["buffers"] as JArray, uris);
            CollectUris(document["images"] as JArray, uris);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < uris.Count; i++)
            {
                string uri = uris[i];
                if (string.IsNullOrWhiteSpace(uri) ||
                    uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Uri.IsWellFormedUriString(uri, UriKind.Absolute))
                {
                    Fail(result, "external_uri", "Remote or absolute glTF asset URIs are not supported.");
                    return false;
                }

                string decoded;
                try
                {
                    decoded = Uri.UnescapeDataString(uri.Replace('/', Path.DirectorySeparatorChar));
                }
                catch (Exception ex)
                {
                    Fail(result, "invalid_uri", ex.Message);
                    return false;
                }

                string path;
                try
                {
                    path = ResolveWithinRoot(sourceRoot, decoded);
                }
                catch (Exception ex)
                {
                    Fail(result, "unsafe_asset_path", ex.Message);
                    return false;
                }

                if (!File.Exists(path))
                {
                    Fail(result, "sidecar_missing", "glTF sidecar is missing: " + uri);
                    return false;
                }
                if (IsSymbolicLink(path))
                {
                    Fail(result, "unsafe_asset_path", "Symbolic-link glTF sidecars are not imported.");
                    return false;
                }

                if (!seen.Add(path))
                    continue;

                totalBytes += new FileInfo(path).Length;
                if (totalBytes > MaxAssetBundleBytes)
                {
                    Fail(result, "bundle_too_large", "3D asset bundle exceeds the 100 MB limit.");
                    return false;
                }

                result.files.Add(new AvatarImportFile
                {
                    sourcePath = path,
                    relativePath = MakeRelativeWithinRoot(sourceRoot, path)
                });
            }

            result.totalFileSizeBytes = totalBytes;
            return true;
        }

        private static void CollectUris(JArray array, List<string> output)
        {
            if (array == null)
                return;

            for (int i = 0; i < array.Count; i++)
            {
                JObject item = array[i] as JObject;
                if (item == null)
                    continue;
                string uri = item.Value<string>("uri");
                if (!string.IsNullOrWhiteSpace(uri))
                    output.Add(uri);
            }
        }

        private static bool TryReadGltfDocument(string path, out JObject document, out string error)
        {
            document = null;
            error = null;
            try
            {
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension == ".gltf")
                {
                    document = JObject.Parse(File.ReadAllText(path));
                    return true;
                }

                using (var stream = File.OpenRead(path))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 20 || reader.ReadUInt32() != 0x46546C67)
                    {
                        error = "File is not a valid GLB container.";
                        return false;
                    }

                    uint version = reader.ReadUInt32();
                    uint declaredLength = reader.ReadUInt32();
                    uint jsonLength = reader.ReadUInt32();
                    uint jsonType = reader.ReadUInt32();
                    if (version != 2 || declaredLength > stream.Length || jsonType != 0x4E4F534A ||
                        jsonLength == 0 || jsonLength > int.MaxValue ||
                        jsonLength > stream.Length - stream.Position)
                    {
                        error = "GLB header or JSON chunk is invalid.";
                        return false;
                    }

                    byte[] bytes = reader.ReadBytes((int)jsonLength);
                    document = JObject.Parse(Encoding.UTF8.GetString(bytes).TrimEnd('\0', ' ', '\t', '\r', '\n'));
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool HasVrmMetadata(JObject document)
        {
            if (document == null)
                return false;

            JObject extensions = document["extensions"] as JObject;
            if (extensions != null &&
                (extensions["VRM"] != null || extensions["VRMC_vrm"] != null))
                return true;

            JArray used = document["extensionsUsed"] as JArray;
            if (used == null)
                return false;

            for (int i = 0; i < used.Count; i++)
            {
                string value = used[i] != null ? used[i].ToString() : string.Empty;
                if (value == "VRM" || value == "VRMC_vrm")
                    return true;
            }

            return false;
        }

        private static int CountArray(JObject document, string propertyName)
        {
            JArray array = document != null ? document[propertyName] as JArray : null;
            return array != null ? array.Count : 0;
        }

        private static bool TryReadImageHeaderSize(
            string path,
            out int width,
            out int height,
            out string error)
        {
            width = 0;
            height = 0;
            error = null;
            try
            {
                using (var stream = File.OpenRead(path))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 24)
                    {
                        error = "Image header is incomplete.";
                        return false;
                    }

                    byte first = reader.ReadByte();
                    byte second = reader.ReadByte();
                    stream.Position = 0;
                    if (first == 0x89 && second == 0x50)
                        return TryReadPngSize(reader, out width, out height, out error);
                    if (first == 0xFF && second == 0xD8)
                        return TryReadJpegSize(reader, out width, out height, out error);

                    error = "Image signature is not PNG or JPEG.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryReadPngSize(
            BinaryReader reader,
            out int width,
            out int height,
            out string error)
        {
            width = 0;
            height = 0;
            error = null;
            byte[] signature = reader.ReadBytes(8);
            byte[] expected = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            for (int i = 0; i < expected.Length; i++)
            {
                if (signature.Length <= i || signature[i] != expected[i])
                {
                    error = "PNG signature is invalid.";
                    return false;
                }
            }

            uint chunkLength = ReadUInt32BigEndian(reader);
            string chunkType = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (chunkLength < 8 || chunkType != "IHDR")
            {
                error = "PNG IHDR chunk is missing.";
                return false;
            }

            uint pngWidth = ReadUInt32BigEndian(reader);
            uint pngHeight = ReadUInt32BigEndian(reader);
            if (pngWidth > int.MaxValue || pngHeight > int.MaxValue)
            {
                error = "PNG dimensions are invalid.";
                return false;
            }
            width = (int)pngWidth;
            height = (int)pngHeight;
            return width > 0 && height > 0;
        }

        private static bool TryReadJpegSize(
            BinaryReader reader,
            out int width,
            out int height,
            out string error)
        {
            width = 0;
            height = 0;
            error = null;
            if (reader.ReadByte() != 0xFF || reader.ReadByte() != 0xD8)
            {
                error = "JPEG signature is invalid.";
                return false;
            }

            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                byte markerPrefix = reader.ReadByte();
                if (markerPrefix != 0xFF)
                    continue;

                byte marker;
                do
                {
                    if (reader.BaseStream.Position >= reader.BaseStream.Length)
                    {
                        error = "JPEG marker is incomplete.";
                        return false;
                    }
                    marker = reader.ReadByte();
                } while (marker == 0xFF);

                if (marker == 0xD8 || marker == 0xD9)
                    continue;
                if (marker == 0xDA)
                    break;

                ushort segmentLength = ReadUInt16BigEndian(reader);
                if (segmentLength < 2 ||
                    reader.BaseStream.Position + segmentLength - 2 > reader.BaseStream.Length)
                {
                    error = "JPEG segment is invalid.";
                    return false;
                }

                bool isStartOfFrame =
                    (marker >= 0xC0 && marker <= 0xC3) ||
                    (marker >= 0xC5 && marker <= 0xC7) ||
                    (marker >= 0xC9 && marker <= 0xCB) ||
                    (marker >= 0xCD && marker <= 0xCF);
                if (isStartOfFrame)
                {
                    reader.ReadByte();
                    height = ReadUInt16BigEndian(reader);
                    width = ReadUInt16BigEndian(reader);
                    return width > 0 && height > 0;
                }

                reader.BaseStream.Seek(segmentLength - 2, SeekOrigin.Current);
            }

            error = "JPEG size marker is missing.";
            return false;
        }

        private static bool TryDecodeImage(string path, out string error)
        {
            error = null;
            Texture2D texture = null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (texture.LoadImage(bytes))
                    return true;
                error = "Image data could not be decoded.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (texture != null)
                    UnityEngine.Object.Destroy(texture);
            }
        }

        private static ushort ReadUInt16BigEndian(BinaryReader reader)
        {
            return (ushort)((reader.ReadByte() << 8) | reader.ReadByte());
        }

        private static uint ReadUInt32BigEndian(BinaryReader reader)
        {
            return ((uint)reader.ReadByte() << 24) |
                   ((uint)reader.ReadByte() << 16) |
                   ((uint)reader.ReadByte() << 8) |
                   reader.ReadByte();
        }

        private static string ResolveWithinRoot(string root, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
                throw new InvalidDataException("Asset path must be relative.");

            string fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!candidate.StartsWith(fullRoot, StringComparison.Ordinal))
                throw new InvalidDataException("Asset path leaves the selected source folder.");
            return candidate;
        }

        private static bool IsSymbolicLink(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }

        private static string MakeRelativeWithinRoot(string root, string path)
        {
            string fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(fullRoot, StringComparison.Ordinal))
                throw new InvalidDataException("Asset path leaves the selected source folder.");
            return fullPath.Substring(fullRoot.Length);
        }

        private static string GetSafeDestination(string root, string relativePath)
        {
            return ResolveWithinRoot(root, relativePath);
        }

        private static string FindCopiedPath(
            AvatarAssetInspection inspection,
            string destinationRoot,
            string sourcePath)
        {
            for (int i = 0; i < inspection.files.Count; i++)
            {
                AvatarImportFile file = inspection.files[i];
                if (string.Equals(file.sourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                    return GetSafeDestination(destinationRoot, file.relativePath);
            }

            return string.Empty;
        }

        private static string MakeRelativeToDataRoot(string path)
        {
            string root = Path.GetFullPath(AppPaths.RootData)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(root, StringComparison.Ordinal)
                ? fullPath.Substring(root.Length)
                : fullPath;
        }

        private static void Fail(AvatarAssetInspection result, string code, string error)
        {
            result.success = false;
            result.errorCode = code;
            result.error = error;
        }
    }
}
