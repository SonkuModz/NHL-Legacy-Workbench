using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Abk;
using NhlWorkbench.Core.GameFiles;

namespace NhlWorkbench.App.ViewModels;

/// <summary>The Sounds page's trim, volume and fade boxes, and hearing the game's own version.</summary>
public partial class SoundsViewModel
{
    [ObservableProperty]
    private string _shapeStart = string.Empty;

    [ObservableProperty]
    private string _shapeLength = string.Empty;

    [ObservableProperty]
    private string _shapeVolume = string.Empty;

    [ObservableProperty]
    private string _shapeFadeOut = string.Empty;

    /// <summary>What the boxes say, or null with a problem shown if one of them isn't a number.</summary>
    private SoundShape? ReadShape()
    {
        bool Number(string text, string what, out double value)
        {
            value = 0;
            if (text.Trim().Length == 0 || (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value)))
            {
                return true;
            }

            Problem = $"{what} needs to be a number, like 1.5.";
            return false;
        }

        if (!Number(ShapeStart, "Start at", out double start) || !Number(ShapeLength, "Keep", out double length)
            || !Number(ShapeVolume, "Louder or quieter", out double volume) || !Number(ShapeFadeOut, "Fade out", out double fade))
        {
            return null;
        }

        if (start < 0 || length < 0 || fade < 0 || (ShapeLength.Trim().Length > 0 && length == 0))
        {
            Problem = "Times have to be above zero.";
            return null;
        }

        if (Math.Abs(volume) > 60)
        {
            Problem = "Volume changes go up to 60 dB either way.";
            return null;
        }

        return new SoundShape(start, ShapeLength.Trim().Length > 0 ? length : null, volume, 0, fade);
    }

    private void ClearShape()
    {
        ShapeStart = string.Empty;
        ShapeLength = string.Empty;
        ShapeVolume = string.Empty;
        ShapeFadeOut = string.Empty;
    }

    /// <summary>Plays the sound on show with the boxes' changes. Nothing is saved.</summary>
    [RelayCommand(CanExecute = nameof(CanUseSound))]
    public async Task PreviewShapeAsync()
    {
        if (_bank is not { } bank || SelectedSound is not { } sound || SelectedBank is not { } entry || ReadShape() is not { } shape)
        {
            return;
        }

        Stop();
        Result<AbkPcmAudio> audio = await DecodeAsync(bank, sound.Record, entry.Info);
        if (!audio.IsSuccess)
        {
            Problem = audio.Error;
            return;
        }

        AbkPcmAudio plain = audio.Value!;
        byte[] shaped = await Task.Run(() => shape.Apply(plain.Pcm16, plain.SampleRate, plain.Channels));
        if (shaped.Length == 0)
        {
            Problem = "Those times leave none of the sound.";
            return;
        }

        await PlayAudioAsync(new AbkPcmAudio { SampleRate = plain.SampleRate, Channels = plain.Channels, Pcm16 = shaped }, $"Playing {sound.Name} with those changes");
    }

    /// <summary>Saves the sound on show, with the boxes' changes, over itself in the mod.</summary>
    [RelayCommand(CanExecute = nameof(CanUseSound))]
    public async Task ChangeThisSoundAsync()
    {
        if (_bank is not { } bank || SelectedSound is not { } sound || SelectedBank is not { } entry || ReadShape() is not { } shape || !_main.EnsureProject())
        {
            return;
        }

        if (shape.DoesNothing)
        {
            Problem = "Fill in at least one of the boxes above first.";
            return;
        }

        Stop();
        Result<AbkPcmAudio> audio = await DecodeAsync(bank, sound.Record, entry.Info);
        if (!audio.IsSuccess)
        {
            Problem = audio.Error;
            return;
        }

        string folder = Path.Combine(Path.GetTempPath(), "NhlWorkbench_Preview");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            await File.WriteAllBytesAsync(file, WavFile.Write(audio.Value!));
            await ApplyFileAsync(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = ex.Message;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Only a temp file.
            }
        }
    }

    /// <summary>Plays the same sound from the game's own copy of the bank, to compare with the mod's.</summary>
    [RelayCommand(CanExecute = nameof(CanUseSound))]
    public async Task PlayOriginalAsync()
    {
        if (SelectedSound is not { } sound || SelectedBank is not { } entry || _main.Index is not { } index)
        {
            return;
        }

        Stop();
        string path = entry.Info.Path;
        Result<AbkBank> original = await Task.Run(() =>
        {
            GameFile? file = index.Find(path);
            GameFileCopy? copy = file is null ? null : TextureCatalog.OriginalCopy(file);
            Result<byte[]> data = copy is null ? Result.Failure<byte[]>("The game's copy can't be found.") : index.ReadDecoded(copy);
            return data.IsSuccess ? AbkBank.Parse(data.Value!) : Result.Failure<AbkBank>(data.Error!);
        });
        if (!original.IsSuccess)
        {
            Problem = original.Error;
            return;
        }

        if (original.Value!.Records.FirstOrDefault(r => r.Index == sound.Record.Index) is not { } record)
        {
            Problem = "The game's copy of this bank doesn't have this sound.";
            return;
        }

        Result<AbkPcmAudio> audio = await DecodeAsync(original.Value, record, entry.Info);
        if (!audio.IsSuccess)
        {
            Problem = audio.Error;
            return;
        }

        await PlayAudioAsync(audio.Value!, $"Playing the game's {sound.Name}");
    }

    /// <summary>For the --shots walk, which shouldn't leave anything playing.</summary>
    public void StopPlayingForWalk() => Stop();
}
