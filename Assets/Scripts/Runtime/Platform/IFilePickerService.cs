using System.Threading.Tasks;

namespace NeonCompanion.Runtime.Platform
{
    public interface IFilePickerService
    {
        Task<string> PickImagePathAsync();
        Task<string> PickFileAsync(string extension);
        /// <summary>
        /// Shows a save-file dialog and returns the chosen path, or null if cancelled.
        /// Falls back to null on platforms without a native save dialog.
        /// </summary>
        Task<string> PickSavePathAsync(string defaultFileName, string extension);
    }
}
