using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NhlWorkbench.App.Services;
using NhlWorkbench.App.ViewModels;

namespace NhlWorkbench.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // A window with custom chrome hangs off the screen edges when maximized.
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);

        PreviewKeyDown += (_, e) => e.Handled = HandleShortcut(e.Key, Keyboard.Modifiers);
    }

    private const string ShortcutList =
        "F1   Setup\n" +
        "Ctrl+K   Search everything\n" +
        "Ctrl+F   Go to this page's search box\n" +
        "Ctrl+Z   Undo on the Roster page\n" +
        "Ctrl+Y   Redo on the Roster page\n" +
        "Ctrl+1 to Ctrl+9   Rink, Teams, Uniforms, Roster, Ice, Textures, Sounds, Videos, Text\n" +
        "Ctrl+0   Explorer\n" +
        "Ctrl+-   Database\n" +
        "Ctrl+=   My Mod\n" +
        "Ctrl+/   This list";

    /// <summary>The app's keys. These work inside text boxes too, apart from undo and redo, which belong to the box there.</summary>
    private bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (key == Key.F1 && modifiers == ModifierKeys.None && DataContext is MainViewModel main && !main.QuickSearch.IsOpen)
        {
            main.ShowSetupCommand.Execute(null);
            return true;
        }

        if (modifiers != ModifierKeys.Control || DataContext is not MainViewModel view)
        {
            return false;
        }

        if (key == Key.K)
        {
            OpenQuickSearch();
            return true;
        }

        if (view.QuickSearch.IsOpen)
        {
            return false;
        }

        ICommand? page = key switch
        {
            Key.D1 or Key.NumPad1 => view.ShowRinkCommand,
            Key.D2 or Key.NumPad2 => view.ShowTeamsCommand,
            Key.D3 or Key.NumPad3 => view.ShowUniformsCommand,
            Key.D4 or Key.NumPad4 => view.ShowRosterCommand,
            Key.D5 or Key.NumPad5 => view.ShowIceCommand,
            Key.D6 or Key.NumPad6 => view.ShowTexturesCommand,
            Key.D7 or Key.NumPad7 => view.ShowSoundsCommand,
            Key.D8 or Key.NumPad8 => view.ShowVideosCommand,
            Key.D9 or Key.NumPad9 => view.ShowTextCommand,
            Key.D0 or Key.NumPad0 => view.ShowFilesCommand,
            Key.OemMinus or Key.Subtract => view.ShowDatabaseCommand,
            Key.OemPlus or Key.Add => view.ShowModCommand,
            _ => null,
        };
        if (page is not null)
        {
            page.Execute(null);
            return true;
        }

        switch (key)
        {
            case Key.F:
                if (FindSearchBox(Root) is { } box)
                {
                    box.Focus();
                    box.SelectAll();
                }

                return true;
            case Key.OemQuestion:
                Dialogs.Tell("Keyboard shortcuts", ShortcutList);
                return true;
            case Key.Z or Key.Y when view.IsRosterPage && Keyboard.FocusedElement is not TextBoxBase:
                ICommand command = key == Key.Z ? view.Roster.UndoCommand : view.Roster.RedoCommand;
                if (command.CanExecute(null))
                {
                    command.Execute(null);
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>The first search box showing on the page, going by the name each one gives screen readers.</summary>
    private static TextBox? FindSearchBox(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement { IsVisible: false })
            {
                continue;
            }

            if (child is TextBox box && AutomationProperties.GetName(box) is { } name
                && (name.StartsWith("Search", StringComparison.Ordinal) || name.StartsWith("Find", StringComparison.Ordinal)))
            {
                return box;
            }

            if (FindSearchBox(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>
    /// Developer check: --shots &lt;folder&gt; --game &lt;path&gt; [--mod &lt;folder&gt;] [--picture &lt;file&gt;] walks every page,
    /// saves a picture of each and exits. It doesn't use or change the saved settings.
    /// </summary>
    public async Task StartAsync(string[] args)
    {
        string? shots = Option(args, "--shots");
        DataContext = new MainViewModel(rememberChoices: shots is null);
        if (shots is null)
        {
            await ViewModel.StartAsync();

            // Switching a mod in the launcher means leaving this window, so coming back is the moment to look.
            Activated += async (_, _) => await ViewModel.FollowLauncherAsync();
            return;
        }

        // Broken bindings don't throw, so they're written out here for checking.
        Directory.CreateDirectory(shots);
        var bindingLog = new TextWriterTraceListener(Path.Combine(shots, "binding-errors.txt"));
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingLog);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        // This mode never stops to ask: a --mod folder that isn't there yet is created as a test mod.
        string? modFolder = Option(args, "--mod");
        if (modFolder is not null && !Directory.Exists(modFolder))
        {
            NhlWorkbench.Core.Mods.ModProject.Create(modFolder, "Shots Test");
        }

        if (!args.Contains("--setup-test"))
        {
            await ViewModel.StartAsync(Option(args, "--game"), modFolder);
        }

        await SaveShotAsync(shots, "1-rink");

        // Starts with nothing connected, as a new person would, then follows the steps.
        if (args.Contains("--setup-test"))
        {
            await WalkSetupAsync(shots, Option(args, "--game")!, modFolder!);
            bindingLog.Flush();
            Close();
            return;
        }

        // Switches a mod off in a throwaway install, so it's only for a folder made for this check.
        if (args.Contains("--mods-test"))
        {
            await WalkLauncherModsAsync(shots);
            bindingLog.Flush();
            Close();
            return;
        }

        // --only walks just the later pages, which is much quicker while working on one of them.
        if (args.Contains("--only"))
        {
            await WalkNewPagesAsync(shots);
            bindingLog.Flush();
            Close();
            return;
        }

        await ViewModel.Ice.WaitForThumbnailsAsync();
        ViewModel.ShowCenterIceCommand.Execute(null);
        await SaveShotAsync(shots, "2-ice-empty");
        ViewModel.Ice.Selected = ViewModel.Ice.VisibleItems.FirstOrDefault();
        await WaitAsync(2500);
        await SaveShotAsync(shots, "3-ice-selected");

        if (Option(args, "--picture") is { } picture && ViewModel.Project is not null)
        {
            await ViewModel.Ice.ApplyPictureAsync(picture);
            await WaitAsync(2500);
            await SaveShotAsync(shots, "4-ice-replaced");
        }

        ViewModel.ShowIceLogosCommand.Execute(null);
        await WaitAsync(800);
        await SaveShotAsync(shots, "5-ice-logos");

        ViewModel.ShowTexturesCommand.Execute(null);
        await ViewModel.Textures.WaitForCatalogAsync();
        await WaitAsync(1500);
        await SaveShotAsync(shots, "t1-textures");
        ViewModel.Textures.ShowCategoryCommand.Execute(ViewModel.Textures.Categories.First(c => c.Name == "Uniforms"));
        ViewModel.Textures.Query = "jersey/texlib";
        await WaitAsync(1500);
        ViewModel.Textures.Selected = ViewModel.Textures.Rows.SelectMany(r => r.Items).FirstOrDefault(i => i.HasSeveral);
        await WaitAsync(1500);
        await SaveShotAsync(shots, "t2-textures-jersey");
        if (ViewModel.Textures.Choices.Count > 1)
        {
            ViewModel.Textures.ChooseTextureCommand.Execute(ViewModel.Textures.Choices[0]);
            await WaitAsync(1000);
            if (Option(args, "--picture") is { } textureFile && ViewModel.Project is not null)
            {
                await ViewModel.Textures.ApplyFileAsync(textureFile);
                await WaitAsync(2000);
            }

            await SaveShotAsync(shots, "t3-textures-replaced");
        }

        ViewModel.Textures.Query = string.Empty;
        ViewModel.Textures.ShowCategoryCommand.Execute(ViewModel.Textures.Categories.First());
        await WaitAsync(500);

        ViewModel.ShowTeamsCommand.Execute(null);
        await ViewModel.Teams.WaitForArtAsync();
        await WaitAsync(3000);
        await SaveShotAsync(shots, "u1-teams");
        if (ViewModel.TeamCatalog?.TeamWithArt(27) is { } leafs)
        {
            ViewModel.Teams.ShowTeam(leafs);
            await WaitAsync(2000);
            await SaveShotAsync(shots, "u2-team");
            ViewModel.ShowUniformsCommand.Execute(null);
            await WaitAsync(2500);
            ViewModel.Textures.Selected = ViewModel.Teams.Lines.OfType<TeamTilesLine>().FirstOrDefault()?.Tiles[0].Item;
            await WaitAsync(2000);
            await SaveShotAsync(shots, "u3-uniforms");
            ViewModel.Textures.Selected = ViewModel.Textures.FindItem(leafs.CrestPath!);
            await WaitAsync(1500);
            if (Option(args, "--picture") is { } crestFile && ViewModel.Project is not null)
            {
                await ViewModel.Textures.ApplyFileAsync(crestFile);
                await WaitAsync(2000);
            }

            await SaveShotAsync(shots, "u4-crest");
            ViewModel.Textures.Selected = null;
            ViewModel.Textures.Query = "leafs";
            ViewModel.ShowTexturesCommand.Execute(null);
            await WaitAsync(2000);
            await SaveShotAsync(shots, "u5-textures-by-team");
            ViewModel.Textures.Query = string.Empty;
            ViewModel.ShowCenterIceCommand.Execute(null);
            await WaitAsync(800);
            await SaveShotAsync(shots, "u6-ice-by-team");
        }

        ViewModel.ShowRosterCommand.Execute(null);
        await WaitAsync(500);
        await ViewModel.Roster.WaitUntilReadyAsync();
        await ViewModel.Textures.WaitForCatalogAsync();
        ViewModel.Roster.ShowTeam(23);
        await WaitAsync(2000);
        ViewModel.Roster.SelectPlayer("Crosby");
        await WaitAsync(800);
        await SaveShotAsync(shots, "r1-roster-readonly");
        if (ViewModel.Project is not null && ViewModel.Roster.CanCopy)
        {
            await ViewModel.Roster.CopyIntoModCommand.ExecuteAsync(null);
            await ViewModel.Roster.WaitUntilReadyAsync();
            ViewModel.Roster.ShowTeam(23);
            ViewModel.Roster.SelectPlayer("Crosby");
            await WaitAsync(800);
            await SaveShotAsync(shots, "r2-roster-editing");
            ViewModel.Roster.Jersey = "abc";
            await WaitAsync(300);
            await SaveShotAsync(shots, "r3-roster-bad-number");
            ViewModel.Roster.SelectPlayer("Malkin");
            await WaitAsync(400);
            ViewModel.Roster.ChooseCommand.Execute(ViewModel.Roster.Letters.First(l => l.Value == NhlWorkbench.Core.Roster.RosterFile.Captain));
            await WaitAsync(800);
            await SaveShotAsync(shots, "r3b-roster-new-captain");
            ViewModel.Roster.SelectPlayer("Crosby");
            await WaitAsync(400);
            ViewModel.Roster.LastName = "Crosby Jr";
            await WaitAsync(800);
            await ViewModel.Roster.WaitUntilReadyAsync();
            ViewModel.Roster.StartTradeCommand.Execute(null);
            ViewModel.Roster.ShowTeam(29);
            await WaitAsync(600);
            ViewModel.Roster.SelectPlayer("Ovechkin");
            await WaitAsync(600);
            await SaveShotAsync(shots, "r4-roster-trade");
            ViewModel.Roster.ConfirmTradeCommand.Execute(null);
            await ViewModel.Roster.WaitUntilReadyAsync();
            await WaitAsync(600);
            await SaveShotAsync(shots, "r5-roster-traded");
            ViewModel.Roster.PlayerQuery = "crosby";
            await WaitAsync(800);
            await SaveShotAsync(shots, "r6-roster-search");
            ViewModel.Roster.PlayerQuery = string.Empty;
        }

        await WalkNewPagesAsync(shots);

        ViewModel.ShowFilesCommand.Execute(null);
        ViewModel.Files.Query = "centerlogo_1";
        ViewModel.Files.Selected = ViewModel.Files.Results.FirstOrDefault();
        await WaitAsync(1500);
        await SaveShotAsync(shots, "6-files");

        ViewModel.ShowModCommand.Execute(null);
        await WaitAsync(800);
        await SaveShotAsync(shots, "7-mod");

        var dialog = new Views.ThemedDialog("Name your mod", "This is the name it gets in the game's launcher. Your edits are saved in its own folder, and your game files stay as they are.", "Create mod", "My Mod") { Owner = this, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0 };
        dialog.Show();
        await Task.Delay(400);
        var dialogShot = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        dialogShot.Render(dialog);
        Imaging.SavePng(dialogShot, Path.Combine(shots, "8-dialog.png"));
        dialog.Close();

        if (args.Contains("--run-install"))
        {
            await ViewModel.InstallCommand.ExecuteAsync(null);
            await SaveShotAsync(shots, "9-installed");
        }

        WindowState = WindowState.Normal;
        Width = MinWidth;
        Height = MinHeight;
        await SaveShotAsync(shots, "a-small-mod");
        ViewModel.ShowRinkCommand.Execute(null);
        await SaveShotAsync(shots, "b-small-rink");
        ViewModel.ShowCenterIceCommand.Execute(null);
        ViewModel.Ice.Selected = ViewModel.Ice.VisibleItems.FirstOrDefault();
        await WaitAsync(1500);
        await SaveShotAsync(shots, "c-small-ice");
        ViewModel.ShowTexturesCommand.Execute(null);
        ViewModel.Textures.Selected = ViewModel.Textures.Rows.FirstOrDefault()?.Items.FirstOrDefault();
        await WaitAsync(1500);
        await SaveShotAsync(shots, "d-small-textures");
        ViewModel.ShowUniformsCommand.Execute(null);
        await WaitAsync(1500);
        await SaveShotAsync(shots, "e-small-uniforms");
        ViewModel.ShowRosterCommand.Execute(null);
        await WaitAsync(1000);
        await SaveShotAsync(shots, "f-small-roster");
        ViewModel.ShowDatabaseCommand.Execute(null);
        await WaitAsync(1000);
        await SaveShotAsync(shots, "g-small-database");
        ViewModel.ShowSoundsCommand.Execute(null);
        await WaitAsync(1000);
        await SaveShotAsync(shots, "h-small-sounds");
        ViewModel.ShowVideosCommand.Execute(null);
        await WaitAsync(1000);
        await SaveShotAsync(shots, "i-small-videos");
        ViewModel.ShowTextCommand.Execute(null);
        await WaitAsync(1000);
        await SaveShotAsync(shots, "j-small-text");
        ViewModel.ShowRosterCommand.Execute(null);
        ViewModel.Roster.ShowTabCommand.Execute("Lines");
        await WaitAsync(1000);
        await SaveShotAsync(shots, "k-small-lines");
        ViewModel.Roster.ShowTabCommand.Execute("Changes");
        await WaitAsync(600);
        await SaveShotAsync(shots, "l-small-changes");
        ViewModel.Roster.ShowTabCommand.Execute("League");
        await WaitAsync(800);
        await SaveShotAsync(shots, "m-small-league");
        ViewModel.Roster.ShowTabCommand.Execute("Checks");
        await WaitAsync(800);
        await SaveShotAsync(shots, "n-small-checks");
        ViewModel.IsWalking = true;
        ViewModel.ShowDatabaseCommand.Execute(null);
        DatabaseViewModel database = ViewModel.Database;
        await database.WaitUntilReadyAsync();
        database.SelectedTable = database.Tables.First(t => t.Name == "exhibitionteams");
        await WaitAsync(300);
        database.Rows[4][database.CurrentTable!.Columns.ToList().FindIndex(c => c.IsText)] = "Small One";
        await database.WaitUntilReadyAsync();
        await WaitAsync(300);
        await SaveShotAsync(shots, "o-small-db-bar");
        database.IsChangesOpen = true;
        await WaitAsync(300);
        await SaveShotAsync(shots, "p-small-db-changes");
        database.IsChangesOpen = false;
        ViewModel.ShowSoundsCommand.Execute(null);
        ViewModel.Sounds.ShowBank("Pittsburgh Penguins");
        await ViewModel.Sounds.WaitUntilReadyAsync();
        await WaitAsync(400);
        await SaveShotAsync(shots, "q-small-sound-tools");
        ViewModel.ShowModCommand.Execute(null);
        await WaitAsync(400);
        if (ViewModel.Mod.Installed.FirstOrDefault(r => r.CanCopy) is { } copyFrom)
        {
            ViewModel.Mod.OpenCopyCommand.Execute(copyFrom);
            await WaitAsync(500);
            await SaveShotAsync(shots, "r-small-copy");
            ViewModel.Mod.CloseCopyCommand.Execute(null);
        }

        ViewModel.ShowTeamsCommand.Execute(null);
        ViewModel.Teams.ShowTeam(ViewModel.TeamCatalog!.Teams.First(t => t.Name == "Pittsburgh Penguins"));
        await ViewModel.Teams.OpenRenameAsync();
        await WaitAsync(500);
        await SaveShotAsync(shots, "s-small-rename");
        ViewModel.IsWalking = false;
        bindingLog.Flush();
        Close();
    }

    private void OpenQuickSearch()
    {
        ViewModel.QuickSearch.Open();
        Dispatcher.BeginInvoke(() => QuickSearchBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnQuickSearchKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Escape:
                ViewModel.QuickSearch.Close();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Down:
            case System.Windows.Input.Key.Up:
                ViewModel.QuickSearch.Move(e.Key == System.Windows.Input.Key.Down ? 1 : -1);
                if (ViewModel.QuickSearch.Selected is { } picked)
                {
                    QuickSearchList.ScrollIntoView(picked);
                }

                e.Handled = true;
                break;
            case System.Windows.Input.Key.Enter:
                // The text box waits a moment before searching, so make sure this is about what's typed now.
                System.Windows.Data.BindingOperations.GetBindingExpression(QuickSearchBox, System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
                ViewModel.QuickSearch.Go(null);
                e.Handled = true;
                break;
        }
    }

    private void OnQuickSearchChosen(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && System.Windows.Controls.ItemsControl.ContainerFromElement(QuickSearchList, source) is System.Windows.Controls.ListBoxItem { DataContext: ViewModels.QuickResult result })
        {
            ViewModel.QuickSearch.Go(result);
        }
    }

    private void OnQuickSearchBackdrop(object sender, System.Windows.Input.MouseButtonEventArgs e) => ViewModel.QuickSearch.Close();

    private void OnQuickSearchPanel(object sender, System.Windows.Input.MouseButtonEventArgs e) => e.Handled = true;

    /// <summary>The Setup page from nothing connected, through connecting and starting a mod.</summary>
    private async Task WalkSetupAsync(string shots, string game, string mod)
    {
        var lines = new List<string>();
        void Note(string when) => lines.Add($"{when}: {ViewModel.Setup.Progress} | done {string.Join(",", ViewModel.Setup.Steps.Select(s => s.IsDone ? "y" : "n"))} | current {ViewModel.Setup.Steps.FirstOrDefault(s => s.IsCurrent)?.Title ?? "none"}");
        lines.Add($"before anything: page is setup after F1: {HandleShortcut(Key.F1, ModifierKeys.None)} {ViewModel.IsSetupPage}");
        Note("nothing connected");
        await SaveShotAsync(shots, "s1-new-person");

        (double width, double height) = (Width, Height);
        (Width, Height) = (MinWidth, MinHeight);
        await SaveShotAsync(shots, "s2-new-person-small");
        (Width, Height) = (width, height);

        await ViewModel.ConnectAsync(game);
        Note("game connected");
        await SaveShotAsync(shots, "s3-connected");
        await ViewModel.OpenModFolderAsync(mod);
        Note("mod open");
        await SaveShotAsync(shots, "s4-mod-open");
        ViewModel.ShowRinkCommand.Execute(null);
        ViewModel.ShowSetupCommand.Execute(null);
        Note("back again");
        lines.Add(ViewModel.Setup.FfmpegNote);
        File.WriteAllLines(Path.Combine(shots, "setup.txt"), lines);
    }

    /// <summary>Shows a mod's picture, switches the mod off in the install's settings, and checks the page follows.</summary>
    private async Task WalkLauncherModsAsync(string shots)
    {
        string install = ViewModel.InstallRoot!;
        if (!install.Contains("mods-test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("--mods-test only runs on a throwaway install.");
        }

        var lines = new List<string>();
        async Task<string> Look(string shot)
        {
            ViewModel.ShowCenterIceCommand.Execute(null);
            await WaitAsync(1500);
            IceItem item = ViewModel.Ice.VisibleItems.First(i => i.GamePath.EndsWith("centerlogo_10_cm.rpsgl", StringComparison.Ordinal));
            ViewModel.Ice.Selected = null;
            ViewModel.Ice.Selected = item;
            await WaitAsync(1500);
            await SaveShotAsync(shots, shot + "-ice");
            string ice = $"ice '{ViewModel.Ice.BeforeLabel}' {PictureHash(ViewModel.Ice.Before)}, thumbnail {PictureHash(item.Thumbnail)}";
            ViewModel.ShowTexturesCommand.Execute(null);
            await ViewModel.Textures.WaitForCatalogAsync();
            ViewModel.Textures.Selected = ViewModel.Textures.FindItem("rendering/icesurface/centerlogo_10_cm.rpsgl");
            await WaitAsync(1500);
            await SaveShotAsync(shots, shot + "-textures");
            return $"{ice}; textures '{ViewModel.Textures.BeforeLabel}' {PictureHash(ViewModel.Textures.Before)}; {ViewModel.Status}";
        }

        lines.Add("on: " + await Look("t1-on"));
        await ViewModel.FollowLauncherAsync();
        lines.Add("nothing changed, still: " + ViewModel.Status);

        string settings = Path.Combine(install, "nhl_settings.ini");
        File.AppendAllText(settings, "\n[mods]\nBright Test = 0\n");
        await ViewModel.FollowLauncherAsync();
        lines.Add("after switching off: " + ViewModel.Status);
        lines.Add("off: " + await Look("t2-off"));
        File.WriteAllLines(Path.Combine(shots, "launcher-mods.txt"), lines);
    }

    private static string PictureHash(BitmapSource? picture)
    {
        if (picture is null)
        {
            return "none";
        }

        var pixels = new byte[picture.PixelWidth * picture.PixelHeight * 4];
        new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, picture.PixelWidth * 4, 0);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels))[..12];
    }

    /// <summary>Presses the keys, opens the list of them, and swaps to another mod and back.</summary>
    private async Task WalkShortcutsAsync(string shots)
    {
        ViewModel.IsWalking = true;
        var lines = new List<string>();
        (Key Key, Func<bool> Landed, string Name)[] pages =
        [
            (Key.D2, () => ViewModel.IsTeamsPage, "teams"), (Key.D4, () => ViewModel.IsRosterPage, "roster"), (Key.D5, () => ViewModel.IsIcePage, "ice"),
            (Key.D6, () => ViewModel.IsTexturesPage, "textures"), (Key.D7, () => ViewModel.IsSoundsPage, "sounds"), (Key.D8, () => ViewModel.IsVideosPage, "videos"),
            (Key.D9, () => ViewModel.IsTextPage, "text"), (Key.D0, () => ViewModel.IsFilesPage, "files"), (Key.OemMinus, () => ViewModel.IsDatabasePage, "database"),
            (Key.OemPlus, () => ViewModel.IsModPage, "mod"), (Key.D1, () => ViewModel.IsRinkPage, "rink"),
        ];
        foreach ((Key key, Func<bool> landed, string name) in pages)
        {
            HandleShortcut(key, ModifierKeys.Control);
            await WaitAsync(250);
            string search = FindSearchBox(Root) is { } box ? AutomationProperties.GetName(box) : "no search box";
            lines.Add($"{name}: {landed()}, {search}");
        }

        lines.Add($"plain 4 ignored: {!HandleShortcut(Key.D4, ModifierKeys.None)}");

        var dialog = new Views.ThemedDialog("Keyboard shortcuts", ShortcutList, "OK", null, showCancel: false);
        dialog.Show();
        await WaitAsync(400);
        var picture = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        picture.Render(dialog);
        Imaging.SavePng(picture, Path.Combine(shots, "s1-shortcuts.png"));
        dialog.Close();

        ViewModel.ShowModCommand.Execute(null);
        string mine = ViewModel.Project!.Folder;
        string other = Path.Combine(shots, "Second Test Mod");
        NhlWorkbench.Core.Mods.ModProject.Create(other, "Second Test Mod");
        await ViewModel.OpenModFolderAsync(other);
        await WaitAsync(300);
        await SaveShotAsync(shots, "s2-recent");
        lines.Add($"recent while on the other mod: {string.Join(", ", ViewModel.RecentMods.Select(m => m.Name))}");
        await ViewModel.OpenRecentModAsync(ViewModel.RecentMods.First(m => m.Folder == mine));
        await WaitAsync(300);
        lines.Add($"back on: {ViewModel.Project!.Name}; recent: {string.Join(", ", ViewModel.RecentMods.Select(m => m.Name))}");
        File.WriteAllLines(Path.Combine(shots, "shortcuts.txt"), lines);
        ViewModel.IsWalking = false;
    }

    /// <summary>Changes two cells, finds them against the game's copy, and puts them back.</summary>
    private async Task WalkDatabaseChangesAsync(string shots)
    {
        ViewModel.IsWalking = true;
        ViewModel.ShowDatabaseCommand.Execute(null);
        await ViewModel.Database.WaitUntilReadyAsync();
        DatabaseViewModel page = ViewModel.Database;
        page.ChooseSourceCommand.Execute(page.Sources.First(s => s.Label == "Main"));
        await page.WaitUntilReadyAsync();
        page.SelectedTable = page.Tables.First(t => t.Name == "exhibitionteams");
        await WaitAsync(300);
        int before = page.ChangedInAll;
        int text = page.CurrentTable!.Columns.ToList().FindIndex(c => c.IsText);
        page.Rows[2][text] = "Chg One";
        page.Rows[3][text] = "Chg Two";
        await page.WaitUntilReadyAsync();
        await WaitAsync(400);
        await SaveShotAsync(shots, "d1-changed");
        int found = page.ChangedInAll;
        page.OnlyChangedRows = true;
        await WaitAsync(300);
        await SaveShotAsync(shots, "d2-only-changed");
        int shownOnly = page.Rows.Count;
        page.OnlyChangedRows = false;
        page.ToggleChangesCommand.Execute(null);
        await WaitAsync(300);
        await SaveShotAsync(shots, "d3-every-change");
        page.PutBackCell(page.ChangeRows.First(r => r.Now == "Chg One"));
        await page.WaitUntilReadyAsync();
        await WaitAsync(300);
        int afterOne = page.ChangedInAll;
        page.GoToChange(page.ChangeRows.First(r => r.Now == "Chg Two"));
        await WaitAsync(300);
        await SaveShotAsync(shots, "d4-went-to");
        page.PutBackTable();
        await page.WaitUntilReadyAsync();
        await WaitAsync(300);
        await SaveShotAsync(shots, "d5-put-back");
        File.WriteAllText(Path.Combine(shots, "database-changes.txt"), $"before {before}, found {found}, only-changed rows {shownOnly}, after one put back {afterOne}, after table put back {page.ChangedInAll}, this table {page.ChangedInThisTable}, problem {page.Problem ?? "none"}, status {ViewModel.Status}");
        ViewModel.IsWalking = false;
    }

    /// <summary>Shapes a sound, plays it, and compares it with the game's.</summary>
    private async Task WalkSoundToolsAsync(string shots)
    {
        ViewModel.IsWalking = true;
        ViewModel.ShowSoundsCommand.Execute(null);
        await ViewModel.Sounds.WaitUntilReadyAsync();
        ViewModel.Sounds.ShowBank("Pittsburgh Penguins");
        await ViewModel.Sounds.WaitUntilReadyAsync();
        await WaitAsync(400);
        ViewModel.Sounds.Mute = true;
        ViewModel.Sounds.ShapeStart = "0.5";
        ViewModel.Sounds.ShapeLength = "3";
        ViewModel.Sounds.ShapeVolume = "-3";
        ViewModel.Sounds.ShapeFadeOut = "1";
        await WaitAsync(200);
        await SaveShotAsync(shots, "w1-shape");
        await ViewModel.Sounds.PreviewShapeAsync();
        await WaitAsync(400);
        string heard = ViewModel.Status;
        double before = ViewModel.Sounds.SelectedSound!.Record.Seconds;
        await ViewModel.Sounds.ChangeThisSoundAsync();
        await ViewModel.Sounds.WaitUntilReadyAsync();
        await WaitAsync(400);
        double after = ViewModel.Sounds.SelectedSound!.Record.Seconds;
        await SaveShotAsync(shots, "w2-shaped");
        string changed = ViewModel.Status;
        await ViewModel.Sounds.PlayOriginalAsync();
        await WaitAsync(300);
        File.WriteAllText(Path.Combine(shots, "sound-tools.txt"), string.Join("\n", heard, changed, ViewModel.Status, $"{before} -> {after}", ViewModel.Sounds.Problem ?? "no problem", $"buttons on: {ViewModel.Sounds.PreviewShapeCommand.CanExecute(null)} {ViewModel.Sounds.ChangeThisSoundCommand.CanExecute(null)} {ViewModel.Sounds.PlayOriginalCommand.CanExecute(null)}"));
        ViewModel.Sounds.StopPlayingForWalk();
        ViewModel.IsWalking = false;
    }

    /// <summary>Copies two files from the installed mod into the throwaway one.</summary>
    private async Task WalkModCopyAsync(string shots)
    {
        ViewModel.IsWalking = true;
        ViewModel.ShowModCommand.Execute(null);
        await WaitAsync(400);
        if (ViewModel.Mod.Installed.FirstOrDefault(r => r.CanCopy) is { } row)
        {
            ViewModel.Mod.OpenCopyCommand.Execute(row);
            ViewModel.Mod.CopyQuery = "canadiens";
            await WaitAsync(700);
            await SaveShotAsync(shots, "c1-copy");
            foreach (ModCopyRow file in ViewModel.Mod.CopyRows!.Cast<ModCopyRow>().Take(2))
            {
                file.IsTicked = true;
            }

            await WaitAsync(200);
            await SaveShotAsync(shots, "c2-copy-ticked");
            await ViewModel.Mod.CopyTickedAsync();
            await WaitAsync(500);
            await SaveShotAsync(shots, "c3-copied");
            File.WriteAllText(Path.Combine(shots, "mod-copy.txt"), ViewModel.Status);
        }

        ViewModel.IsWalking = false;
    }

    /// <summary>Renames a team, replaces text, and shows each can be undone.</summary>
    private async Task WalkTextToolsAsync(string shots)
    {
        ViewModel.IsWalking = true;
        await ViewModel.Text.WaitUntilReadyAsync();
        ViewModel.ShowTeamsCommand.Execute(null);
        ViewModel.Teams.ShowTeam(ViewModel.TeamCatalog!.Teams.First(t => t.Name == "Pittsburgh Penguins"));
        await ViewModel.Teams.OpenRenameAsync();
        await WaitAsync(500);
        ViewModel.Teams.RenameCity = "Steeltown";
        ViewModel.Teams.RenameAbbreviation = "STL";
        await SaveShotAsync(shots, "n1-rename");
        await ViewModel.Teams.SaveRenameAsync();
        string renamed = ViewModel.Status;
        await WaitAsync(500);

        ViewModel.ShowTextCommand.Execute(null);
        await ViewModel.Text.WaitUntilReadyAsync();
        ViewModel.Text.ChooseFilter("changed");
        await WaitAsync(500);
        await SaveShotAsync(shots, "n2-renamed-text");
        ViewModel.Text.ChooseFilter("all");
        ViewModel.Text.ReplaceFind = "Goalie";
        ViewModel.Text.ReplaceWith = "Netminder";
        await WaitAsync(500);
        await SaveShotAsync(shots, "n3-replace");
        ViewModel.Text.ReplaceAll();
        await ViewModel.Text.WaitUntilReadyAsync();
        string replaced = ViewModel.Status;
        await ViewModel.Text.ReplaceEverywhereAsync();
        string everywhere = ViewModel.Status;
        ViewModel.Text.ReplaceFind = string.Empty;
        ViewModel.Text.ReplaceWith = string.Empty;

        ViewModel.ShowTeamsCommand.Execute(null);
        await ViewModel.Teams.PutBackNamesAsync();
        File.WriteAllText(Path.Combine(shots, "text-tools.txt"), string.Join("\n", renamed, replaced, everywhere, ViewModel.Status, string.Join(",", ViewModel.Project!.Files.Select(f => f.Path).Where(p => p.StartsWith("fe/loc", StringComparison.Ordinal)))));
        ViewModel.IsWalking = false;
    }

    /// <summary>Looks across the league, and follows a result back to its team.</summary>
    private async Task WalkRosterFinderAsync(string shots)
    {
        ViewModel.ShowRosterCommand.Execute(null);
        await ViewModel.Roster.WaitUntilReadyAsync();
        ViewModel.Roster.ShowTabCommand.Execute("League");
        await WaitAsync(800);
        await SaveShotAsync(shots, "f1-league");
        ViewModel.Roster.ChooseFinderPositionCommand.Execute(ViewModel.Roster.FinderPositions.First(p => p.Label == "G"));
        ViewModel.Roster.ChooseFinderSortCommand.Execute(ViewModel.Roster.FinderSorts.First(s => s.Value == "young"));
        ViewModel.Roster.FinderMinimum = "70";
        await WaitAsync(500);
        await SaveShotAsync(shots, "f2-league-filtered");
        ViewModel.Roster.OpenFinderRowCommand.Execute(ViewModel.Roster.FinderRows[0]);
        await WaitAsync(800);
        await SaveShotAsync(shots, "f3-league-went");
        ViewModel.Roster.ShowTabCommand.Execute("Checks");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await WaitAsync(800);
        await SaveShotAsync(shots, "f4-checks");
        ViewModel.Roster.ChooseFinderScopeCommand.Execute(ViewModel.Roster.FinderScopes.First(s => s.Value is null));
        await WaitAsync(800);
        File.WriteAllText(Path.Combine(shots, "checks.txt"), ViewModel.Roster.IssuesSummary + "\n" + string.Join("\n", ViewModel.Roster.IssueRows.Select(r => $"{r.Team}: {r.Text}")));
        ViewModel.Roster.ChooseFinderScopeCommand.Execute(ViewModel.Roster.FinderScopes.First(s => s.Value == "NHL"));
        ViewModel.Roster.ShowTabCommand.Execute("Players");
        await WaitAsync(300);
    }

    /// <summary>Saves a kit and a search as folders, then brings pictures back by name.</summary>
    private async Task WalkPictureToolsAsync(string shots)
    {
        ViewModel.IsWalking = true;
        await ViewModel.Textures.WaitForCatalogAsync();
        ViewModel.ShowUniformsCommand.Execute(null);
        ViewModel.Teams.EnsureLoaded();
        await ViewModel.Teams.WaitForArtAsync();
        ViewModel.Teams.ShowTeam(ViewModel.TeamCatalog!.Teams.First(t => t.Name == "Montreal Canadiens"));
        await WaitAsync(800);
        await SaveShotAsync(shots, "p1-kit-buttons");

        string folder = Path.Combine(shots, "pictures");
        Directory.CreateDirectory(folder);
        string? kitFolder = await ViewModel.Teams.ExportKitToAsync(ViewModel.Teams.SelectedTeam!.Team.Kits[0], folder, Core.Mods.TeamBundleFormat.Png);
        Console.WriteLine($"kit folder: {kitFolder}: {ViewModel.Status}");

        ViewModel.ShowTexturesCommand.Execute(null);
        ViewModel.Textures.Query = "jersey_0_15";
        await WaitAsync(800);
        await SaveShotAsync(shots, "p2-textures-tools");
        string? shown = await ViewModel.Textures.SaveShownToAsync(folder, Core.Mods.TeamBundleFormat.Dds);
        Console.WriteLine($"shown folder: {shown}: {ViewModel.Status}");

        string loose = Path.Combine(folder, "loose");
        Directory.CreateDirectory(loose);
        File.Copy(Directory.EnumerateFiles(shown!, "*.dds", SearchOption.AllDirectories).First(f => Path.GetFileNameWithoutExtension(f) == "jersey_0_15_0"), Path.Combine(loose, "jersey_0_15_0.dds"));
        File.WriteAllBytes(Path.Combine(loose, "not_a_real_texture.png"), [1]);
        await ViewModel.Teams.ImportBundleFromAsync(loose);
        await WaitAsync(500);
        await SaveShotAsync(shots, "p3-textures-imported");
        File.WriteAllText(Path.Combine(shots, "picture-tools.txt"), ViewModel.Status);
        ViewModel.Textures.Query = string.Empty;
        ViewModel.IsWalking = false;
    }

    /// <summary>Searches for a team, a player and a string, and follows a result the way a click would.</summary>
    private async Task WalkQuickSearchAsync(string shots)
    {
        await ViewModel.Roster.WaitUntilReadyAsync();
        await ViewModel.Textures.WaitForCatalogAsync();
        await ViewModel.Sounds.WaitUntilReadyAsync();
        OpenQuickSearch();
        await WaitAsync(400);
        await SaveShotAsync(shots, "q1-quick-search");
        ViewModel.QuickSearch.Query = "crosby";
        await WaitAsync(400);
        await SaveShotAsync(shots, "q2-quick-player");
        ViewModel.QuickSearch.Go(ViewModel.QuickSearch.Results.First(r => r.Kind == "Player"));
        await WaitAsync(800);
        await SaveShotAsync(shots, "q3-quick-went");
        OpenQuickSearch();
        ViewModel.QuickSearch.Query = "penguins";
        await WaitAsync(400);
        await SaveShotAsync(shots, "q4-quick-team");
        ViewModel.QuickSearch.Go(ViewModel.QuickSearch.Results.First(r => r.Kind == "Sound"));
        await WaitAsync(500);
        await SaveShotAsync(shots, "q5-quick-sound");

        OpenQuickSearch();
        ViewModel.QuickSearch.Query = "canadiens";
        await WaitAsync(400);
        ViewModel.QuickSearch.Go(ViewModel.QuickSearch.Results.First(r => r.Kind == "Picture"));
        await WaitAsync(800);
        await SaveShotAsync(shots, "q6-quick-picture");

        OpenQuickSearch();
        ViewModel.QuickSearch.Query = "crosby";
        await WaitAsync(400);
        ViewModel.QuickSearch.Go(ViewModel.QuickSearch.Results.First(r => r.Kind == "Text"));
        await WaitAsync(500);
        await SaveShotAsync(shots, "q7-quick-text");

        // The smallest the window goes, with a long list.
        (double width, double height) = (Width, Height);
        (Width, Height) = (MinWidth, MinHeight);
        OpenQuickSearch();
        ViewModel.QuickSearch.Query = "penguins";
        await WaitAsync(500);
        await SaveShotAsync(shots, "q8-quick-small");
        ViewModel.QuickSearch.Close();
        (Width, Height) = (width, height);
        await WaitAsync(300);
    }

    /// <summary>Saves a snapshot of the throwaway mod, changes it, goes back, and shows the page.</summary>
    private async Task WalkModToolsAsync(string shots)
    {
        if (ViewModel.Project is null)
        {
            return;
        }

        ViewModel.ShowModCommand.Execute(null);
        await ViewModel.Mod.TakeSnapshotNamedAsync("Everything so far");
        await WaitAsync(500);
        await SaveShotAsync(shots, "m1-mod-tools");
        var before = Core.Mods.ModSnapshots.List(ViewModel.Project);
        ViewModel.Text.Selected = ViewModel.Text.RowsView?.Cast<ViewModels.TextRow>().FirstOrDefault(r => r.Text.Contains("Canadiens"));
        await WaitAsync(100);
        ViewModel.Text.EditText = "Les Habs";
        await ViewModel.Text.WaitUntilReadyAsync();
        await ViewModel.Mod.RestoreAsync(before[0]);
        await WaitAsync(1000);
        await SaveShotAsync(shots, "m2-mod-restored");
    }

    /// <summary>Exports a team, paints on one picture as an outside editor would, and brings it back into the throwaway mod.</summary>
    private async Task WalkTeamToolsAsync(string shots)
    {
        ViewModel.ShowTeamsCommand.Execute(null);
        await ViewModel.Teams.WaitForArtAsync();
        if (ViewModel.TeamCatalog?.Teams.FirstOrDefault(t => t.Index == 15) is { } team)
        {
            ViewModel.Teams.ShowTeam(team);
        }

        await WaitAsync(800);
        await SaveShotAsync(shots, "t1-team-tools");
        string? folder = await ViewModel.Teams.ExportBundleToAsync(Path.Combine(shots, "bundle"), Core.Mods.TeamBundleFormat.Png);
        if (folder is not null)
        {
            string logo = Directory.EnumerateFiles(Path.Combine(folder, "Logos"), "*.png", SearchOption.AllDirectories).First();
            byte[] bytes = File.ReadAllBytes(logo);
            var decoded = new System.Windows.Media.Imaging.PngBitmapDecoder(new MemoryStream(bytes), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            var inverted = new System.Windows.Media.Imaging.FormatConvertedBitmap(decoded, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int stride = inverted.PixelWidth * 4;
            byte[] pixels = new byte[stride * inverted.PixelHeight];
            inverted.CopyPixels(pixels, stride, 0);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            }

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(inverted.PixelWidth, inverted.PixelHeight, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride)));
            using (FileStream stream = File.Create(logo))
            {
                encoder.Save(stream);
            }

            await ViewModel.Teams.ImportBundleFromAsync(folder);
            await WaitAsync(800);
            await SaveShotAsync(shots, "t2-team-imported");
        }
    }

    /// <summary>Works the Roster page's tools on the throwaway mod: lines, ratings, undo, the changes list and a CSV round trip.</summary>
    private async Task WalkRosterToolsAsync(string shots)
    {
        ViewModels.RosterViewModel roster = ViewModel.Roster;
        ViewModel.ShowRosterCommand.Execute(null);
        await roster.WaitUntilReadyAsync();
        if (ViewModel.Project is null)
        {
            return;
        }

        if (!ViewModel.Project.HasRoster && roster.CanCopy)
        {
            await roster.CopyIntoModCommand.ExecuteAsync(null);
            await roster.WaitUntilReadyAsync();
        }

        roster.ShowTeam(23);
        await WaitAsync(400);
        roster.ShowTabCommand.Execute("Lines");
        await WaitAsync(500);
        await SaveShotAsync(shots, "l1-lines");
        roster.LineUnitRows[0].Spots[1].Selected = roster.LineUnitRows[1].Spots[1].Selected;
        await WaitAsync(500);
        await SaveShotAsync(shots, "l2-lines-swapped");
        roster.ChooseLineSection(Core.Roster.RosterFile.PowerPlay);
        await WaitAsync(400);
        await SaveShotAsync(shots, "l3-power-play");
        roster.BestLinesCommand.Execute(null);
        await roster.WaitUntilReadyAsync();
        await WaitAsync(300);
        await SaveShotAsync(shots, "l4-best-lines");
        roster.UndoCommand.Execute(null);
        await roster.WaitUntilReadyAsync();
        await WaitAsync(300);

        roster.ShowTabCommand.Execute("Players");
        roster.ToggleBulkCommand.Execute(null);
        roster.ChooseBulkWhoCommand.Execute(roster.BulkWho[3]);
        await WaitAsync(400);
        await SaveShotAsync(shots, "l5-bulk");
        roster.ApplyBulkCommand.Execute(null);
        await roster.WaitUntilReadyAsync();
        roster.ToggleBulkCommand.Execute(null);

        string csv = Path.Combine(shots, "roster.csv");
        await roster.SaveCsvAsync(csv, everything: false);
        string text = File.ReadAllText(csv);
        await roster.ApplyCsvAsync(text.Replace("Pittsburgh Penguins", "Pittsburgh Penguins"));
        await roster.WaitUntilReadyAsync();

        roster.ShowTabCommand.Execute("Changes");
        await WaitAsync(200);
        await roster.WaitForChangesAsync();
        await WaitAsync(400);
        await SaveShotAsync(shots, "l6-changes");
        roster.ShowTabCommand.Execute("Players");
        await WaitAsync(300);
        await SaveShotAsync(shots, "l7-players-tools");
    }

    private async Task WalkNewPagesAsync(string shots)
    {
        ViewModel.ShowTextCommand.Execute(null);
        await ViewModel.Text.WaitUntilReadyAsync();
        await WaitAsync(500);
        await SaveShotAsync(shots, "x1-text");
        ViewModel.Text.ChooseFilter("teams");
        ViewModel.Text.Selected = ViewModel.Text.RowsView?.Cast<ViewModels.TextRow>().FirstOrDefault(r => r.Text.Contains("Penguins"));
        await WaitAsync(300);
        ViewModel.Text.EditText = "Pittsburgh Pens";
        await ViewModel.Text.WaitUntilReadyAsync();
        await WaitAsync(400);
        await SaveShotAsync(shots, "x2-text-edited");
        ViewModel.Text.EditText = "Pittsburgh \u4e16\u754c";
        await WaitAsync(300);
        await SaveShotAsync(shots, "x3-text-font-note");

        await WalkRosterToolsAsync(shots);
        await WalkTeamToolsAsync(shots);
        await WalkModToolsAsync(shots);
        await WalkPictureToolsAsync(shots);
        await WalkRosterFinderAsync(shots);
        await WalkTextToolsAsync(shots);
        await WalkModCopyAsync(shots);
        await WalkSoundToolsAsync(shots);
        await WalkDatabaseChangesAsync(shots);
        await WalkShortcutsAsync(shots);
        await WalkQuickSearchAsync(shots);
        ViewModel.ShowVideosCommand.Execute(null);
        await ViewModel.Videos.WaitUntilReadyAsync();
        await WaitAsync(500);
        await SaveShotAsync(shots, "v1-videos");
        ViewModel.Videos.Selected = ViewModel.Videos.Videos.FirstOrDefault(v => v.Video.Seconds > 5) ?? ViewModel.Videos.Videos.FirstOrDefault();
        await ViewModel.Videos.WaitUntilReadyAsync();
        await WaitAsync(2500);
        await SaveShotAsync(shots, "v2-video-playing");

        ViewModel.Sounds.Mute = true;
        ViewModel.ShowSoundsCommand.Execute(null);
        await ViewModel.Sounds.WaitUntilReadyAsync();
        await WaitAsync(800);
        await SaveShotAsync(shots, "s1-sounds");
        ViewModel.Sounds.ShowBank("Pittsburgh Penguins");
        await ViewModel.Sounds.WaitUntilReadyAsync();
        await ViewModel.Sounds.PlayCommand.ExecuteAsync(null);
        await WaitAsync(800);
        await SaveShotAsync(shots, "s2-sounds-playing");
        ViewModel.Sounds.ShowBank("crowds");
        await ViewModel.Sounds.WaitUntilReadyAsync();
        await WaitAsync(500);
        await ViewModel.Sounds.PlayCommand.ExecuteAsync(null);
        await WaitAsync(800);
        await SaveShotAsync(shots, "s3-sounds-stream");
        ViewModel.Sounds.ShowBank("Pittsburgh Penguins");
        await ViewModel.Sounds.WaitUntilReadyAsync();
        if (ViewModel.Project is not null && Option(Environment.GetCommandLineArgs(), "--sound") is { } sound)
        {
            await ViewModel.Sounds.ApplyFileAsync(sound);
            await ViewModel.Sounds.WaitUntilReadyAsync();
            await ViewModel.Sounds.PlayCommand.ExecuteAsync(null);
            await WaitAsync(800);
            await SaveShotAsync(shots, "s4-sounds-replaced");
        }

        ViewModel.ShowDatabaseCommand.Execute(null);
        await ViewModel.Database.WaitUntilReadyAsync();
        await WaitAsync(800);
        await SaveShotAsync(shots, "db1-teams");
        ViewModel.Database.RowQuery = "pittsburgh";
        await WaitAsync(800);
        await SaveShotAsync(shots, "db2-search");
        ViewModel.Database.RowQuery = string.Empty;
        await WaitAsync(300);
        if (ViewModel.Project is not null)
        {
            var table = ViewModel.Database.CurrentTable!;
            int nameColumn = table.Columns.ToList().FindIndex(c => c.Name == "fullname");
            ViewModel.Database.Rows[0][nameColumn] = "Shots Test";
            ViewModel.Database.Rows[0][table.Columns.ToList().FindIndex(c => c.Name == "primarycolor_r")] = "not a number";
            await ViewModel.Database.WaitUntilReadyAsync();
            await WaitAsync(800);
            await SaveShotAsync(shots, "db3-edited");
            if (ViewModel.Database.Sources.FirstOrDefault(s => s.GamePath is null) is { } roster)
            {
                ViewModel.Database.ChooseSourceCommand.Execute(roster);
                await ViewModel.Database.WaitUntilReadyAsync();
                await WaitAsync(800);
                await SaveShotAsync(shots, "db4-roster");
            }
        }
    }

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static Task WaitAsync(int milliseconds) => Task.Delay(milliseconds);

    private async Task SaveShotAsync(string folder, string name)
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(400);
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        Imaging.SavePng(bitmap, Path.Combine(folder, name + ".png"));
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
