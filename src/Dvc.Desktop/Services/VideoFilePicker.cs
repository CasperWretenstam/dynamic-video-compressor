using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Dvc.Core.Definitions;

namespace Dvc.Desktop.Services;

/// <summary>Asks the user to choose one video file.</summary>
public interface IVideoFilePicker
{
    /// <summary>
    /// The path of the video the user chose, or null if they cancelled or
    /// chose a file that isn't on this machine.
    /// </summary>
    Task<string?> PickVideoAsync();
}

/// <summary>
/// Asks the user to choose one video with the system's file picker, showing
/// only the file types the engine reads (<c>VideoFileDefinitions.SupportedExtensions</c>).
/// </summary>
/// <param name="owner">The window the picker opens over.</param>
public sealed class VideoFilePicker(TopLevel owner) : IVideoFilePicker
{
    private static readonly FilePickerFileType VideoFiles = new("Videos")
    {
        Patterns = [.. VideoFileDefinitions.SupportedExtensions.Select(extension => $"*{extension}")],
    };

    public async Task<string?> PickVideoAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a video to compress",
            AllowMultiple = false,
            FileTypeFilter = [VideoFiles],
        });

        // A file from a remote or virtual location has no local path for
        // ffmpeg to read.
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
