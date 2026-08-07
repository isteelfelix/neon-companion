using System;
using System.Collections.Generic;
using UniGLTF.SpringBoneJobs.Blittables;
using UniVRM10;
using Unity.Mathematics;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar3D
{
    /// <summary>
    /// Gives the authored bust springs a small, continuous source of motion and
    /// adds inertia while the avatar is being turned. VRM spring parameters only
    /// describe a response; without acceleration a perfectly still model remains
    /// perfectly still regardless of how soft the springs are.
    /// </summary>
    public sealed class BustSpringAnimator : MonoBehaviour
    {
        private sealed class DrivenJoint
        {
            public VRM10SpringBoneJoint Joint;
            public BlittableJointMutable Original;
            public float Side;
        }

        private readonly List<DrivenJoint> _joints = new List<DrivenJoint>();
        private Vrm10Instance _vrm;
        // Vrm10Instance.Runtime is a lazy factory: reading it builds the control rig and
        // reparents a new GameObject. OnDisable runs inside SetActive(false), where Unity
        // forbids reparenting, so touching the property there throws and leaves a half-built
        // rig behind. Only ever use a reference captured while the object was safely active.
        private Vrm10Runtime _runtime;
        private float _elapsed;
        private float _turnVelocity;

        private const float PrimaryFrequency = 0.34f;
        private const float SecondaryFrequency = 0.83f;
        private const float IdleForwardForce = 0.04f;
        private const float IdleLiftForce = 0.014f;
        private const float IdleSeparationForce = 0.006f;
        private const float TurnForce = 0.42f;

        public int BustJointCount
        {
            get { return _joints.Count; }
        }

        public void Configure(Vrm10Instance vrm)
        {
            if (_vrm == vrm && _joints.Count > 0)
                return;

            RestoreOriginalSettings();
            _vrm = vrm;
            _runtime = null;
            _joints.Clear();
            _elapsed = 0f;
            _turnVelocity = 0f;

            if (_vrm == null || _vrm.SpringBone == null)
                return;

            HashSet<VRM10SpringBoneJoint> found =
                new HashSet<VRM10SpringBoneJoint>();
            List<Vrm10InstanceSpringBone.Spring> springs =
                _vrm.SpringBone.Springs;
            for (int springIndex = 0; springIndex < springs.Count; springIndex++)
            {
                Vrm10InstanceSpringBone.Spring spring = springs[springIndex];
                if (spring == null || string.IsNullOrEmpty(spring.Name) ||
                    spring.Name.IndexOf("Bust", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string firstJointName = spring.Joints.Count > 0 &&
                    spring.Joints[0] != null
                    ? spring.Joints[0].name
                    : string.Empty;
                float side = firstJointName.IndexOf(
                    "_L_", StringComparison.OrdinalIgnoreCase) >= 0
                    ? -1f
                    : 1f;
                // In VRM 1.0 the final entry is the tail position, not a simulated
                // joint. UniVRM excludes it from the mutable joint buffer too.
                for (int jointIndex = 0;
                    jointIndex < spring.Joints.Count - 1;
                    jointIndex++)
                {
                    VRM10SpringBoneJoint joint = spring.Joints[jointIndex];
                    if (joint == null || !found.Add(joint))
                        continue;

                    _joints.Add(new DrivenJoint
                    {
                        Joint = joint,
                        Original = joint.Blittable,
                        Side = side
                    });
                }
            }
        }

        public void SetTurnVelocity(float normalizedVelocity)
        {
            _turnVelocity = Mathf.Clamp(normalizedVelocity, -1f, 1f);
        }

        private void Update()
        {
            if (_vrm == null || _joints.Count == 0 || Time.deltaTime <= 0f)
                return;

            // Safe to build here: Update never runs during a SetActive transition.
            if (_runtime == null)
                _runtime = _vrm.Runtime;

            _elapsed += Time.deltaTime;
            float primary = Mathf.Sin(_elapsed * Mathf.PI * 2f * PrimaryFrequency);
            float secondary = Mathf.Sin(
                _elapsed * Mathf.PI * 2f * SecondaryFrequency + 0.65f);
            float lift = Mathf.Sin(
                _elapsed * Mathf.PI * 2f * PrimaryFrequency - 0.55f);

            Transform root = _vrm.transform;
            Vector3 sharedMotion =
                root.forward * (primary * IdleForwardForce +
                    secondary * IdleForwardForce * 0.28f) +
                root.up * lift * IdleLiftForce -
                root.right * _turnVelocity * TurnForce;

            for (int i = 0; i < _joints.Count; i++)
            {
                DrivenJoint driven = _joints[i];
                if (driven.Joint == null)
                    continue;

                float separationWave = Mathf.Sin(
                    _elapsed * Mathf.PI * 2f * (PrimaryFrequency * 0.73f) +
                    driven.Side * 0.8f);
                Vector3 originalGravity = new Vector3(
                    driven.Original.gravityDir.x,
                    driven.Original.gravityDir.y,
                    driven.Original.gravityDir.z) * driven.Original.gravityPower;
                Vector3 force = originalGravity + sharedMotion +
                    root.right * driven.Side * separationWave *
                    IdleSeparationForce;
                float power = force.magnitude;
                Vector3 direction = power > 0.0001f
                    ? force / power
                    : Vector3.down;

                _runtime.SpringBone.SetJointLevel(
                    driven.Joint.transform,
                    new BlittableJointMutable(
                        driven.Original.stiffnessForce,
                        power,
                        new float3(direction.x, direction.y, direction.z),
                        driven.Original.dragForce,
                        driven.Original.radius,
                        (float)driven.Original.anglelimitType,
                        driven.Original.anglelimit1,
                        driven.Original.anglelimit2,
                        driven.Original.anglelimitOffset));
            }
        }

        private void OnDisable()
        {
            RestoreOriginalSettings();
            _turnVelocity = 0f;
        }

        private void RestoreOriginalSettings()
        {
            // A null runtime means Update() never pushed an override, so there is nothing to
            // restore. Never fall back to _vrm.Runtime here: this also runs from OnDisable.
            if (_vrm == null || _runtime == null)
                return;

            for (int i = 0; i < _joints.Count; i++)
            {
                DrivenJoint driven = _joints[i];
                if (driven.Joint != null)
                    _runtime.SpringBone.SetJointLevel(
                        driven.Joint.transform,
                        driven.Original);
            }
        }
    }
}
