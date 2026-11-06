using System.IO.Compression;
using NhlWorkbench.Core.Mods;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>Snapshots and the list of installed mods, on throwaway folders.</summary>
public sealed class ModSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mod-safety").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    private ModProject NewMod(string name = "Mine") => ModProject.Create(Path.Combine(_root, name), name).Value!;

    [Fact]
    public void AModCanGoBackToASnapshot()
    {
        ModProject mod = NewMod();
        Assert.True(mod.SetFile("audio/sfx/a.abk", [1, 2, 3]).IsSuccess);
        Assert.True(mod.SetRoster([9, 9], null).IsSuccess);
        var snapshot = ModSnapshots.Create(mod, "Good: version / one");
        Assert.True(snapshot.IsSuccess, snapshot.Error);
        Assert.Equal("Good- version - one", snapshot.Value!.Label);

        Assert.True(mod.SetFile("audio/sfx/a.abk", [4, 4]).IsSuccess);
        Assert.True(mod.SetFile("audio/sfx/new.abk", [5]).IsSuccess);
        Assert.True(mod.UpdateRoster([7]).IsSuccess);

        var restored = ModSnapshots.Restore(mod, snapshot.Value);
        Assert.True(restored.IsSuccess, restored.Error);
        ModProject back = restored.Value!.Project;
        Assert.Equal([1, 2, 3], back.ReadFile("audio/sfx/a.abk").Value);
        Assert.False(back.HasFile("audio/sfx/new.abk"));
        Assert.False(File.Exists(Path.Combine(mod.Folder, "files", "audio", "sfx", "new.abk")));
        Assert.Equal([9, 9], File.ReadAllBytes(back.RosterPath));
        Assert.Contains("audio/sfx/new.abk", restored.Value.AffectedPaths);

        // The copy from just before can undo the undo.
        Assert.Equal(2, ModSnapshots.List(back).Count);
        var again = ModSnapshots.Restore(back, restored.Value.SafetyCopy);
        Assert.True(again.IsSuccess, again.Error);
        Assert.True(again.Value!.Project.HasFile("audio/sfx/new.abk"));
        Assert.Equal([7], File.ReadAllBytes(again.Value.Project.RosterPath));
        Assert.False(Directory.Exists(Path.Combine(mod.Folder, "snapshots", ".restoring")));
    }

    [Fact]
    public void SnapshotsStayOutOfExports()
    {
        // The checks want a real roster save, so this needs the one in the installed mod. It's only read.
        string save = Path.Combine(SampleFileCatalog.RepoRoot, "NHL-Legacy-Native_v1.0.0", "NHL Legacy Native", "mods", "26-27 Conversion.zip (1)", "roster", "SYS-DATA");
        if (!File.Exists(save))
        {
            return;
        }

        ModProject mod = NewMod();
        Assert.True(mod.SetFile("audio/sfx/a.abk", [1, 2, 3]).IsSuccess);
        Assert.True(mod.SetRoster(File.ReadAllBytes(save), null).IsSuccess);
        Assert.True(ModSnapshots.Create(mod, "Keep").IsSuccess);

        string target = Path.Combine(_root, "export");
        var exported = ModExporter.ExportFolder(mod, target, null);
        Assert.True(exported.IsSuccess, exported.Error);
        List<string> files = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(target, f).Replace('\\', '/')).ToList();
        Assert.DoesNotContain(files, f => f.Contains("snapshots") || f.EndsWith(".original", StringComparison.Ordinal) || f.EndsWith(".zip", StringComparison.Ordinal));
        Assert.Contains("roster/SYS-DATA", files);
    }

    [Fact]
    public void ABadSnapshotChangesNothing()
    {
        ModProject mod = NewMod();
        Assert.True(mod.SetFile("audio/sfx/a.abk", [1]).IsSuccess);
        Directory.CreateDirectory(ModSnapshots.FolderFor(mod));

        string notZip = Path.Combine(ModSnapshots.FolderFor(mod), "2026-01-01_000000 Junk.zip");
        File.WriteAllBytes(notZip, [1, 2, 3]);
        Assert.False(ModSnapshots.Restore(mod, ModSnapshots.List(mod)[0]).IsSuccess);
        File.Delete(notZip);

        string escape = Path.Combine(ModSnapshots.FolderFor(mod), "2026-01-01_000000 Escape.zip");
        using (ZipArchive zip = ZipFile.Open(escape, ZipArchiveMode.Create))
        {
            zip.CreateEntry("../../evil.txt");
        }

        Assert.False(ModSnapshots.Restore(mod, ModSnapshots.List(mod)[0]).IsSuccess);
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.Equal([1], mod.ReadFile("audio/sfx/a.abk").Value);
        Assert.True(ModSnapshots.Delete(ModSnapshots.List(mod)[0]).IsSuccess);
        Assert.Empty(ModSnapshots.List(mod));
    }

    [Fact]
    public void InstalledModsAreReadInTheLaunchersOrder()
    {
        string install = Path.Combine(_root, "install");
        void Add(string name, params string[] files)
        {
            foreach (string file in files)
            {
                string path = Path.Combine(install, "mods", name, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, [1]);
            }
        }

        Add("Below = One", "USRDIR/Audio/SFX/a.abk", "USRDIR/other.big", "roster/SYS-DATA");
        Add("Top", "USRDIR/audio/sfx/b.abk");
        Add("Mine", "USRDIR/audio/sfx/a.abk");
        Add("Off", "USRDIR/audio/sfx/a.abk");
        File.WriteAllText(Path.Combine(install, "nhl_settings.ini"), """
            ; comment
            [paths]
            disc = a=b
            [launcher]
            roster = Below = One
            mod_order = Top|Mine|Below = One
            [mods]
            Below = One = 1
            Top = 1
            Off = 0
            """);

        ModProject mine = NewMod();
        Assert.True(mine.SetFile("audio/sfx/a.abk", [2]).IsSuccess);

        IReadOnlyList<InstalledMod> mods = InstalledMods.Read(install, mine);
        Assert.Equal(["Top", "Mine", "Below = One", "Off"], mods.Select(m => m.Name));
        Assert.True(mods[1].IsMine);
        Assert.Empty(mods[1].Shared);

        InstalledMod below = mods[2];
        Assert.True(below.IsEnabled && below.HasRoster && below.IsRosterInUse);
        Assert.Equal(2, below.FileCount);
        Assert.Equal(["audio/sfx/a.abk"], below.Shared);
        Assert.True(InstalledMods.MineWinsOver(below, mods));
        Assert.False(InstalledMods.MineWinsOver(mods[0], mods));
        Assert.False(mods[3].IsEnabled);

        Assert.Empty(InstalledMods.Read(Path.Combine(_root, "nowhere"), null));
    }

    [Fact]
    public void FilesCanBeCopiedFromAnInstalledMod()
    {
        string install = Path.Combine(_root, "install");
        string usrdir = Path.Combine(install, "mods", "Theirs", "USRDIR");
        Directory.CreateDirectory(Path.Combine(usrdir, "Audio", "SFX"));
        Directory.CreateDirectory(Path.Combine(usrdir, "db"));
        File.WriteAllBytes(Path.Combine(usrdir, "Audio", "SFX", "A.abk"), [1, 2]);
        File.WriteAllBytes(Path.Combine(usrdir, "db", "nhlng.db"), [3]);

        IReadOnlyList<InstalledModFile> files = InstalledMods.FilesOf(install, "Theirs");
        Assert.Equal(["audio/sfx/a.abk", "db/nhlng.db"], files.Select(f => f.GamePath));
        Assert.Equal(2, files[0].Size);
        Assert.Empty(InstalledMods.FilesOf(install, "../Theirs"));
        Assert.Empty(InstalledMods.FilesOf(install, "Nobody"));

        ModProject mine = NewMod();
        Assert.True(mine.SetFile("db/nhlng.db", [9]).IsSuccess);
        (int copied, IReadOnlyList<string> problems) = InstalledMods.CopyInto(mine, files.Take(1), null);
        Assert.Equal(1, copied);
        Assert.Empty(problems);
        Assert.Equal([1, 2], mine.ReadFile("audio/sfx/a.abk").Value);
        Assert.Equal([9], mine.ReadFile("db/nhlng.db").Value);
        Assert.Equal([1, 2], File.ReadAllBytes(Path.Combine(usrdir, "Audio", "SFX", "A.abk")));

        (copied, problems) = InstalledMods.CopyInto(mine, [new InstalledModFile("db/gone.db", Path.Combine(usrdir, "gone"), 1)], null);
        Assert.Equal(0, copied);
        Assert.Single(problems);
    }
}
