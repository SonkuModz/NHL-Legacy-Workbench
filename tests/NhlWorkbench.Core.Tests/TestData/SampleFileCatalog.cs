using System.Runtime.CompilerServices;

namespace NhlWorkbench.Core.Tests.TestData;

/// <summary>
/// Resolves the real sample RX2/RPSGL corpus under References/, regardless of where the
/// test binaries actually run from. Uses <see cref="CallerFilePathAttribute"/> to find
/// this source file's location at compile time, then walks up to the repo root (marked
/// by the .sln) -- this keeps the suite portable: if References/ isn't present in a given
/// environment, every method here just returns empty/false and callers skip gracefully
/// rather than failing.
/// </summary>
public static class SampleFileCatalog
{
    public static readonly string RepoRoot = FindRepoRoot();

    public static string ReferencesRoot => Path.Combine(RepoRoot, "references", "my old legacy tool", "References");

    public static bool ReferencesAvailable => Directory.Exists(ReferencesRoot);

    /// <summary>The user's PS3 disc image. Tests only ever READ it.</summary>
    public static string Ps3Disc => Path.Combine(RepoRoot, "references", "NHL Legacy Edition.iso");

    public static bool Ps3DiscAvailable => File.Exists(Ps3Disc);

    public static string HeckEditorRoot => Path.Combine(ReferencesRoot, "Heck Texture Editor v1.2");

    /// <summary>Real BIGF archive used to ground-truth <see cref="NhlWorkbench.Core.Formats.Bigf.BigfArchive"/>'s
    /// byte layout (alignment, endianness) -- see <c>BigfRealFileTests</c>.</summary>
    public static string BigSampleFile => Path.Combine(ReferencesRoot, ".Big Texture File", "d24.big");

    public static bool BigSampleAvailable => File.Exists(BigSampleFile);

    /// <summary>Raw unedited stadium container used to ground-truth mesh write-back (attribute
    /// preservation + FInfo/VInfo descriptor patching) -- see <c>Rx2ModelWriterStadiumTests</c>.</summary>
    public static string StadiumContainerSampleFile => Path.Combine(ReferencesRoot, "Stadium RX2 Container", "stadium_26_container_0.rx2");

    public static bool StadiumContainerSampleAvailable => File.Exists(StadiumContainerSampleFile);

    private static string OverlaysRoot => Path.Combine(NhlLegacyEditionRenderingRoot, "..", "fe", "ion", "game", "overlays");

    /// <summary>Real overlay .big samples used to ground-truth the Scorebug Editor's Apt movie/shape/texture parsing -- see <c>OverlayArchiveRealFileTests</c>. Three different import-table sizes (0, 1, 2 imports) so the class-table group-count arithmetic in <c>AptMovieReader</c> is exercised against more than one shape.</summary>
    public static string OverlaySampleFile => Path.Combine(OverlaysRoot, "overlay_0.big");

    public static string OverlaySampleFileWithTwoImports => Path.Combine(OverlaysRoot, "overlay_39.big");

    public static string OverlaySampleFileLeaf => Path.Combine(OverlaysRoot, "overlay_53.big");

    public static bool OverlaySamplesAvailable =>
        File.Exists(OverlaySampleFile) && File.Exists(OverlaySampleFileWithTwoImports) && File.Exists(OverlaySampleFileLeaf);

    private static string NhlLegacyEditionRenderingRoot => Path.Combine(ReferencesRoot, "NHL Legacy Edition", "game", "_compiled", "rendering");

    public static string ArenaStadiumRoot => Path.Combine(NhlLegacyEditionRenderingRoot, "stadium");

    public static string ArenaIceSurfaceRoot => Path.Combine(NhlLegacyEditionRenderingRoot, "icesurface");

    public static string ArenaJumbotronRoot => Path.Combine(NhlLegacyEditionRenderingRoot, "jumbotron");

    public static bool ArenaAssetsAvailable => Directory.Exists(ArenaStadiumRoot);

    /// <summary>The user's live game database (teams / jerseys / stock equipment). Tests only ever READ it.</summary>
    public static string GameDatabaseFile => Path.Combine(ReferencesRoot, "NHL Legacy Edition", "game", "_compiled", "db", "nhlng.db");

    /// <summary>Heck's stock glove/helmet model + zone-mask files used by the equipment 3D preview.</summary>
    public static string EquipmentPreviewFolder => Path.Combine(ReferencesRoot, "Heck DB Editor v1.2", "helmet-gloves");

    public static bool EquipmentPreviewAvailable => Directory.Exists(EquipmentPreviewFolder);

    /// <summary>The game's language databases (English + 7 others). Tests only ever READ them.</summary>
    public static string LanguageDatabaseFolder => Path.Combine(ReferencesRoot, "NHL Legacy Edition", "game", "_compiled", "fe", "loc");

    public static IReadOnlyList<string> LanguageDatabaseFiles =>
        Directory.Exists(LanguageDatabaseFolder) ? Directory.GetFiles(LanguageDatabaseFolder, "nhl_*.db").OrderBy(f => f).ToList() : new List<string>();

    public static bool GameDatabaseAvailable => File.Exists(GameDatabaseFile);

    /// <summary>Vanilla copy of the same database shipped with the NHL Modding Studio reference source.</summary>
    public static string VanillaAnchorDatabaseFile => Path.Combine(
        ReferencesRoot, "nhl-modding-studio-sourcecode", "nhl-modding-studio-main", "anchors", "xbox360", "nhllegacy", "databases", "nhlng.db");

    public static bool VanillaAnchorDatabaseAvailable => File.Exists(VanillaAnchorDatabaseFile);

    public static string JerseySourceRoot => Path.Combine(ReferencesRoot, "2526 conversion", "out", "jerseys");

    public static string JerseyDestinationRoot => Path.Combine(NhlLegacyEditionRenderingRoot, "jersey");

    public static bool JerseyAssetsAvailable => Directory.Exists(JerseySourceRoot) && Directory.Exists(JerseyDestinationRoot);

    /// <summary>Lives outside the repo entirely (a separate conversion-output tree on the dev
    /// machine), unlike the jersey source root -- <see cref="UniformAssetsAvailable"/> gates every
    /// real-tree test the same way the other Available flags do, so the suite stays portable.</summary>
    public static string UniformSourceRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "2526 conversion", "out", "renderinout");

    public static string UniformDestinationRoot => NhlLegacyEditionRenderingRoot;

    public static bool UniformAssetsAvailable => Directory.Exists(UniformSourceRoot) && Directory.Exists(UniformDestinationRoot);

    private static string AbkRoot => Path.Combine(ReferencesRoot, "Xbox and Ps3 ABK");

    /// <summary>PS3 sample used to ground-truth the ABKC/S10A EA-XAS decode path -- see <c>AbkContainerRealFileTests</c>.</summary>
    public static string AbkGoalPs3SampleFile => Path.Combine(AbkRoot, "024_goalPS3.abk");

    /// <summary>Xbox 360 sample (same clip as <see cref="AbkGoalPs3SampleFile"/>) used to ground-truth the XMA2 decode path.</summary>
    public static string AbkGoal360SampleFile => Path.Combine(AbkRoot, "024_goal360.abk");

    public static bool AbkSamplesAvailable => File.Exists(AbkGoalPs3SampleFile) && File.Exists(AbkGoal360SampleFile);

    /// <summary>ffmpeg.exe bundled alongside the user's PS3 ABK tools -- used to decode XMA2 audio. Not
    /// checked into the repo, so XMA2-path tests skip gracefully when it's absent (see <see cref="AbkFfmpegAvailable"/>).</summary>
    public static string AbkFfmpegExePath => Path.Combine(AbkRoot, "ps3 abk writer", "ffmpeg.exe");

    public static bool AbkFfmpegAvailable => File.Exists(AbkFfmpegExePath);

    /// <summary>The user's own PS3 EA-XAS insertion tool -- see <see cref="AbkFfmpegExePath"/> for why
    /// this is referenced from References/ rather than checked into the repo.</summary>
    public static string AbkInsertExePath => Path.Combine(AbkRoot, "ps3 abk writer", "ABK_insert.exe");

    /// <summary>Microsoft's real XDK XMA2 encoder, used by <c>AbkXbox360Writer</c>. Also referenced
    /// from References/ rather than checked in -- same reasoning as the other ABK tools.</summary>
    public static string AbkXma2EncodeExePath => Path.Combine(AbkRoot, "xma2encode.exe");

    public static bool AbkXma2EncodeAvailable => File.Exists(AbkXma2EncodeExePath);

    /// <summary>Real multi-sound (3x stereo/32kHz XMA2) game audio bank -- used to cross-validate
    /// the per-sound "quirk field" formula in <c>AbkXbox360Writer</c> against a second file/channel
    /// count, after the mono/44.1kHz-only version of that formula was confirmed in-game to corrupt
    /// the file. See <c>AbkXbox360WriterFormulaTests</c>.</summary>
    public static string AbkChantSampleFile => Path.Combine(NhlLegacyEditionRenderingRoot, "..", "audio", "sfx", "026_chant.abk");

    public static bool AbkChantSampleAvailable => File.Exists(AbkChantSampleFile);

    /// <summary>Hand-verified-good sample assets (9 .rx2 + 6 .rpsgl) -- used for structural/roundtrip assertions.</summary>
    private static string CuratedAssetsRoot => Path.Combine(HeckEditorRoot, "rx2_modelassets");

    /// <summary>~1000 real .rx2 files -- used for broad "never throws" smoke coverage only.</summary>
    private static string ExtraModelViewersRoot => Path.Combine(HeckEditorRoot, "Extra Model Viewers");

    public static IReadOnlyList<string> AllSampleRx2Files() => FindFiles("*.rx2", CuratedAssetsRoot, ExtraModelViewersRoot);

    public static IReadOnlyList<string> AllSampleRpsglFiles() => FindFiles("*.rpsgl", CuratedAssetsRoot, ExtraModelViewersRoot);

    public static IReadOnlyList<string> CuratedRx2Files() => FindFiles("*.rx2", CuratedAssetsRoot);

    public static IReadOnlyList<string> CuratedRpsglFiles() => FindFiles("*.rpsgl", CuratedAssetsRoot);

    private static IReadOnlyList<string> FindFiles(string pattern, params string[] roots)
    {
        var results = new List<string>();
        foreach (string root in roots)
        {
            if (Directory.Exists(root))
            {
                results.AddRange(Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories));
            }
        }

        return results;
    }

    private static string FindRepoRoot([CallerFilePath] string thisFilePath = "")
    {
        DirectoryInfo? dir = new FileInfo(thisFilePath).Directory;
        while (dir is not null
            && !File.Exists(Path.Combine(dir.FullName, "NhlLegacyWorkbench.sln"))
            && !File.Exists(Path.Combine(dir.FullName, "NhlLegacyWorkbench.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
