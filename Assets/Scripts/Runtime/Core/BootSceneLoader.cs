using System.Collections;
using NeonCompanion.Runtime.UI.UITK;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NeonCompanion.Runtime.Core
{
    /// <summary>
    /// Loads the main scene after the boot/splash sequence.
    /// If a <see cref="SplashViewController"/> is present in the scene it takes
    /// full ownership of the scene transition; this loader becomes a no-op.
    /// Without a SplashViewController a bare minimum delay is used.
    /// </summary>
    public sealed class BootSceneLoader : MonoBehaviour
    {
        [SerializeField] private string mainSceneName  = "Main";
        [SerializeField] private float  minBootSeconds = 0.25f;

        private IEnumerator Start()
        {
            // Defer to SplashViewController if one exists in the scene.
            var splash = FindAnyObjectByType<SplashViewController>();
            if (splash != null)
            {
                // SplashViewController drives the SceneManager.LoadScene call.
                yield break;
            }

            // Fallback: simple timed delay.
            if (minBootSeconds > 0f)
                yield return new WaitForSeconds(minBootSeconds);

            SceneManager.LoadScene(mainSceneName, LoadSceneMode.Single);
        }
    }
}
