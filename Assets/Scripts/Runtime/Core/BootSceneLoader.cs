using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NeonCompanion.Runtime.Core
{
    public sealed class BootSceneLoader : MonoBehaviour
    {
        [SerializeField] private string mainSceneName = "Main";
        [SerializeField] private float minBootSeconds = 0.25f;

        private IEnumerator Start()
        {
            if (minBootSeconds > 0f)
            {
                yield return new WaitForSeconds(minBootSeconds);
            }

            SceneManager.LoadScene(mainSceneName, LoadSceneMode.Single);
        }
    }
}
