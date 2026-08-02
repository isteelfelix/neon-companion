using System.Collections;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Avatar3D;
using NeonCompanion.Runtime.Data.Models;
using NUnit.Framework;
using UniVRM10;
using UnityEngine;
using UnityEngine.TestTools;

namespace NeonCompanion.Tests
{
    public sealed class BuiltInVrmRuntimeTests
    {
        [UnityTest]
        public IEnumerator IdleAnimationRunsWithoutRuntimeExceptions()
        {
            var service = new Avatar3DService();
            Task<bool> loadTask = service.LoadAvatar(
                BuiltInAvatarProfiles.ResourceScheme +
                BuiltInAvatarProfiles.NeonVrmResourcePath);
            while (!loadTask.IsCompleted)
                yield return null;

            Assert.IsFalse(loadTask.IsFaulted);
            Assert.IsTrue(loadTask.Result);
            Assert.IsTrue(service.SetAnimation("idle"));

            Vrm10Instance vrm =
                service.GetRuntimeRoot().GetComponent<Vrm10Instance>();
            Assert.IsNotNull(vrm);
            Assert.IsNotNull(vrm.Runtime.ControlRig);
            Assert.IsNotNull(vrm.Runtime.VrmAnimation);

            service.SetGazeNormalized(0.25f, -0.15f);
            for (int i = 0; i < 30; i++)
                yield return null;

            var rendererObject = new GameObject("BuiltInVrmRenderTest");
            var avatarRenderer = rendererObject.AddComponent<Avatar3DRenderer>();
            avatarRenderer.SetModelRoot(service.GetRuntimeTransform());
            for (int i = 0; i < 10; i++)
                yield return null;

            BustSpringAnimator bustAnimator =
                vrm.GetComponent<BustSpringAnimator>();
            Assert.IsNotNull(bustAnimator);
            Assert.AreEqual(4, bustAnimator.BustJointCount);

            Quaternion[] initialBustRotations = new Quaternion[4];
            int bustIndex = 0;
            for (int springIndex = 0;
                springIndex < vrm.SpringBone.Springs.Count;
                springIndex++)
            {
                Vrm10InstanceSpringBone.Spring spring =
                    vrm.SpringBone.Springs[springIndex];
                if (spring == null || string.IsNullOrEmpty(spring.Name) ||
                    !spring.Name.Contains("Bust"))
                    continue;
                for (int jointIndex = 0;
                    jointIndex < spring.Joints.Count - 1;
                    jointIndex++)
                {
                    initialBustRotations[bustIndex] =
                        spring.Joints[jointIndex].transform.localRotation;
                    bustIndex++;
                }
            }

            float greatestBustMotion = 0f;
            for (int frame = 0; frame < 90; frame++)
            {
                yield return null;
                bustIndex = 0;
                for (int springIndex = 0;
                    springIndex < vrm.SpringBone.Springs.Count;
                    springIndex++)
                {
                    Vrm10InstanceSpringBone.Spring spring =
                        vrm.SpringBone.Springs[springIndex];
                    if (spring == null || string.IsNullOrEmpty(spring.Name) ||
                        !spring.Name.Contains("Bust"))
                        continue;
                    for (int jointIndex = 0;
                        jointIndex < spring.Joints.Count - 1;
                        jointIndex++)
                    {
                        float motion = Quaternion.Angle(
                            initialBustRotations[bustIndex],
                            spring.Joints[jointIndex].transform.localRotation);
                        greatestBustMotion = Mathf.Max(
                            greatestBustMotion,
                            motion);
                        bustIndex++;
                    }
                }
            }
            Assert.Greater(
                greatestBustMotion,
                0.1f,
                "The bust springs did not react to the procedural idle force.");
            RenderTexture output = avatarRenderer.OutputTexture as RenderTexture;
            Assert.IsNotNull(output);
            RenderTexture previous = RenderTexture.active;
            var readback = new Texture2D(
                output.width,
                output.height,
                TextureFormat.RGBA32,
                false);
            RenderTexture.active = output;
            readback.ReadPixels(
                new Rect(0f, 0f, output.width, output.height),
                0,
                0,
                false);
            readback.Apply(false, false);
            RenderTexture.active = previous;

            Color32[] pixels = readback.GetPixels32();
            int visiblePixels = 0;
            int coloredPixels = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].a > 8)
                    visiblePixels++;
                if (pixels[i].r > 8 || pixels[i].g > 8 || pixels[i].b > 8)
                    coloredPixels++;
            }
            Renderer[] modelRenderers =
                service.GetRuntimeRoot().GetComponentsInChildren<Renderer>(true);
            Bounds modelBounds = modelRenderers[0].bounds;
            int enabledRenderers = 0;
            for (int i = 0; i < modelRenderers.Length; i++)
            {
                modelBounds.Encapsulate(modelRenderers[i].bounds);
                if (modelRenderers[i].enabled &&
                    modelRenderers[i].gameObject.activeInHierarchy)
                    enabledRenderers++;
            }
            Camera renderCamera =
                rendererObject.GetComponentInChildren<Camera>(true);
            string firstShader = modelRenderers[0].sharedMaterial != null &&
                modelRenderers[0].sharedMaterial.shader != null
                ? modelRenderers[0].sharedMaterial.shader.name
                : "null";
            Assert.Greater(
                visiblePixels,
                100,
                "The VRM render texture is fully transparent. Colored pixels: " +
                coloredPixels +
                "; renderers=" + modelRenderers.Length +
                "; enabled=" + enabledRenderers +
                "; bounds=" + modelBounds +
                "; camera=" +
                (renderCamera != null
                    ? renderCamera.transform.position.ToString()
                    : "null") +
                "; shader=" + firstShader);

            LogAssert.NoUnexpectedReceived();
            Object.Destroy(readback);
            Object.Destroy(rendererObject);
            service.Unload();
            yield return null;
        }
    }
}
