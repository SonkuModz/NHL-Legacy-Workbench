using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NhlWorkbench.Core.GameFiles;

namespace NhlWorkbench.App.ViewModels;

/// <summary>A button on a setup step. The first one on the current step stands out.</summary>
public sealed partial class SetupAction(string label, ICommand command) : ObservableObject
{
    [ObservableProperty]
    private bool _isPrimary;

    public string Label { get; } = label;

    public ICommand Command { get; } = command;
}

/// <summary>One step of getting started, ticked off by looking at what's really there.</summary>
public sealed partial class SetupStep(int number, string title, string text, Func<bool> isDone, params SetupAction[] actions) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mark))]
    private bool _isDone;

    [ObservableProperty]
    private bool _isCurrent;

    public string Title { get; } = title;

    public string Text { get; } = text;

    public IReadOnlyList<SetupAction> Actions { get; } = actions;

    public string Mark => IsDone ? "✓" : number.ToString(CultureInfo.InvariantCulture);

    public bool CheckDone() => IsDone = isDone();
}

/// <summary>The Setup page: what a new person needs, in order, with each step checked against their machine.</summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private const string RecompReleases = "https://github.com/csrpfivem/PS3NHLLegacyRecomp/releases";

    private readonly MainViewModel _main;

    [ObservableProperty]
    private string _progress = string.Empty;

    public SetupViewModel(MainViewModel main)
    {
        _main = main;
        Steps =
        [
            new SetupStep(
                1,
                "Download the game's recomp",
                "The Workbench makes mods for NHL Legacy Native, the PC version of the PS3 game. Download it from its GitHub page and unzip it where you want the game to live.",
                () => _main.InstallRoot is not null,
                new SetupAction("Open the download page", new RelayCommand(OpenRecompPage))),
            new SetupStep(
                2,
                "Install it with your own copy of the game",
                "You need your own NHL Legacy Edition (US, BLUS31540) as a decrypted ISO or an extracted folder, plus its 1.01 update. Neither program includes any game files. Run NHLLauncher.exe, pick both, press Install, and check that the game starts with PLAY.",
                () => _main.InstallRoot is { } root && Directory.Exists(Path.Combine(root, "data", "update", "USRDIR"))),
            new SetupStep(
                3,
                "Connect the game here",
                "Pick your NHL Legacy Native folder. The Workbench only reads it, and your mods are kept in their own folders. A disc image also works for looking around, but mods can't be installed from one.",
                () => _main.HasGame,
                new SetupAction("Connect game folder", _main.PickGameCommand),
                new SetupAction("Use a disc image", _main.PickDiscImageCommand)),
            new SetupStep(
                4,
                "Start a mod",
                "A mod holds your changes, so the game's own files stay as they are. Your mods are saved in Documents\\NHL Legacy Workbench\\Mods.",
                () => _main.HasProject,
                new SetupAction("New mod", _main.NewModCommand),
                new SetupAction("Open a mod", _main.OpenModCommand)),
            new SetupStep(
                5,
                "Change something",
                "Try a center ice logo first, since it's the quickest change to spot in game. Each change saves to your mod as you make it. Ctrl+K searches for any team, player, sound, picture or string.",
                () => _main.Project is { } project && (project.Files.Count > 0 || project.HasRoster),
                new SetupAction("Center ice logos", _main.ShowCenterIceCommand),
                new SetupAction("Teams", _main.ShowTeamsCommand)),
            new SetupStep(
                6,
                "Install it and play",
                "Install hands your mod to the game's launcher, where the top mod in the list wins any clash. If your mod has a roster, choose it in the launcher too. Then press PLAY.",
                () => _main.Project is { } project && _main.InstallRoot is { } root
                    && GameInstallLocator.EnabledMods(root).Contains(project.Name, StringComparer.OrdinalIgnoreCase),
                new SetupAction("My Mod page", _main.ShowModCommand),
                new SetupAction("Open the launcher", new RelayCommand(OpenLauncher))),
        ];

        _main.PropertyChanged += OnMainChanged;
        Refresh();
    }

    public IReadOnlyList<SetupStep> Steps { get; }

    public string FfmpegNote => _main.InstallRoot is null
        ? "Sounds and videos use the ffmpeg that comes with NHL Legacy Native, so they work once the game is connected."
        : _main.FfmpegPath is null
            ? "ffmpeg wasn't found in your game folder, so sound replacement and video playback won't work. Installing the game with its launcher should bring it back."
            : "ffmpeg was found in your game folder, so sounds and videos are ready.";

    /// <summary>Works out what's done. Later steps can be done before earlier ones, so the first one left is the current one.</summary>
    public void Refresh()
    {
        SetupStep? current = null;
        foreach (SetupStep step in Steps)
        {
            if (!step.CheckDone())
            {
                current ??= step;
            }

            step.IsCurrent = step == current;
            for (int i = 0; i < step.Actions.Count; i++)
            {
                step.Actions[i].IsPrimary = step.IsCurrent && i == 0;
            }
        }

        int done = Steps.Count(s => s.IsDone);
        Progress = done == Steps.Count
            ? "All done. Your mod is on in the launcher, so press PLAY there to see it."
            : $"{done} of {Steps.Count} steps done";
        OnPropertyChanged(nameof(FfmpegNote));
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Index) or nameof(MainViewModel.Project) or nameof(MainViewModel.InstallRoot) or nameof(MainViewModel.CurrentPage))
        {
            Refresh();
        }
    }

    private void OpenRecompPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(RecompReleases) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _main.Status = $"Couldn't open your browser. The page is {RecompReleases}";
        }
    }

    private void OpenLauncher()
    {
        if (_main.InstallRoot is null)
        {
            _main.Status = "Connect your game folder first, so the Workbench knows where the launcher is.";
            return;
        }

        _main.OpenLauncherCommand.Execute(null);
    }
}
