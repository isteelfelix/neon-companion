using System.Threading.Tasks;

namespace NeonCompanion.Runtime.Platform
{
    public interface IFilePickerService
    {
        Task<string> PickImagePathAsync();
    }
}
