using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Avatar;
using NeonCompanion.Runtime.Avatar3D;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Platform;
using NeonCompanion.Runtime.Voice;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NeonCompanion.Tests
{
    public sealed class AvatarPhaseDTests
    {
        private readonly List<string> _temporaryDirectories = new List<string>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _temporaryDirectories.Count; i++)
            {
                if (Directory.Exists(_temporaryDirectories[i]))
                    Directory.Delete(_temporaryDirectories[i], true);
            }
            _temporaryDirectories.Clear();
        }

        [Test]
        public void LegacyStaticAndSpriteProfilesRemainReadable()
        {
            var staticProfile = new AvatarProfile
            {
                id = "legacy-static",
                imagePath = "legacy/avatar.png"
            };
            staticProfile.NormalizeContract();
            Assert.AreEqual(AvatarProfileTypes.Static2D, staticProfile.avatarType);
            Assert.AreEqual(AvatarProfile.CurrentContractVersion, staticProfile.contractVersion);
            Assert.IsTrue(staticProfile.capabilities.canRender);
            Assert.IsFalse(staticProfile.capabilities.isVerified);
            CollectionAssert.Contains(staticProfile.capabilities.evidence, "legacy_profile_fields");

            var spriteProfile = new AvatarProfile
            {
                id = "legacy-sprite",
                imagePath = "legacy/idle.png",
                animationClips = new List<SpriteSheetAnimation>
                {
                    new SpriteSheetAnimation { clipName = "idle", spriteSheetPath = "idle.png" }
                },
                lipsyncClip = new SpriteSheetAnimation
                {
                    clipName = "lipsync",
                    spriteSheetPath = "mouth.png"
                }
            };
            spriteProfile.NormalizeContract();
            Assert.AreEqual(AvatarProfileTypes.SpriteSheet, spriteProfile.avatarType);
            Assert.IsTrue(spriteProfile.capabilities.canRender);
            Assert.IsTrue(spriteProfile.capabilities.canAnimate);
            Assert.IsTrue(spriteProfile.capabilities.hasLipsync);
            Assert.AreEqual(1, spriteProfile.capabilities.animationClipCount);
        }

        [Test]
        public void GenericMappingAndFutureContractFallbackStayDeterministic()
        {
            var mapping = new Avatar3DStateClipMapping
            {
                idle = "Idle_Clip",
                thinking = "Think_Clip",
                talking = "Talk_Clip"
            };
            Assert.AreEqual("Think_Clip", mapping.GetClip("thinking"));
            Assert.AreEqual("Talk_Clip", mapping.GetClip("talking"));
            Assert.AreEqual("Idle_Clip", mapping.GetClip("unknown"));

            var future = new AvatarProfile
            {
                contractVersion = AvatarProfile.CurrentContractVersion + 1,
                avatarType = AvatarProfileTypes.Generic3D,
                modelPath = "future.glb",
                capabilities = new AvatarCapabilities
                {
                    canRender = true,
                    canAnimate = true,
                    isRuntimeSupported = true
                }
            };
            future.NormalizeContract();
            Assert.AreEqual("unsupported_contract_version", future.diagnostic);
            Assert.IsFalse(future.capabilities.canRender);
            Assert.IsFalse(future.capabilities.canAnimate);
            Assert.IsFalse(future.capabilities.isRuntimeSupported);
        }

        [Test]
        public void CompanionSnapshotExcludesPersonaAndTransportState()
        {
            var profile = new AvatarProfile
            {
                id = "private-avatar",
                name = "Display name",
                avatarType = AvatarProfileTypes.Generic3D,
                modelPath = "model.glb",
                systemPrompt = "must not cross the process boundary"
            };
            CompanionDisplaySnapshot snapshot =
                CompanionDisplaySnapshot.FromProfile(profile, profile.id, profile.name);
            string json = JsonUtility.ToJson(snapshot);
            StringAssert.DoesNotContain("systemPrompt", json);
            StringAssert.DoesNotContain("must not cross", json);
            StringAssert.DoesNotContain("apiKey", json);
            Assert.AreEqual(profile.modelPath, snapshot.modelPath);
        }

        [UnityTest]
        public IEnumerator ChangedSourceIsRejectedBeforeCopy()
        {
            string directory = CreateTemporaryDirectory();
            string path = Path.Combine(directory, "avatar.png");
            File.WriteAllBytes(path, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScL8WQAAAABJRU5ErkJggg=="));

            Task<AvatarAssetInspection> task =
                AvatarAssetImporter.InspectAsync(path, AvatarProfileTypes.Static2D);
            while (!task.IsCompleted)
                yield return null;
            Assert.IsFalse(task.IsFaulted);
            AvatarAssetInspection inspection = task.Result;
            Assert.IsTrue(inspection.success, inspection.error);

            using (FileStream stream = File.Open(path, FileMode.Append, FileAccess.Write, FileShare.None))
                stream.WriteByte(0);

            AvatarAssetImportResult imported = AvatarAssetImporter.Import(inspection, null);
            Assert.IsFalse(imported.success);
            Assert.AreEqual("source_changed", imported.errorCode);
            Assert.IsFalse(Directory.Exists(imported.assetDirectory));
        }

        [Test]
        public void TemporaryPreviewObjectsUseEditModeSafeCleanup()
        {
            var preview = new GameObject("TemporaryAvatarPreview");
            AvatarAssetImporter.DestroyTemporaryObject(preview);
            Assert.IsTrue(preview == null);
        }

        [UnityTest]
        public IEnumerator OversizedImageIsRejectedBeforeDecode()
        {
            string directory = CreateTemporaryDirectory();
            string path = Path.Combine(directory, "oversized.png");
            using (FileStream stream = File.Create(path))
                stream.SetLength(AvatarAssetImporter.MaxImageFileBytes + 1L);

            Task<AvatarAssetInspection> task =
                AvatarAssetImporter.InspectAsync(path, AvatarProfileTypes.Static2D);
            while (!task.IsCompleted)
                yield return null;
            Assert.IsFalse(task.Result.success);
            Assert.AreEqual("file_too_large", task.Result.errorCode);
        }

        [UnityTest]
        public IEnumerator CatalogLimitsRejectWorkBeforeRuntimeInstantiation()
        {
            string directory = CreateTemporaryDirectory();
            string nodesPath = Path.Combine(directory, "too-many-nodes.gltf");
            var nodes = new StringBuilder();
            nodes.Append("{\"asset\":{\"version\":\"2.0\"},\"nodes\":[");
            for (int i = 0; i <= Avatar3DLoader.MaxSceneNodes; i++)
            {
                if (i > 0)
                    nodes.Append(',');
                nodes.Append("{}");
            }
            nodes.Append("]}");
            File.WriteAllText(nodesPath, nodes.ToString());

            Task<AvatarAssetInspection> nodesTask =
                AvatarAssetImporter.InspectAsync(nodesPath, AvatarProfileTypes.Generic3D);
            while (!nodesTask.IsCompleted)
                yield return null;
            Assert.IsFalse(nodesTask.Result.success);
            Assert.AreEqual("scene_limit_exceeded", nodesTask.Result.errorCode);

            string trianglesPath = Path.Combine(directory, "too-many-triangles.gltf");
            long indexCount = (Avatar3DLoader.MaxTriangles + 1L) * 3L;
            File.WriteAllText(
                trianglesPath,
                "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"mesh\":0}]," +
                "\"meshes\":[{\"primitives\":[{\"indices\":0}]}]," +
                "\"accessors\":[{\"count\":" + indexCount + "}]}");
            Task<AvatarAssetInspection> trianglesTask =
                AvatarAssetImporter.InspectAsync(trianglesPath, AvatarProfileTypes.Generic3D);
            while (!trianglesTask.IsCompleted)
                yield return null;
            Assert.IsFalse(trianglesTask.Result.success);
            Assert.AreEqual("scene_limit_exceeded", trianglesTask.Result.errorCode);

            string malformedPath = Path.Combine(directory, "wrong-length.glb");
            using (var stream = File.Create(malformedPath))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x46546C67u);
                writer.Write(2u);
                writer.Write(20u);
                writer.Write(4u);
                writer.Write(0x4E4F534Au);
                writer.Write(Encoding.UTF8.GetBytes("{}  "));
            }
            Task<AvatarAssetInspection> malformedTask =
                AvatarAssetImporter.InspectAsync(malformedPath, AvatarProfileTypes.Generic3D);
            while (!malformedTask.IsCompleted)
                yield return null;
            Assert.IsFalse(malformedTask.Result.success);
            Assert.AreEqual("invalid_gltf", malformedTask.Result.errorCode);
        }

        [UnityTest]
        public IEnumerator GenericGltfAndGlbMappingsLoadThroughRuntime()
        {
            string directory = CreateTemporaryDirectory();
            byte[] geometry = BuildTriangleGeometry();
            string gltfPath = Path.Combine(directory, "triangle.gltf");
            File.WriteAllText(gltfPath, BuildTriangleGltf(
                "data:application/octet-stream;base64," + Convert.ToBase64String(geometry)));
            string glbPath = Path.Combine(directory, "triangle.glb");
            File.WriteAllBytes(glbPath, BuildTriangleGlb(geometry));

            string[] paths = { gltfPath, glbPath };
            for (int i = 0; i < paths.Length; i++)
            {
                Task<Avatar3DLoadResult> task = Avatar3DLoader.LoadAsync(paths[i]);
                while (!task.IsCompleted)
                    yield return null;
                Assert.IsFalse(task.IsFaulted);
                Avatar3DLoadResult result = task.Result;
                Assert.IsFalse(result.Success);
                Assert.IsNull(result.Instance);
                StringAssert.Contains("not enabled", result.Error);
            }
        }

        [UnityTest]
        public IEnumerator StopCancelsBackendWaitAndAllowsImmediateReplay()
        {
            var host = new GameObject("VoiceOutputManagerTest");
            VoiceOutputManager manager = host.AddComponent<VoiceOutputManager>();
            var voice = new NonCompletingVoiceService();
            int started = 0;
            int completed = 0;
            manager.Initialize(voice, delegate { return true; }, delegate { return false; });
            manager.OnPlaybackStarted += delegate { started++; };
            manager.OnPlaybackCompleted += delegate { completed++; };

            manager.EnqueueResponse("first");
            Assert.AreEqual(1, voice.SpeakCalls);
            Assert.AreEqual(1, started);
            manager.StopSpeakingAndClear();
            Assert.AreEqual(1, voice.StopCalls);
            Assert.AreEqual(1, completed);

            manager.EnqueueResponse("second");
            for (int i = 0; i < 20 && voice.SpeakCalls < 2; i++)
                yield return null;
            Assert.AreEqual(2, voice.SpeakCalls,
                "A backend that omits OnPlaybackComplete must not stall the TTS queue after stop/barge-in.");

            voice.Complete();
            yield return null;
            Assert.AreEqual(2, completed);
            UnityEngine.Object.DestroyImmediate(host);
        }

        [UnityTest]
        [Explicit("Requires Windows Unity plus NEON_PHASE_D_VRM0_FIXTURE pointing to a licensed VRM 0.x model.")]
        public IEnumerator VrmZeroAndOneFixturesLoadThroughUniVrm()
        {
            string legacyPath = Environment.GetEnvironmentVariable("NEON_PHASE_D_VRM0_FIXTURE");
            Assert.IsFalse(string.IsNullOrWhiteSpace(legacyPath));
            string currentPath = Path.Combine(
                Application.dataPath,
                "Resources",
                "Avatars",
                "neon",
                "Neon.vrm");
            string[] paths = { currentPath, legacyPath };
            for (int i = 0; i < paths.Length; i++)
            {
                AssertVrmGeneration(paths[i], i == 0);
                Task<Avatar3DLoadResult> task = Avatar3DLoader.LoadAsync(paths[i]);
                while (!task.IsCompleted)
                    yield return null;
                Assert.IsFalse(task.IsFaulted);
                Avatar3DLoadResult result = task.Result;
                Assert.IsTrue(result.Success, paths[i] + ": " + result.Error);
                Assert.Greater(result.RendererCount, 0);
                Assert.LessOrEqual(result.SceneNodeCount, Avatar3DLoader.MaxSceneNodes);
                Assert.LessOrEqual(result.TriangleCount, Avatar3DLoader.MaxTriangles);
                UnityEngine.Object.DestroyImmediate(result.Instance);
            }
        }

        private static void AssertVrmGeneration(string path, bool expectVrm1)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                Assert.AreEqual(0x46546C67u, reader.ReadUInt32());
                Assert.AreEqual(2u, reader.ReadUInt32());
                reader.ReadUInt32();
                uint jsonLength = reader.ReadUInt32();
                Assert.AreEqual(0x4E4F534Au, reader.ReadUInt32());
                string document = Encoding.UTF8.GetString(reader.ReadBytes((int)jsonLength));
                if (expectVrm1)
                {
                    StringAssert.Contains("\"VRMC_vrm\"", document);
                }
                else
                {
                    StringAssert.Contains("\"VRM\"", document);
                    StringAssert.DoesNotContain("\"VRMC_vrm\"", document);
                }
            }
        }

        private string CreateTemporaryDirectory()
        {
            string path = Path.Combine(
                Application.temporaryCachePath,
                "neon-avatar-phase-d-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            _temporaryDirectories.Add(path);
            return path;
        }

        private static byte[] BuildTriangleGeometry()
        {
            var bytes = new byte[42];
            float[] positions =
            {
                0f, 0f, 0f,
                1f, 0f, 0f,
                0f, 1f, 0f
            };
            for (int i = 0; i < positions.Length; i++)
            {
                byte[] value = BitConverter.GetBytes(positions[i]);
                Buffer.BlockCopy(value, 0, bytes, i * 4, value.Length);
            }
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)0), 0, bytes, 36, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)1), 0, bytes, 38, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)2), 0, bytes, 40, 2);
            return bytes;
        }

        private static string BuildTriangleGltf(string uri)
        {
            string buffer = string.IsNullOrEmpty(uri)
                ? "{\"byteLength\":42}"
                : "{\"byteLength\":42,\"uri\":\"" + uri + "\"}";
            return "{\"asset\":{\"version\":\"2.0\"}," +
                "\"scene\":0,\"scenes\":[{\"nodes\":[0]}]," +
                "\"nodes\":[{\"mesh\":0}]," +
                "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1}]}]," +
                "\"buffers\":[" + buffer + "]," +
                "\"bufferViews\":[" +
                "{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36,\"target\":34962}," +
                "{\"buffer\":0,\"byteOffset\":36,\"byteLength\":6,\"target\":34963}]," +
                "\"accessors\":[" +
                "{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"," +
                "\"min\":[0,0,0],\"max\":[1,1,0]}," +
                "{\"bufferView\":1,\"componentType\":5123,\"count\":3,\"type\":\"SCALAR\"}]}";
        }

        private static byte[] BuildTriangleGlb(byte[] geometry)
        {
            byte[] json = Encoding.UTF8.GetBytes(BuildTriangleGltf(null));
            int jsonLength = (json.Length + 3) & ~3;
            int binaryLength = (geometry.Length + 3) & ~3;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x46546C67u);
                writer.Write(2u);
                writer.Write((uint)(12 + 8 + jsonLength + 8 + binaryLength));
                writer.Write((uint)jsonLength);
                writer.Write(0x4E4F534Au);
                writer.Write(json);
                for (int i = json.Length; i < jsonLength; i++)
                    writer.Write((byte)0x20);
                writer.Write((uint)binaryLength);
                writer.Write(0x004E4942u);
                writer.Write(geometry);
                for (int i = geometry.Length; i < binaryLength; i++)
                    writer.Write((byte)0);
                return stream.ToArray();
            }
        }

        private sealed class NonCompletingVoiceService : IVoiceService
        {
            public bool IsRecording { get; private set; }
            public bool IsSpeaking { get; private set; }
            public bool IsAvailable { get { return true; } }
            public bool AutoStopOnSilence { get; set; }
            public int SpeakCalls { get; private set; }
            public int StopCalls { get; private set; }

#pragma warning disable CS0067
            public event Action<string> OnSpeechRecognized;
            public event Action OnPlaybackStarted;
            public event Action OnPlaybackComplete;
            public event Action<string, float> OnRecordingComplete;
            public event Action<string, float> OnSpeechAudioReady;
#pragma warning restore CS0067

            public void StartRecording()
            {
                IsRecording = true;
            }

            public byte[] StopRecording()
            {
                IsRecording = false;
                return new byte[0];
            }

            public void Speak(string text)
            {
                SpeakCalls++;
                IsSpeaking = true;
                if (OnPlaybackStarted != null)
                    OnPlaybackStarted();
            }

            public void StopSpeaking()
            {
                StopCalls++;
                IsSpeaking = false;
            }

            public void Complete()
            {
                IsSpeaking = false;
                if (OnPlaybackComplete != null)
                    OnPlaybackComplete();
            }
        }
    }
}
