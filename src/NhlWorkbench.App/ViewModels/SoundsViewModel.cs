using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NhlWorkbench.App.Services;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Abk;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;
using NhlWorkbench.Core.Sounds;

namespace NhlWorkbench.App.ViewModels;

/// <summary>A bank in the side list.</summary>
public partial class SoundBankEntry(SoundBankInfo info) : ObservableObject
{
    [ObservableProperty]
    private bool _isChanged;

    public SoundBankInfo Info { get; } = info;

    public string Title => Info.Title;

    public string Group => Info.Group;

    public string FileName => Info.FileName;

    /// <summary>The names of the sounds inside, filled in the background so searching can find them.</summary>
    public string SoundNames { get; set; } = string.Empty;
}

/// <summary>A sound in the middle list.</summary>
public sealed record SoundRow(AbkRecord Record)
{
    public string Name => Record.Name;

    public string Length => SoundsViewModel.FormatLength(Record.Seconds);

    public string Where => Record.IsStream ? "STREAM" : "BANK";

    public bool IsLooped => Record.IsLooped;
}

/// <summary>
/// The Sounds page: every sound bank, sorted by what it's for. Sounds can be played and saved as WAV, and the ones held
/// in a bank can be replaced with any audio file. Replacing writes a copy of the bank to the mod.
/// </summary>
public partial class SoundsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly MediaPlayer _player = new();
    private readonly Dictionary<string, byte[]> _streams = [];
    private List<SoundBankEntry> _entries = [];
    private ListCollectionView? _bankView;
    private AbkBank? _bank;
    private string? _previewFile;
    private bool _isLoaded;
    private int _openVersion;
    private int _busyVersion;
    private Task _listing = Task.CompletedTask;
    private Task _opening = Task.CompletedTask;

    [ObservableProperty]
    private ListCollectionView? _banks;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBank), nameof(BankFacts), nameof(BankInMod))]
    [NotifyCanExecuteChangedFor(nameof(UndoBankCommand), nameof(ExportAllCommand))]
    private SoundBankEntry? _selectedBank;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<SoundRow> _sounds = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSound), nameof(SoundFacts), nameof(CanReplace), nameof(ReplaceNote))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand), nameof(ExportWavCommand), nameof(ReplaceCommand), nameof(PreviewShapeCommand), nameof(ChangeThisSoundCommand), nameof(PlayOriginalCommand))]
    private SoundRow? _selectedSound;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayLabel))]
    private bool _isPlaying;

    public SoundsViewModel(MainViewModel main)
    {
        _main = main;
        _player.MediaEnded += (_, _) => StopPlaying();
        _player.MediaFailed += (_, e) =>
        {
            StopPlaying();
            Problem = $"That sound wouldn't play: {e.ErrorException?.Message}";
        };
    }

    /// <summary>Set by the page checks, which walk the page without making any noise.</summary>
    public bool Mute { get; set; }

    public bool HasBank => SelectedBank is not null;

    public bool HasSound => SelectedSound is not null;

    public bool HasProblem => Problem is not null;

    public string PlayLabel => IsPlaying ? "Stop" : "Play";

    public bool BankInMod => SelectedBank is { } bank && _main.Project?.HasFile(bank.Info.Path) == true;

    public bool CanReplace => SelectedSound is { Record: { IsStream: false, CodecId: 5 } record } && record.Channels is 1 or 2 or 4;

    public string ReplaceNote => SelectedSound is not { } sound ? string.Empty
        : sound.Record.IsStream ? "This sound plays from a stream file shared with other sounds, so it can't be replaced yet."
        : !CanReplace ? "This kind of sound can't be replaced yet."
        : sound.Record.IsLooped ? "This sound loops. Your replacement will loop from its start, so it should end where it can pick up again."
        : sound.Record.Channels == 4 ? "This sound has four channels. Your replacement plays the same on front and rear."
        : "Any audio file works. It's converted to fit this slot, and it can be longer or shorter than the original.";

    public string BankFacts
    {
        get
        {
            if (SelectedBank is not { } entry || _bank is null)
            {
                return string.Empty;
            }

            string count = $"{_bank.Records.Count} sound{(_bank.Records.Count == 1 ? string.Empty : "s")}";
            string where = BankInMod ? "your mod's copy" : "the game's copy";
            return entry.Info.Teams.Count > 1
                ? $"{entry.FileName} · {count} · {where} · used by {string.Join(", ", entry.Info.Teams)}"
                : $"{entry.FileName} · {count} · {where}";
        }
    }

    public string SoundFacts
    {
        get
        {
            if (SelectedSound?.Record is not { } record)
            {
                return string.Empty;
            }

            string channels = record.Channels switch { 1 => "Mono", 2 => "Stereo", 4 => "4 channels", _ => $"{record.Channels} channels" };
            string kept = record.IsStream ? "streamed" : "held in the bank";
            string loops = record.IsLooped ? ", loops" : string.Empty;
            return $"{FormatLength(record.Seconds)} · {channels} · {record.SampleRate:N0} Hz · {kept}{loops}";
        }
    }

    public static string FormatLength(double seconds) =>
        seconds >= 60 ? $"{(int)seconds / 60}:{(int)seconds % 60:00}" : seconds.ToString("0.0 s", CultureInfo.InvariantCulture);

    partial void OnQueryChanged(string value) => _bankView?.Refresh();

    partial void OnSelectedBankChanged(SoundBankEntry? value)
    {
        Stop();
        _opening = OpenBankAsync(value);
    }

    partial void OnSelectedSoundChanged(SoundRow? value)
    {
        Stop();
        Problem = null;
    }

    /// <summary>Forgets the old game. The new one is read the next time the page opens.</summary>
    public void Reset()
    {
        Stop();
        _openVersion++;
        _isLoaded = false;
        _bank = null;
        _streams.Clear();
        _entries = [];
        _bankView = null;
        Banks = null;
        SelectedBank = null;
        Sounds = [];
        Message = string.Empty;
        if (_main.IsSoundsPage)
        {
            EnsureLoaded();
        }
    }

    public void EnsureLoaded()
    {
        if (_isLoaded || _main.Index is null)
        {
            return;
        }

        _isLoaded = true;
        _listing = ListBanksAsync();
    }

    /// <summary>Called when the mod may have changed, so an undo elsewhere shows up here.</summary>
    public void ProjectChanged()
    {
        foreach (SoundBankEntry entry in _entries)
        {
            entry.IsChanged = _main.Project?.HasFile(entry.Info.Path) == true;
        }

        OnPropertyChanged(nameof(BankInMod));
        OnPropertyChanged(nameof(BankFacts));
        UndoBankCommand.NotifyCanExecuteChanged();
    }

    public async Task WaitUntilReadyAsync()
    {
        await _listing;
        await _opening;
    }

    /// <summary>Opens a bank by its title or file name, or a team's goal horn by the team's name.</summary>
    public void ShowBank(string title) =>
        SelectedBank = _entries.FirstOrDefault(e => e.Title == title || e.FileName == title) ?? _entries.FirstOrDefault(e => e.Info.Teams.Contains(title));

    /// <summary>Banks whose title or teams hold all these words, for quick search. A team's own sounds come before ones shared around the league.</summary>
    public IEnumerable<SoundBankEntry> FindBanks(string[] words) =>
        _entries.Where(e => words.All(w => e.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Info.Teams.Any(t => t.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(e => e.Info.Teams.Count);

    /// <summary>Opens this exact bank, since a title can be on more than one (a team's goal horn and period horn).</summary>
    public void ShowEntry(SoundBankEntry entry)
    {
        Query = string.Empty;
        SelectedBank = entry;
    }

    /// <summary>The same, once the list of banks has been read.</summary>
    public async Task ShowBankWhenReadyAsync(string title)
    {
        await _listing;
        ShowBank(title);
    }

    private async Task ListBanksAsync()
    {
        Message = "Reading the sound banks…";
        GameFileIndex index = _main.Index!;
        TeamCatalog? teams = _main.TeamCatalog;
        IReadOnlyList<SoundBankInfo> infos = await Task.Run(() => SoundCatalog.Build(index, teams));
        if (index != _main.Index)
        {
            return;
        }

        _entries = infos.Select(i => new SoundBankEntry(i) { IsChanged = _main.Project?.HasFile(i.Path) == true }).ToList();
        _bankView = new ListCollectionView(_entries) { Filter = Matches };
        _bankView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SoundBankEntry.Group)));
        Banks = _bankView;
        Message = string.Empty;
        SelectedBank = _entries.FirstOrDefault();

        // In the background, so a search can find a bank by the sounds inside it.
        List<SoundBankEntry> entries = _entries;
        _ = Task.Run(() =>
        {
            foreach (SoundBankEntry entry in entries)
            {
                if (index.Find(entry.Info.Path) is { } file && index.ReadDecoded(file.Winner) is { IsSuccess: true } data
                    && AbkBank.Parse(data.Value!) is { IsSuccess: true } bank)
                {
                    entry.SoundNames = string.Join(' ', bank.Value!.Records.Select(r => r.Name));
                }
            }
        });
    }

    private bool Matches(object item)
    {
        var entry = (SoundBankEntry)item;
        return Query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w =>
            entry.Title.Contains(w, StringComparison.OrdinalIgnoreCase)
            || entry.Info.Path.Contains(w, StringComparison.OrdinalIgnoreCase)
            || entry.Group.Contains(w, StringComparison.OrdinalIgnoreCase)
            || entry.Info.Teams.Any(t => t.Contains(w, StringComparison.OrdinalIgnoreCase))
            || entry.SoundNames.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private async Task OpenBankAsync(SoundBankEntry? entry)
    {
        int version = ++_openVersion;
        Problem = null;
        if (entry is null)
        {
            _bank = null;
            Sounds = [];
            return;
        }

        string path = entry.Info.Path;
        ModProject? project = _main.Project;
        GameFileIndex? index = _main.Index;
        Result<AbkBank> opened = await Task.Run(() => ReadBank(path, project, index));
        if (version != _openVersion)
        {
            return;
        }

        if (!opened.IsSuccess)
        {
            _bank = null;
            Sounds = [];
            Problem = opened.Error;
            return;
        }

        int? keep = SelectedSound?.Record.Index;
        _bank = opened.Value;
        Sounds = _bank!.Records.Select(r => new SoundRow(r)).ToList();
        SelectedSound = Sounds.FirstOrDefault(s => s.Record.Index == keep) ?? Sounds.FirstOrDefault();
        OnPropertyChanged(nameof(BankFacts));
        OnPropertyChanged(nameof(BankInMod));
        ExportAllCommand.NotifyCanExecuteChanged();
        Message = _bank.Records.Count == 0 ? "This bank has no sounds in it." : string.Empty;
    }

    private static Result<AbkBank> ReadBank(string path, ModProject? project, GameFileIndex? index)
    {
        Result<byte[]> data;
        if (project?.HasFile(path) == true)
        {
            data = project.ReadFile(path);
        }
        else if (index?.Find(path) is { } file)
        {
            data = index.ReadDecoded(file.Winner);
        }
        else
        {
            return Result.Failure<AbkBank>($"{path} wasn't found.");
        }

        return data.IsSuccess ? AbkBank.Parse(data.Value!) : Result.Failure<AbkBank>(data.Error!);
    }

    /// <summary>A sound as plain samples, reading its stream file if it needs one.</summary>
    private async Task<Result<AbkPcmAudio>> DecodeAsync(AbkBank bank, AbkRecord record, SoundBankInfo info)
    {
        string? ffmpeg = _main.FfmpegPath;
        GameFileIndex? index = _main.Index;
        byte[]? ast = null;
        if (record.IsStream)
        {
            if (!_streams.TryGetValue(info.StreamPath, out ast))
            {
                Result<byte[]> read = await Task.Run(() =>
                    index?.Find(info.StreamPath) is { } file ? index.ReadDecoded(file.Winner) : Result.Failure<byte[]>($"{info.StreamPath} wasn't found."));
                if (!read.IsSuccess)
                {
                    return Result.Failure<AbkPcmAudio>(read.Error!);
                }

                // One stream file is kept, since they run up to 50 MB and people tend to stay in one bank.
                _streams.Clear();
                ast = read.Value!;
                _streams[info.StreamPath] = ast;
            }
        }

        byte[]? stream = ast;
        return await Task.Run(() => AbkAudio.Decode(bank, record, stream, ffmpeg));
    }

    private bool CanUseSound() => SelectedSound is not null && _bank is not null;

    [RelayCommand(CanExecute = nameof(CanUseSound))]
    private async Task PlayAsync()
    {
        if (IsPlaying)
        {
            Stop();
            return;
        }

        if (_bank is not { } bank || SelectedSound is not { } sound || SelectedBank is not { } entry)
        {
            return;
        }

        int version = ++_busyVersion;
        _main.Status = $"Getting {sound.Name} ready…";
        Result<AbkPcmAudio> audio = await DecodeAsync(bank, sound.Record, entry.Info);
        if (version != _busyVersion || SelectedSound != sound)
        {
            return;
        }

        if (!audio.IsSuccess)
        {
            Problem = audio.Error;
            _main.Status = "That sound couldn't be played.";
            return;
        }

        await PlayAudioAsync(audio.Value!, $"Playing {sound.Name}");
    }

    /// <summary>Plays samples through the page's player.</summary>
    private async Task PlayAudioAsync(AbkPcmAudio audio, string status)
    {
        try
        {
            string folder = Path.Combine(Path.GetTempPath(), "NhlWorkbench_Preview");
            Directory.CreateDirectory(folder);
            _player.Close();
            DeletePreview();
            _previewFile = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".wav");
            await File.WriteAllBytesAsync(_previewFile, WavFile.Write(Stereo(audio)));
            _player.Volume = Mute ? 0 : 1;
            _player.Open(new Uri(_previewFile));
            _player.Play();
            IsPlaying = true;
            Problem = null;
            _main.Status = status;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = ex.Message;
        }
    }

    private void Stop()
    {
        _busyVersion++;
        _player.Stop();
        _player.Close();
        StopPlaying();
        DeletePreview();
    }

    private void StopPlaying() => IsPlaying = false;

    private void DeletePreview()
    {
        if (_previewFile is not null)
        {
            try
            {
                File.Delete(_previewFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still open by the player. It's only a temp file.
            }

            _previewFile = null;
        }
    }

    /// <summary>The player only takes stereo, so a four-channel sound previews its front pair.</summary>
    private static AbkPcmAudio Stereo(AbkPcmAudio audio)
    {
        if (audio.Channels <= 2)
        {
            return audio;
        }

        int frames = audio.Pcm16.Length / (audio.Channels * 2);
        var pcm = new byte[frames * 4];
        for (int i = 0; i < frames; i++)
        {
            Array.Copy(audio.Pcm16, i * audio.Channels * 2, pcm, i * 4, 4);
        }

        return new AbkPcmAudio { SampleRate = audio.SampleRate, Channels = 2, Pcm16 = pcm };
    }

    [RelayCommand(CanExecute = nameof(CanUseSound))]
    private async Task ExportWavAsync()
    {
        if (_bank is not { } bank || SelectedSound is not { } sound || SelectedBank is not { } entry)
        {
            return;
        }

        var dialog = new SaveFileDialog { Title = "Save this sound", FileName = SafeName($"{entry.FileName} {sound.Name}") + ".wav", Filter = "WAV files (*.wav)|*.wav" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await SaveWavAsync(bank, sound.Record, entry.Info, dialog.FileName);
    }

    /// <summary>Writes one sound to a WAV file. Returns false and says why if it can't.</summary>
    public async Task<bool> SaveWavAsync(AbkBank bank, AbkRecord record, SoundBankInfo info, string path)
    {
        _main.BusyText = "Saving the sound";
        try
        {
            Result<AbkPcmAudio> audio = await DecodeAsync(bank, record, info);
            if (!audio.IsSuccess)
            {
                Problem = audio.Error;
                return false;
            }

            await File.WriteAllBytesAsync(path, WavFile.Write(audio.Value!));
            _main.Status = $"Saved {path}";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = ex.Message;
            return false;
        }
        finally
        {
            _main.BusyText = null;
        }
    }

    private bool HasLoadedBank() => SelectedBank is not null && _bank is { Records.Count: > 0 };

    [RelayCommand(CanExecute = nameof(HasLoadedBank))]
    private async Task ExportAllAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Pick where to put this bank's sounds" };
        if (dialog.ShowDialog() == true)
        {
            await ExportAllToAsync(dialog.FolderName);
        }
    }

    public async Task<int> ExportAllToAsync(string folder)
    {
        if (_bank is not { } bank || SelectedBank is not { } entry)
        {
            return 0;
        }

        int saved = 0;
        _main.BusyText = "Saving the sounds";
        try
        {
            foreach (AbkRecord record in bank.Records)
            {
                Result<AbkPcmAudio> audio = await DecodeAsync(bank, record, entry.Info);
                if (audio.IsSuccess)
                {
                    await File.WriteAllBytesAsync(Path.Combine(folder, SafeName($"{record.Index:000} {record.Name}") + ".wav"), WavFile.Write(audio.Value!));
                    saved++;
                }

                _main.BusyText = $"Saved {saved} of {bank.Records.Count}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = ex.Message;
        }
        finally
        {
            _main.BusyText = null;
        }

        _main.Status = $"Saved {saved} of {bank.Records.Count} sounds to {folder}";
        return saved;
    }

    private bool CanPickReplacement() => CanReplace;

    [RelayCommand(CanExecute = nameof(CanPickReplacement))]
    private async Task ReplaceAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Pick the new sound",
            Filter = "Audio files (*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.aac;*.wma)|*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.aac;*.wma|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true && _main.EnsureProject())
        {
            await ApplyFileAsync(dialog.FileName);
        }
    }

    /// <summary>Encodes an audio file into the selected slot, shaped by the page's boxes, and saves the bank to the mod.</summary>
    public async Task ApplyFileAsync(string audioFile)
    {
        if (ReadShape() is not { } shape)
        {
            return;
        }

        if (_bank is not { } bank || SelectedSound?.Record is not { } record || SelectedBank is not { } entry || _main.Project is not { } project)
        {
            return;
        }

        if (_main.FfmpegPath is not { } ffmpeg)
        {
            Problem = "Replacing sounds needs the ffmpeg that comes with NHL Legacy Native. Connect your game folder rather than a disc image.";
            return;
        }

        Stop();
        _main.BusyText = "Converting the sound";
        GameFileIndex? index = _main.Index;
        Result<(int Samples, double Before)> done = await Task.Run(() =>
        {
            Result<EncodedSound> encoded = AbkAudio.Encode(audioFile, record.SampleRate, record.Channels, ffmpeg, shape);
            if (!encoded.IsSuccess)
            {
                return Result.Failure<(int, double)>(encoded.Error!);
            }

            Result<byte[]> rebuilt = bank.Replace(record, encoded.Value!.Payload, encoded.Value.Samples);
            if (!rebuilt.IsSuccess)
            {
                return Result.Failure<(int, double)>(rebuilt.Error!);
            }

            Result<ProjectFile> saved = project.SetFile(entry.Info.Path, rebuilt.Value!, index);
            return saved.IsSuccess ? Result.Success((encoded.Value.Samples, record.Seconds)) : Result.Failure<(int, double)>(saved.Error!);
        });

        _main.BusyText = null;
        if (!done.IsSuccess)
        {
            Problem = done.Error;
            _main.Status = "The sound wasn't replaced.";
            return;
        }

        Problem = null;
        ClearShape();
        double now = done.Value.Samples / (double)record.SampleRate;
        _main.Status = $"Replaced {record.Name} in {project.Name}: now {FormatLength(now)}, was {FormatLength(done.Value.Before)}.";
        await OpenBankAsync(entry);
        await _main.RefreshProjectAsync();
    }

    private bool CanUndoBank() => BankInMod;

    [RelayCommand(CanExecute = nameof(CanUndoBank))]
    private async Task UndoBankAsync()
    {
        if (_main.Project is not { } project || SelectedBank is not { } entry
            || !Dialogs.Confirm("Put this bank back?", $"This takes {entry.Info.Path} out of {project.Name}, so every sound in it goes back to the game's own.", "Put it back"))
        {
            return;
        }

        Stop();
        Result<bool> removed = project.RemoveFile(entry.Info.Path);
        if (!removed.IsSuccess)
        {
            Dialogs.Tell("It wasn't put back", removed.Error!);
            return;
        }

        await OpenBankAsync(entry);
        await _main.RefreshProjectAsync();
    }

    private static string SafeName(string name)
    {
        char[] bad = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => bad.Contains(c) ? '_' : c));
    }
}
