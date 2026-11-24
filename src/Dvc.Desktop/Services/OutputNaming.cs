namespace Dvc.Desktop.Services;

/// <summary>
/// Names a compressed video after its source, as an <c>.mp4</c> that never
/// replaces an existing file.
/// </summary>
public static class OutputNaming
{
    private const string OutputExtension = ".mp4";

    /// <summary>
    /// The output file name for a source video: its name with an
    /// <c>.mp4</c> extension, e.g. <c>clip.mp4</c> for <c>clip.mov</c>.
    /// </summary>
    /// <param name="copyNumber">
    /// Which numbered copy to name: 0 for the plain name, 1 or more for
    /// <c>clip (1).mp4</c> and onwards.
    /// </param>
    public static string FileName(string sourcePath, int copyNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(copyNumber);

        var name = Path.GetFileNameWithoutExtension(sourcePath);
        return copyNumber == 0 ? $"{name}{OutputExtension}" : $"{name} ({copyNumber}){OutputExtension}";
    }

    /// <summary>
    /// A unique name for the file an encode writes to before it's finished,
    /// e.g. <c>clip.3f2a….partial.mp4</c>. It ends in <c>.mp4</c> because
    /// ffmpeg picks the output format from the extension.
    /// </summary>
    public static string TemporaryFileName(string sourcePath) =>
        $"{Path.GetFileNameWithoutExtension(sourcePath)}.{Guid.NewGuid():N}.partial{OutputExtension}";

    /// <summary>
    /// Moves a finished encode into the output folder under the first free
    /// output name (<c>FileName</c>): the plain name, else <c>clip (1).mp4</c>,
    /// <c>clip (2).mp4</c> and so on. Never overwrites a file.
    /// </summary>
    /// <param name="finishedPath">The finished encode to move.</param>
    /// <param name="sourcePath">The video it was compressed from, which names the output.</param>
    /// <returns>The path the encode was moved to.</returns>
    public static string MoveToFreeName(string finishedPath, string outputFolder, string sourcePath)
    {
        for (var copyNumber = 0; ; copyNumber++)
        {
            var candidate = Path.Combine(outputFolder, FileName(sourcePath, copyNumber));
            if (Path.Exists(candidate))
            {
                continue;
            }

            try
            {
                File.Move(finishedPath, candidate, overwrite: false);
                return candidate;
            }
            // Something else created the file since the check; try the next
            // number. Any other failure is rethrown.
            catch (IOException) when (Path.Exists(candidate) && File.Exists(finishedPath))
            {
            }
        }
    }
}
