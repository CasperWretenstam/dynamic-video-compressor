using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Dvc.Core.Definitions;

namespace Dvc.Desktop.Services;

/// <summary>Asks the user to choose video files.</summary>
public interface IVideoFilePicker
{
    /// <summary>
    /// The paths of the videos the user chose: empty if they cancelled.
    /// Files that aren't on this machine are left out.
    /// </summary>
    Task<IReadOnlyList<string>> PickVideosAsync();
}

/// <summary>
/// Asks the user to choose videos with the system's file picker, showing
/// only the file types the engine reads (<c>VideoFileDefinitions.SupportedExtensions</c>).
/// </summary>
/// <param name="owner">The window the picker opens over.</param>
public sealed class VideoFilePicker(TopLevel owner) : IVideoFilePicker
{
    private static readonly FilePickerFileType VideoFiles = new("Videos")
    {
        Patterns = [.. VideoFileDefinitions.SupportedExtensions.Select(extension => $"*{extension}")],
    };

    public async Task<IReadOnlyList<string>> PickVideosAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose videos to compress",
            AllowMultiple = true,
            FileTypeFilter = [VideoFiles],
        });

        // A file from a remote or virtual location has no local path for
        // ffmpeg to read.
        return [.. files.Select(file => file.TryGetLocalPath()).OfType<string>()];
    }
}
