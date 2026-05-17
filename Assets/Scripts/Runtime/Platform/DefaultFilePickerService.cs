using System.Threading.Tasks;

namespace NeonCompanion.Runtime.Platform
{
    public sealed class DefaultFilePickerService : IFilePickerService
    {
        public Task<string> PickImagePathAsync()
        {
#if UNITY_EDITOR
            return Task.FromResult(UnityEditor.EditorUtility.OpenFilePanel("Select avatar image", "", "png,jpg,jpeg"));
#else
            return Task.FromResult<string>(null);
#endif
        }
    }
}
