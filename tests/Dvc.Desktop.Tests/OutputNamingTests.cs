using Dvc.Desktop.Services;

namespace Dvc.Desktop.Tests;

public class OutputNamingTests
{
    [Theory]
    [InlineData("Videos/clip.mov", "clip.mp4")]
    [InlineData("Videos/clip.mp4", "clip.mp4")]
    [InlineData("Videos/clip.MP4", "clip.mp4")]
    [InlineData("Videos/my.holiday.clip.mkv", "my.holiday.clip.mp4")]
    public void FileName_ReplacesTheExtensionWithMp4(string sourcePath, string expected)
    {
        Assert.Equal(expected, OutputNaming.FileName(sourcePath, 0));
    }

    [Theory]
    [InlineData(1, "clip (1).mp4")]
    [InlineData(2, "clip (2).mp4")]
    [InlineData(10, "clip (10).mp4")]
    public void FileName_NumbersCopies(int copyNumber, string expected)
    {
        Assert.Equal(expected, OutputNaming.FileName("Videos/clip.mov", copyNumber));
    }

    [Fact]
    public void TemporaryFileName_EndsInMp4AndIsUniquePerCall()
    {
        var first = OutputNaming.TemporaryFileName("Videos/clip.mov");
        var second = OutputNaming.TemporaryFileName("Videos/clip.mov");

        Assert.EndsWith(".mp4", first);
        Assert.NotEqual("clip.mp4", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void MoveToFreeName_UsesThePlainNameWhenFree()
    {
        using var folder = new TempFolder();
        var finished = folder.File("encode.partial.mp4", "new");

        var outputPath = OutputNaming.MoveToFreeName(finished, folder.Path, "Videos/clip.mov");

        Assert.Equal(Path.Combine(folder.Path, "clip.mp4"), outputPath);
        Assert.Equal(["clip.mp4"], folder.FileNames());
        Assert.Equal("new", File.ReadAllText(outputPath));
    }

    [Fact]
    public void MoveToFreeName_NumbersPastTakenNamesWithoutOverwriting()
    {
        using var folder = new TempFolder();
        folder.File("clip.mp4", "first");
        folder.File("clip (1).mp4", "second");
        var finished = folder.File("encode.partial.mp4", "new");

        var outputPath = OutputNaming.MoveToFreeName(finished, folder.Path, "Videos/clip.mov");

        Assert.Equal(Path.Combine(folder.Path, "clip (2).mp4"), outputPath);
        Assert.Equal(["clip (1).mp4", "clip (2).mp4", "clip.mp4"], folder.FileNames());
        Assert.Equal("first", File.ReadAllText(Path.Combine(folder.Path, "clip.mp4")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(folder.Path, "clip (1).mp4")));
        Assert.Equal("new", File.ReadAllText(outputPath));
    }

    [Fact]
    public void MoveToFreeName_DoesNotReplaceASourceInTheOutputFolder()
    {
        using var folder = new TempFolder();
        var source = folder.File("clip.mp4", "original");
        var finished = folder.File("encode.partial.mp4", "new");

        var outputPath = OutputNaming.MoveToFreeName(finished, folder.Path, source);

        Assert.Equal(Path.Combine(folder.Path, "clip (1).mp4"), outputPath);
        Assert.Equal("original", File.ReadAllText(source));
    }

    [Fact]
    public void MoveToFreeName_SkipsAFolderWithTheOutputName()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, "clip.mp4"));
        var finished = folder.File("encode.partial.mp4", "new");

        var outputPath = OutputNaming.MoveToFreeName(finished, folder.Path, "Videos/clip.mov");

        Assert.Equal(Path.Combine(folder.Path, "clip (1).mp4"), outputPath);
    }
}
