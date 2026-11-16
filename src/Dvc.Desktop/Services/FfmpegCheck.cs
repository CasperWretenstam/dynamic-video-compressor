using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Dvc.Desktop.Services;

/// <summary>
/// Checks that the ffmpeg and ffprobe the compression engine will call are
/// usable: both start, and ffmpeg has the libx264 encoder every encode uses.
/// </summary>
/// <param name="ffmpegPath">The ffmpeg executable to check; a bare name is looked up on PATH.</param>
/// <param name="ffprobePath">The ffprobe executable to check; a bare name is looked up on PATH.</param>
public sealed class FfmpegCheck(ILogger<FfmpegCheck> logger, string ffmpegPath = "ffmpeg", string ffprobePath = "ffprobe")
{
    private const string RequiredEncoder = "libx264";

    // Both commands return in well under a second; one that hangs is treated
    // as broken rather than left to block the check.
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// What's wrong with the ffmpeg setup, if anything. Never throws for a
    /// missing or broken ffmpeg; that's reported in the result.
    /// </summary>
    public async Task<FfmpegCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var ffmpegRun = RunAsync(ffmpegPath, "-hide_banner -encoders", cancellationToken);
        var ffprobeRun = RunAsync(ffprobePath, "-version", cancellationToken);
        await Task.WhenAll(ffmpegRun, ffprobeRun);

        List<string> problems = [];

        switch (ffmpegRun.Result)
        {
            case { Outcome: RunOutcome.NotFound }:
                problems.Add("ffmpeg wasn't found on PATH.");
                break;
            case { Outcome: RunOutcome.Failed }:
                problems.Add("ffmpeg was found, but didn't run correctly.");
                break;
            case { Output: var encoders } when !HasEncoder(encoders, RequiredEncoder):
                problems.Add($"ffmpeg was found, but it was built without {RequiredEncoder}, the H.264 encoder this app uses.");
                break;
        }

        switch (ffprobeRun.Result.Outcome)
        {
            case RunOutcome.NotFound:
                problems.Add("ffprobe wasn't found on PATH.");
                break;
            case RunOutcome.Failed:
                problems.Add("ffprobe was found, but didn't run correctly.");
                break;
        }

        return new FfmpegCheckResult(problems);
    }

    /// <summary>
    /// Whether the output of <c>ffmpeg -encoders</c> lists the named encoder.
    /// </summary>
    /// <param name="encodersOutput">The command's standard output.</param>
    /// <param name="encoder">The encoder's exact name, e.g. <c>libx264</c>.</param>
    public static bool HasEncoder(string encodersOutput, string encoder)
    {
        // Each encoder line is a flags column, the encoder's name, then its
        // description, e.g. " V....D libx264   libx264 H.264 / AVC ...". The
        // name is compared whole, so libx264rgb doesn't count as libx264.
        return encodersOutput
            .Split('\n')
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(columns => columns.Length >= 2 && columns[1] == encoder);
    }

    private async Task<RunResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning(ex, "Could not start {FileName}", fileName);
            return new RunResult(RunOutcome.NotFound, "");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RunTimeout);

        try
        {
            // Standard error is drained too, so a chatty process can't fill
            // its buffer and stall waiting for a reader.
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, errors);

            if (process.ExitCode != 0)
            {
                logger.LogWarning(
                    "{FileName} {Arguments} exited with code {ExitCode}: {Errors}",
                    fileName, arguments, process.ExitCode, errors.Result);
                return new RunResult(RunOutcome.Failed, "");
            }

            return new RunResult(RunOutcome.Ran, output.Result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{FileName} {Arguments} didn't finish within {Timeout}", fileName, arguments, RunTimeout);
            return new RunResult(RunOutcome.Failed, "");
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It exited between the check and the kill.
                }
            }
        }
    }

    private enum RunOutcome { Ran, NotFound, Failed }

    private sealed record RunResult(RunOutcome Outcome, string Output);
}

/// <summary>
/// The outcome of checking the ffmpeg setup (<c>FfmpegCheck.CheckAsync</c>).
/// </summary>
/// <param name="Problems">
/// One sentence per problem found, e.g. that ffmpeg isn't on PATH. Empty when
/// ffmpeg is usable.
/// </param>
public sealed record FfmpegCheckResult(IReadOnlyList<string> Problems)
{
    /// <summary>Whether ffmpeg and ffprobe are usable for compression.</summary>
    public bool IsUsable => Problems.Count == 0;
}
