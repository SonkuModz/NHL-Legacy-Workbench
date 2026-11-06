using System.Diagnostics;
using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Formats.Abk;

/// <summary>
/// Replaces one sound slot in a PS3 (EA-XAS) .abk file by driving the user's own proven
/// ABK_insert.exe tool, rather than reimplementing EA-XAS encoding -- that tool already
/// P/Invokes the real native EA_ADPCM_codec.dll encoder, and re-deriving an equivalent
/// encoder (shift/coefficient selection per block) is a much bigger, riskier undertaking
/// than porting the decoder was.
///
/// Mirrors the exact pipeline the user's own SonkusAudioGUI.py drove by hand: ffmpeg
/// converts the source audio to stereo/44100Hz/16-bit PCM and strips its metadata, then
/// ABK_insert.exe is run against a copy of the target file. One correction from that
/// script: ABK_insert.exe's own Main() writes its output as "&lt;stem&gt;m.abk" resolved
/// against its *current working directory*, not next to the input file -- the Python tool's
/// final "saved to" message pointed at the wrong path. This runs ABK_insert.exe with the
/// tool's own folder as the working directory and explicitly locates and moves that "m.abk"
/// output to the real destination.
/// </summary>
public static class AbkPs3Writer
{
    public static Result<string> ReplaceSound(
        string sourceAbkPath,
        string replacementAudioPath,
        int sampleIndex,
        string outputAbkPath,
        string ffmpegExePath,
        string abkInsertExePath)
    {
        if (!File.Exists(ffmpegExePath))
        {
            return Result.Failure<string>($"ffmpeg.exe not found at '{ffmpegExePath}'.");
        }

        if (!File.Exists(abkInsertExePath))
        {
            return Result.Failure<string>($"ABK_insert.exe not found at '{abkInsertExePath}'.");
        }

        string toolDir = Path.GetDirectoryName(abkInsertExePath)!;
        string tempDir = Path.Combine(Path.GetTempPath(), "NhlWorkbench_AbkWrite_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        string stereoWav = Path.Combine(tempDir, "stereo.wav");
        string cleanWav = Path.Combine(tempDir, "clean.wav");

        try
        {
            if (!string.Equals(Path.GetFullPath(sourceAbkPath), Path.GetFullPath(outputAbkPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourceAbkPath, outputAbkPath, overwrite: true);
            }

            Result<string> step1 = RunProcess(ffmpegExePath, null, "-y", "-i", replacementAudioPath, "-ac", "2", "-ar", "44100", "-sample_fmt", "s16", "-acodec", "pcm_s16le", stereoWav);
            if (!step1.IsSuccess)
            {
                return Result.Failure<string>($"Converting '{replacementAudioPath}' to stereo PCM failed: {step1.Error}");
            }

            Result<string> step2 = RunProcess(ffmpegExePath, null, "-y", "-i", stereoWav, "-map_metadata", "-1", "-c", "copy", cleanWav);
            if (!step2.IsSuccess)
            {
                return Result.Failure<string>($"Stripping WAV metadata failed: {step2.Error}");
            }

            Result<string> step3 = RunProcess(abkInsertExePath, toolDir, outputAbkPath, cleanWav, sampleIndex.ToString());
            if (!step3.IsSuccess)
            {
                return Result.Failure<string>($"ABK_insert.exe failed: {step3.Error}");
            }

            string producedName = Path.GetFileNameWithoutExtension(outputAbkPath) + "m.abk";
            string producedPath = Path.Combine(toolDir, producedName);
            if (!File.Exists(producedPath))
            {
                return Result.Failure<string>($"ABK_insert.exe reported success but its output file '{producedPath}' was not found.");
            }

            File.Copy(producedPath, outputAbkPath, overwrite: true);
            File.Delete(producedPath);

            return Result.Success(outputAbkPath);
        }
        catch (Exception ex)
        {
            return Result.Failure<string>($"Replacing sound {sampleIndex} failed: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private static Result<string> RunProcess(string exePath, string? workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };

        // ABK_insert.exe is a 32-bit .NET apphost (it P/Invokes the 32-bit-only
        // EA_ADPCM_codec.dll). If the calling (64-bit) process has inherited a DOTNET_ROOT
        // pointing at the 64-bit shared runtime, the apphost's own host resolver can pick that
        // up ahead of the correct x86 install and fail with "not a valid Win32 application" /
        // a hostfxr load failure -- even though an x86 runtime is installed. Pointing
        // DOTNET_ROOT(x86) at the actual x86 install directory for this one child process
        // sidesteps that without touching the machine's environment.
        string x86DotnetRoot = @"C:\Program Files (x86)\dotnet";
        if (Directory.Exists(x86DotnetRoot))
        {
            psi.Environment["DOTNET_ROOT(x86)"] = x86DotnetRoot;
        }

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process? process = Process.Start(psi);
        if (process is null)
        {
            return Result.Failure<string>($"Could not start '{exePath}'.");
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            return Result.Failure<string>($"exit code {process.ExitCode}: {stdout}\n{stderr}");
        }

        return Result.Success(stdout);
    }
}
