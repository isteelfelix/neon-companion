using System;
using System.Collections;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar
{
    public sealed class AvatarAnimationController : MonoBehaviour
    {
        public enum State
        {
            Idle,
            Listening,
            Talking,
            Reacting
        }

        private SpriteSheetAnimator _animator;
        private State _currentState = State.Idle;
        private Coroutine _pendingIdleCoroutine;
        private const float IdleDelaySeconds = 0.5f;

        public void SetAnimator(SpriteSheetAnimator animator)
        {
            CancelPendingIdle();
            _animator = animator;
            _currentState = State.Idle;
        }

        public void TriggerSend()
        {
            CancelPendingIdle();
            TransitionTo(State.Listening);
        }

        public void TriggerStreamStart()
        {
            CancelPendingIdle();
            TransitionTo(State.Talking);
        }

        public void TriggerStreamEnd()
        {
            if (_animator == null)
                return;

            if (_currentState != State.Listening && _currentState != State.Talking && _currentState != State.Reacting)
                return;

            CancelPendingIdle();
            _pendingIdleCoroutine = StartCoroutine(DelayedToIdle());
        }

        private IEnumerator DelayedToIdle()
        {
            yield return new WaitForSecondsRealtime(IdleDelaySeconds);
            _pendingIdleCoroutine = null;
            TransitionTo(State.Idle);
        }

        private void CancelPendingIdle()
        {
            if (_pendingIdleCoroutine != null)
            {
                StopCoroutine(_pendingIdleCoroutine);
                _pendingIdleCoroutine = null;
            }
        }

        private void TransitionTo(State targetState)
        {
            if (_animator == null)
                return;

            if (targetState == _currentState && targetState != State.Reacting)
                return;

            string clipName = GetClipNameForState(targetState);
            if (!_animator.HasClip(clipName))
            {
                // Graceful fallback: if clip does not exist, stay in current state (no visual change)
                return;
            }

            if (targetState == State.Reacting)
            {
                _currentState = State.Reacting;
                _animator.PlayOneShot(clipName, OnReactionComplete, false);
                return;
            }

            _animator.Play(clipName);
            _currentState = targetState;
        }

        private void OnReactionComplete()
        {
            _currentState = State.Idle;
            if (_animator != null && _animator.HasClip("idle"))
            {
                _animator.Play("idle");
            }
        }

        private static string GetClipNameForState(State state)
        {
            switch (state)
            {
                case State.Listening:
                    return "listening";
                case State.Talking:
                    return "talking";
                case State.Reacting:
                    return "confused";
                default:
                    return "idle";
            }
        }
    }
}
