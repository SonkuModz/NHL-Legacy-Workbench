using System.IO.Compression;
using System.Text;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public sealed class ModProjectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nhlwb-mod-{Guid.NewGuid():N}");

    public ModProjectTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static byte[] Text(string text) => Encoding.ASCII.GetBytes(text);

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private void Write(string relativePath, byte[] data)
    {
        string path = At(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    private ModProject NewProject(string name = "My Mod")
    {
        Result<ModProject> project = ModProject.Create(At("project"), name);
        Assert.True(project.IsSuccess, project.Error);
        return project.Value!;
    }

    private GameFileIndex GameWithLogo(string content = "RW4 original")
    {
        Write("install/nhl_settings.ini", Text(string.Empty));
        Write("install/game/PS3_GAME/USRDIR/cacherender.big", EbTestData.EbArchive([("rendering/logo/a.rpsgl", Text(content))]));
        return GameInstallLocator.Open(At("install")).Value!;
    }

    private static ModIssueCode[] Codes(IEnumerable<ModIssue> issues) => issues.Select(i => i.Code).ToArray();

    [Fact]
    public void ProjectSurvivesSaveAndReopen()
    {
        ModProject project = NewProject();
        Assert.True(project.SetFile(@"Rendering\Logo\A.RPSGL", Text("new")).IsSuccess);

        Result<ModProject> reopened = ModProject.Open(project.Folder);

        Assert.True(reopened.IsSuccess, reopened.Error);
        Assert.Empty(reopened.Warnings);
        Assert.Equal("My Mod", reopened.Value!.Name);
        Assert.Equal("rendering/logo/a.rpsgl", Assert.Single(reopened.Value.Files).Path);
        Assert.Equal(Text("new"), reopened.Value.ReadFile("rendering/logo/a.rpsgl").Value);
    }

    [Fact]
    public void CreateRefusesAFolderWithFilesInIt()
    {
        Write("project/other.txt", Text("x"));
        Assert.False(ModProject.Create(At("project"), "Mod").IsSuccess);
        Assert.Equal(Text("x"), File.ReadAllBytes(At("project", "other.txt")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("...")]
    [InlineData("???")]
    [InlineData("CON")]
    public void CreateRefusesUnusableNames(string name)
    {
        Assert.False(ModProject.Create(At("project"), name).IsSuccess);
    }

    [Theory]
    [InlineData("A/B", "A_B")]
    [InlineData("  .Bright Ice.  ", "Bright Ice")]
    [InlineData("a=b|c", "a_b_c")]
    public void ModNamesAreCleaned(string input, string expected)
    {
        Assert.Equal(expected, ModNames.Clean(input));
    }

    [Theory]
    [InlineData("../evil.bin")]
    [InlineData("a/../../evil.bin")]
    [InlineData(@"C:\Windows\evil.bin")]
    [InlineData("con/x.bin")]
    [InlineData("a/nul.txt")]
    [InlineData("trailing./x.bin")]
    [InlineData("bad?name.bin")]
    public void UnsafeGamePathsAreRefusedAndWriteNothing(string path)
    {
        ModProject project = NewProject();

        Assert.False(project.SetFile(path, Text("x")).IsSuccess);
        Assert.Empty(project.Files);
        Assert.False(File.Exists(At("evil.bin")));
    }

    [Fact]
    public void OpenRefusesAManifestWithATraversalPath()
    {
        NewProject();
        File.WriteAllText(At("project", ModProject.ManifestFileName), """{"FormatVersion":1,"Name":"M","Files":[{"Path":"../../evil.bin","Size":1,"Sha256":"00"}]}""");

        Assert.False(ModProject.Open(At("project")).IsSuccess);
    }

    [Fact]
    public void OpenRefusesNewerFormatAndBrokenManifest()
    {
        NewProject();
        File.WriteAllText(At("project", ModProject.ManifestFileName), """{"FormatVersion":99,"Name":"M"}""");
        Assert.False(ModProject.Open(At("project")).IsSuccess);

        File.WriteAllText(At("project", ModProject.ManifestFileName), "not json");
        Assert.False(ModProject.Open(At("project")).IsSuccess);

        Assert.False(ModProject.Open(At("nowhere")).IsSuccess);
    }

    [Fact]
    public void BaseRecordComesFromTheGameAndSurvivesRepeatedEdits()
    {
        using GameFileIndex game = GameWithLogo();
        ModProject project = NewProject();

        ProjectFile first = project.SetFile("rendering/logo/a.rpsgl", Text("RW4 one"), game).Value!;
        ProjectFile second = project.SetFile("rendering/logo/a.rpsgl", Text("RW4 two")).Value!;

        Assert.Equal("Disc", first.Base!.Layer);
        Assert.Equal("cacherender.big", first.Base.ArchivePath);
        Assert.Equal(first.Base, second.Base);
    }

    [Fact]
    public void RemoveTakesTheFileBackOut()
    {
        ModProject project = NewProject();
        project.SetFile("a.bin", Text("x"));

        Assert.True(project.RemoveFile("A.BIN").Value);
        Assert.False(project.RemoveFile("A.BIN").Value);
        Assert.Empty(ModProject.Open(project.Folder).Value!.Files);
    }

    [Fact]
    public void EmptyProjectCannotExport()
    {
        ModProject project = NewProject();

        Assert.Contains(ModIssueCode.EmptyProject, Codes(ModValidator.Check(project)));
        Assert.False(ModExporter.ExportFolder(project, At("out")).IsSuccess);
        Assert.False(Directory.Exists(At("out")));
    }

    [Fact]
    public void EbootIsRefusedAnywhere()
    {
        ModProject project = NewProject();
        project.SetFile("EBOOT.BIN", Text("patched code"));

        Assert.Contains(ModIssueCode.CodePatchUnsupported, Codes(ModValidator.Check(project)));
        Assert.False(ModExporter.ExportZip(project, At("mod.zip")).IsSuccess);
        Assert.False(File.Exists(At("mod.zip")));
    }

    [Fact]
    public void SameSizeOfficialFileIsRefusedButOtherSizesPass()
    {
        ModProject project = NewProject();
        project.SetFile("options.viv", new byte[284]);
        Assert.Contains(ModIssueCode.DroppedByLauncherImport, Codes(ModValidator.Check(project)));

        project.SetFile("options.viv", new byte[285]);
        Assert.DoesNotContain(ModIssueCode.DroppedByLauncherImport, Codes(ModValidator.Check(project)));
    }

    [Fact]
    public void TamperedAndMissingFilesAreCaught()
    {
        ModProject project = NewProject();
        project.SetFile("a.bin", Text("x"));
        project.SetFile("b.bin", Text("y"));
        File.WriteAllBytes(At("project", "files", "a.bin"), Text("changed"));
        File.Delete(At("project", "files", "b.bin"));

        ModIssueCode[] codes = Codes(ModValidator.Check(project));

        Assert.Contains(ModIssueCode.ChangedProjectFile, codes);
        Assert.Contains(ModIssueCode.MissingProjectFile, codes);
        Assert.Equal(2, ModProject.Open(project.Folder).Warnings.Count);
    }

    [Fact]
    public void WarnsAboutPathsTheGameDoesNotHave()
    {
        using GameFileIndex game = GameWithLogo();
        ModProject project = NewProject();
        project.SetFile("rendering/logo/typo.rpsgl", Text("x"), game);

        ModIssue issue = Assert.Single(ModValidator.Check(project, game));

        Assert.Equal(ModIssueCode.NotAGameFile, issue.Code);
        Assert.Equal(ModIssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void GoodEditHasNoIssues()
    {
        using GameFileIndex game = GameWithLogo();
        ModProject project = NewProject();
        project.SetFile("rendering/logo/a.rpsgl", Text("RW4 edit"), game);

        Assert.Empty(ModValidator.Check(project, game));
    }

    [Fact]
    public void WarnsAboutWrongTypeAndOversizedFile()
    {
        using GameFileIndex game = GameWithLogo();
        ModProject project = NewProject();
        project.SetFile("rendering/logo/a.rpsgl", new byte[500], game);

        ModIssueCode[] codes = Codes(ModValidator.Check(project, game));

        Assert.Contains(ModIssueCode.TypeMismatch, codes);
        Assert.Contains(ModIssueCode.MuchLargerThanOriginal, codes);
    }

    [Fact]
    public void WarnsWhenGameCopyChangedAfterTheEdit()
    {
        ModProject project = NewProject();
        using (GameFileIndex before = GameWithLogo("RW4 original"))
        {
            project.SetFile("rendering/logo/a.rpsgl", Text("RW4 edit"), before);
        }

        using GameFileIndex after = GameWithLogo("RW4 changed in an update");

        Assert.Contains(ModIssueCode.GameFileChanged, Codes(ModValidator.Check(project, after)));
    }

    [Fact]
    public void WarnsAboutOtherModsButNotAnInstalledCopyOfItself()
    {
        Write("install/nhl_settings.ini", Text(string.Empty));
        Write("install/game/PS3_GAME/USRDIR/cacherender.big", EbTestData.EbArchive([("rendering/logo/a.rpsgl", Text("RW4 original"))]));
        Write("install/mods/Other Mod/USRDIR/rendering/logo/a.rpsgl", Text("RW4 other"));
        Write("install/mods/My Mod/USRDIR/rendering/logo/a.rpsgl", Text("RW4 mine"));
        using GameFileIndex game = GameInstallLocator.Open(At("install")).Value!;
        ModProject project = NewProject();
        project.SetFile("rendering/logo/a.rpsgl", Text("RW4 edit"), game);

        ModIssue issue = Assert.Single(ModValidator.Check(project, game));

        Assert.Equal(ModIssueCode.OtherModChangesFile, issue.Code);
        Assert.Contains("Other Mod", issue.Message);
        Assert.Equal("Disc", project.Files.Single().Base!.Layer);
    }

    [Fact]
    public void WarnsWhenReplacingAWholeArchive()
    {
        using GameFileIndex game = GameWithLogo();
        ModProject project = NewProject();
        project.SetFile("cacherender.big", EbTestData.EbArchive([("x.bin", Text("x"))]), game);

        Assert.Contains(ModIssueCode.ReplacesWholeArchive, Codes(ModValidator.Check(project, game)));
    }

    [Fact]
    public void DamagedDiscFileGetsAWarning()
    {
        ModProject project = NewProject();
        project.SetFile("scenario/scenario_online.big", Text("x"));

        Assert.Contains(ModIssueCode.KnownDamagedFile, Codes(ModValidator.Check(project)));
    }

    [Fact]
    public void RosterMustBeAPs3RosterSave()
    {
        ModProject project = NewProject();
        project.SetRoster(Text("not a roster, just text that is long enough to be checked for its magic"));
        Assert.Contains(ModIssueCode.InvalidRoster, Codes(ModValidator.Check(project)));

        byte[] roster = new byte[0x40];
        "PS3RosterFile"u8.CopyTo(roster);
        project.SetRoster(roster);
        Assert.Empty(ModValidator.Check(project));
    }

    [Fact]
    public void FolderExportHasTheLauncherLayout()
    {
        ModProject project = NewProject();
        project.SetFile("rendering/logo/a.rpsgl", Text("logo"));
        byte[] roster = new byte[0x40];
        "PS3RosterFile"u8.CopyTo(roster);
        project.SetRoster(roster, Text("icon"));

        Result<ModExportResult> result = ModExporter.ExportFolder(project, At("out", "My Mod"));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(Text("logo"), File.ReadAllBytes(At("out", "My Mod", "USRDIR", "rendering", "logo", "a.rpsgl")));
        Assert.Equal(roster, File.ReadAllBytes(At("out", "My Mod", "roster", "SYS-DATA")));
        Assert.Equal(Text("icon"), File.ReadAllBytes(At("out", "My Mod", "roster", "ICON0.PNG")));
        Assert.False(Directory.Exists(At("out", "My Mod.exporting")));
    }

    [Fact]
    public void ExportsCanBeReplacedButOtherFoldersAreNotTouched()
    {
        ModProject project = NewProject();
        project.SetFile("a.bin", Text("one"));
        Assert.True(ModExporter.ExportFolder(project, At("out")).IsSuccess);

        project.SetFile("a.bin", Text("two"));
        Assert.True(ModExporter.ExportFolder(project, At("out")).IsSuccess);
        Assert.Equal(Text("two"), File.ReadAllBytes(At("out", "USRDIR", "a.bin")));
        Assert.False(Directory.Exists(At("out.previous")));

        Write("documents/keep.txt", Text("mine"));
        Assert.False(ModExporter.ExportFolder(project, At("documents")).IsSuccess);
        Assert.Equal(Text("mine"), File.ReadAllBytes(At("documents", "keep.txt")));
    }

    [Theory]
    [InlineData("data", "hdd0", "game", "BLUS31540")]
    [InlineData("data", "update", "x", "y")]
    [InlineData("game", "PS3_GAME", "USRDIR", "x")]
    public void ExportRefusesTheGamesOwnFolders(params string[] parts)
    {
        ModProject project = NewProject();
        project.SetFile("a.bin", Text("x"));
        string target = At(parts);

        Assert.True(ModExporter.IsProtectedRecompFolder(target));
        Assert.False(ModExporter.ExportFolder(project, target).IsSuccess);
        Assert.False(ModExporter.ExportZip(project, target + ".zip").IsSuccess);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void ZipExportHasForwardSlashEntriesAndRoundTrips()
    {
        ModProject project = NewProject();
        project.SetFile("rendering/logo/a.rpsgl", Text("logo"));

        Result<ModExportResult> result = ModExporter.ExportZip(project, At("My Mod.zip"));

        Assert.True(result.IsSuccess, result.Error);
        using ZipArchive zip = ZipFile.OpenRead(At("My Mod.zip"));
        Assert.Contains(zip.Entries, e => e.FullName == "USRDIR/rendering/logo/a.rpsgl");
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains('\\'));
        using var reader = new StreamReader(zip.GetEntry("USRDIR/rendering/logo/a.rpsgl")!.Open());
        Assert.Equal("logo", reader.ReadToEnd());
        Assert.False(File.Exists(At("My Mod.zip.exporting")));
    }

    [Fact]
    public void ExportedModWinsOnceItIsInTheModsFolder()
    {
        using (GameFileIndex game = GameWithLogo())
        {
            ModProject project = NewProject();
            project.SetFile("rendering/logo/a.rpsgl", Text("RW4 edit"), game);
            Assert.True(ModExporter.ExportFolder(project, At("install", "mods", "My Mod")).IsSuccess);
        }

        using GameFileIndex after = GameInstallLocator.Open(At("install")).Value!;
        GameFile file = after.Find("rendering/logo/a.rpsgl")!;

        Assert.Equal(GameLayerKind.Mod, file.Winner.Layer.Kind);
        Assert.True(file.Winner.IsLoose);
        Assert.Equal(Text("RW4 edit"), after.ReadDecoded(file.Winner).Value);
    }

    [Fact]
    public void LauncherArgumentsPutInstallFirst()
    {
        IReadOnlyList<string> arguments = RecompLauncher.BuildImportArguments(At("install"), At("mod.zip"));

        Assert.Equal("--install", arguments[0]);
        Assert.Equal("--import-mod", arguments[2]);
    }

    [Fact]
    public void LauncherInstallFailsClearlyWithoutTheExe()
    {
        Assert.False(RecompLauncher.Import(At("install"), At("mod.zip")).IsSuccess);
    }

    [Fact]
    public void LauncherInstallReportsThisRunsResultWhenNoLogsFolderExistsYet()
    {
        Write($"install/{RecompLauncher.LauncherExeName}", Text("not really a launcher"));

        Result<ModInstallResult> result = RecompLauncher.Import(At("install"), At("mod.zip"), start =>
        {
            Assert.Equal("--install", start.ArgumentList[0]);
            Write("install/logs/mod_import.log", Text("installed My Mod\n"));
            return 0;
        });

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(result.Value!.Succeeded);
        Assert.Equal("installed My Mod", result.Value.Message);
    }

    [Fact]
    public void LauncherInstallDoesNotReuseAnOldResultOrHideAFailure()
    {
        Write($"install/{RecompLauncher.LauncherExeName}", Text("not really a launcher"));
        Write("install/logs/mod_import.log", Text("old success"));

        Result<ModInstallResult> result = RecompLauncher.Import(At("install"), At("mod.zip"), _ => 1);

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(result.Value!.Succeeded);
        Assert.DoesNotContain("old success", result.Value.Message);
    }

    [Fact]
    public void LauncherTimeoutIsAFailure()
    {
        Write($"install/{RecompLauncher.LauncherExeName}", Text("not really a launcher"));

        Assert.False(RecompLauncher.Import(At("install"), At("mod.zip"), _ => null).IsSuccess);
    }

    [Theory]
    [InlineData("""{"FormatVersion":1,"Name":"M","Files":[null]}""")]
    [InlineData("""{"FormatVersion":1,"Name":"M","Files":[{"Size":1,"Sha256":"00"}]}""")]
    [InlineData("""{"FormatVersion":1,"Name":"M","Files":[{"Path":"a.bin","Size":1}]}""")]
    public void OpenRefusesIncompleteFileRecords(string manifest)
    {
        NewProject();
        File.WriteAllText(At("project", ModProject.ManifestFileName), manifest);

        Assert.False(ModProject.Open(At("project")).IsSuccess);
    }

    [Theory]
    [InlineData("desktop.ini")]
    [InlineData("rendering/Thumbs.db")]
    [InlineData("rendering/._logo.rpsgl")]
    [InlineData("__MACOSX/logo.rpsgl")]
    public void NamesTheImporterDropsAreRefused(string path)
    {
        ModProject project = NewProject();
        Assert.True(project.SetFile(path, Text("x")).IsSuccess);

        Assert.Contains(ModIssueCode.DroppedByLauncherImport, Codes(ModValidator.Check(project)));
        Assert.False(ModExporter.ExportFolder(project, At("out")).IsSuccess);
    }

    [Fact]
    public void ProjectWithOnlyEmptyFilesCannotExportButOneEmptyFileAmongOthersWarns()
    {
        ModProject project = NewProject();
        project.SetFile("empty.bin", []);
        Assert.Contains(ModIssueCode.EmptyProject, Codes(ModValidator.Check(project)));

        project.SetFile("real.bin", Text("x"));
        ModIssue issue = Assert.Single(ModValidator.Check(project));
        Assert.Equal(ModIssueCode.EmptyFile, issue.Code);
        Assert.True(ModExporter.ExportFolder(project, At("out")).IsSuccess);
    }

    [Fact]
    public void ZipExportHasToEndInZip()
    {
        ModProject project = NewProject();
        project.SetFile("a.bin", Text("x"));

        Assert.False(ModExporter.ExportZip(project, At("mod.big")).IsSuccess);
        Assert.False(File.Exists(At("mod.big")));
    }

    [Theory]
    [InlineData("a=b")]
    [InlineData(" spaced")]
    public void ExportNamesMustSurviveAsModNames(string name)
    {
        ModProject project = NewProject();
        project.SetFile("a.bin", Text("x"));

        Assert.False(ModExporter.ExportFolder(project, At(name)).IsSuccess);
        Assert.False(ModExporter.ExportZip(project, At(name + ".zip")).IsSuccess);
    }

    [Fact]
    public void TrailingSlashesDoNotChangeTheExportOrInstalledName()
    {
        ModProject project = NewProject();
        project.SetFile("a.bin", Text("x"));
        string target = At("My Mod") + Path.DirectorySeparatorChar;

        Assert.True(ModExporter.ExportFolder(project, target).IsSuccess);
        Assert.Equal(At("My Mod"), RecompLauncher.BuildImportArguments(At("install"), target)[3]);
        Assert.Equal("My Mod", RecompLauncher.InstalledNameFor(target));
    }

    [Fact]
    public void SpotsAnInstalledModThatWouldBeReplaced()
    {
        Write("install/mods/My Mod/USRDIR/a.bin", Text("someone else's"));
        Write("exports/My Mod/USRDIR/a.bin", Text("x"));
        Write("exports/Other/USRDIR/a.bin", Text("x"));

        Assert.True(RecompLauncher.WouldReplaceInstalledMod(At("install"), At("exports", "My Mod")));
        Assert.True(RecompLauncher.WouldReplaceInstalledMod(At("install"), At("My Mod.zip")));
        Assert.False(RecompLauncher.WouldReplaceInstalledMod(At("install"), At("exports", "Other")));
    }

    [Fact]
    public void LocatorFindsAnInstallOneFolderDown()
    {
        Write("zip/NHL Legacy Native/nhl_settings.ini", Text(string.Empty));
        Write("zip/NHL Legacy Native/game/PS3_GAME/USRDIR/cacherender.big", EbTestData.EbArchive([("a.rpsgl", Text("x"))]));

        Result<GameFileIndex> opened = GameInstallLocator.Open(At("zip"));

        Assert.True(opened.IsSuccess, opened.Error);
        using GameFileIndex game = opened.Value!;
        Assert.NotNull(game.Find("a.rpsgl"));
    }

    [Fact]
    public void LocatorDoesNotGuessBetweenTwoInstalls()
    {
        Write("zip/one/nhl_settings.ini", Text(string.Empty));
        Write("zip/two/nhl_settings.ini", Text(string.Empty));

        Assert.False(GameInstallLocator.Open(At("zip")).IsSuccess);
    }

    [Fact]
    public void RemovingARosterThatWasNeverSetIsFine()
    {
        Assert.True(NewProject().RemoveRoster().IsSuccess);
    }

    [Fact]
    public void ZipExportCreatesItsFolder()
    {
        ModProject project = NewProject();
        project.SetFile("a.bin", Text("x"));

        Assert.True(ModExporter.ExportZip(project, At("new", "folder", "mod.zip")).IsSuccess);
        Assert.True(File.Exists(At("new", "folder", "mod.zip")));
    }
}
