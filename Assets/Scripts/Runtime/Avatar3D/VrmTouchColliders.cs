using System.Collections.Generic;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar3D
{
    /// <summary>Marks a collider as the touch zone for one body region.</summary>
    internal sealed class VrmTouchZone : MonoBehaviour
    {
        internal AvatarTouchRegion Region;
    }

    /// <summary>
    /// Hangs trigger spheres off the bones a viewer can actually reach — head,
    /// hands, forearms. Nothing lower: the portrait frames bust-up, so feet and
    /// legs are never on screen and never touched. The rig is read through
    /// <see cref="Animator"/>, so this needs no VRM dependency and works for any
    /// humanoid.
    /// </summary>
    internal static class VrmTouchColliders
    {
        // Radii in metres for a roughly human-scale model. Divided out by the
        // bone's world scale so an unusually scaled rig still gets sane spheres.
        private const float HeadRadius = 0.13f;
        private const float HandRadius = 0.06f;
        private const float ForearmRadius = 0.05f;

        internal static void Build(Animator animator, List<GameObject> created)
        {
            if (animator == null || !animator.isHuman)
                return;

            Add(animator, HumanBodyBones.Head, AvatarTouchRegion.Head, HeadRadius, created);
            Add(animator, HumanBodyBones.LeftHand, AvatarTouchRegion.Hand, HandRadius, created);
            Add(animator, HumanBodyBones.RightHand, AvatarTouchRegion.Hand, HandRadius, created);
            Add(animator, HumanBodyBones.LeftLowerArm, AvatarTouchRegion.Forearm, ForearmRadius, created);
            Add(animator, HumanBodyBones.RightLowerArm, AvatarTouchRegion.Forearm, ForearmRadius, created);
        }

        private static void Add(
            Animator animator,
            HumanBodyBones bone,
            AvatarTouchRegion region,
            float radius,
            List<GameObject> created)
        {
            Transform anchor = animator.GetBoneTransform(bone);
            if (anchor == null)
                return;

            var go = new GameObject("AvatarTouch_" + region);
            go.transform.SetParent(anchor, false);
            go.transform.localPosition = Vector3.zero;

            Vector3 scale = go.transform.lossyScale;
            float worldScale = Mathf.Max(
                Mathf.Abs(scale.x),
                Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            if (worldScale < 1e-4f)
                worldScale = 1f;

            SphereCollider collider = go.AddComponent<SphereCollider>();
            collider.isTrigger = true;
            collider.radius = radius / worldScale;

            VrmTouchZone zone = go.AddComponent<VrmTouchZone>();
            zone.Region = region;

            created.Add(go);
        }
    }
}
