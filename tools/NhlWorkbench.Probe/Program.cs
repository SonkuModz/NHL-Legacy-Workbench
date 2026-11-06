using System.Diagnostics;
using System.Text;
using NhlWorkbench.Core.Compression;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Eb;
using NhlWorkbench.Core.Formats.Iso;
using NhlWorkbench.Core.Formats.Rpsgl;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;

// Read-only check of a PS3 disc image.
//   NhlWorkbench.Probe <disc.iso>             list the disc's EB archives
//   NhlWorkbench.Probe <disc.iso> --decode    also decode every entry and parse every RPSGL
//   NhlWorkbench.Probe index <disc.iso or install folder> [--nested] [search words]
//   NhlWorkbench.Probe modcheck <disc.iso> <game path> <new empty work folder> <mod name>
//       runs one game file through a project and both exports, using a same-size test edit
if (args.Length == 0)
{
    Console.WriteLine("usage: NhlWorkbench.Probe <disc.iso> [--decode]");
    Console.WriteLine("       NhlWorkbench.Probe index <disc.iso or install folder> [--nested] [search words]");
    Console.WriteLine("       NhlWorkbench.Probe modcheck <disc.iso> <game path> <new empty work folder> <mod name>");
    return 2;
}

if (args[0] == "index")
{
    return RunIndex(args[1..]);
}

if (args[0] == "import")
{
    // Runs <launcher folder>\NHLLauncher.exe against <install folder>, which can be a different, throwaway one.
    if (args.Length != 4)
    {
        return 2;
    }

    Result<ModInstallResult> imported = RecompLauncher.Import(args[1], args[2], args[3]);
    Console.WriteLine(imported.IsSuccess ? $"succeeded={imported.Value!.Succeeded}: {imported.Value.Message}" : imported.Error);
    return imported.IsSuccess && imported.Value!.Succeeded ? 0 : 1;
}

if (args[0] == "textures")
{
    return args.Length == 3 ? RunTextures(args[1], args[2]) : 2;
}

if (args[0] == "texmap")
{
    return args.Length == 2 ? RunTextureMap(args[1]) : 2;
}

if (args[0] == "teamdb")
{
    return args.Length == 2 ? RunTeamDb(args[1]) : 2;
}

if (args[0] == "rosterdb")
{
    return args.Length >= 2 ? RunRosterDb(args[1], args.Skip(2).ToArray()) : 2;
}

if (args[0] == "banks")
{
    return args.Length == 3 ? RunBanks(args[1], args[2]) : 2;
}

if (args[0] == "abk")
{
    return args.Length == 2 ? RunAbk(args[1]) : 2;
}

if (args[0] == "dds")
{
    // dds <source> <search, or "all"> [folder to save the first few]: exports every texture and checks each file survives a round trip.
    return args.Length is 3 or 4 ? RunDds(args[1], args[2], args.Length == 4 ? args[3] : null) : 2;
}

if (args[0] == "modcheck")
{
    return args.Length == 5 ? RunModCheck(args[1], args[2], args[3], args[4]) : 2;
}

bool decode = args.Contains("--decode");
Result<Iso9660Image> opened = Iso9660Image.Open(args[0]);
if (!opened.IsSuccess)
{
    Console.WriteLine(opened.Error);
    return 1;
}

Iso9660Image image = opened.Value!;
Console.WriteLine($"{image.Files.Count} files on disc, {image.Files.Sum(f => f.Length):N0} bytes");

var clock = Stopwatch.StartNew();
int archives = 0;
long totalEntries = 0, totalDecoded = 0, rpsglOk = 0, rpsglFailed = 0, rpsglTextures = 0;
var failures = new List<string>();
var magics = new Dictionary<string, long>();

foreach (IsoFile file in image.Files)
{
    using Stream stream = image.OpenFile(file);
    var head = new byte[4];
    stream.ReadExactly(head);
    if (!EbArchive.IsEbArchive(head))
    {
        continue;
    }

    Result<EbArchive> result = EbArchive.Open(stream, file.Path, leaveOpen: true);
    if (!result.IsSuccess)
    {
        failures.Add(result.Error!);
        Console.WriteLine($"FAILED {file.Path}: {result.Error}");
        continue;
    }

    using EbArchive archive = result.Value!;
    archives++;
    totalEntries += archive.Entries.Count;
    foreach (ParseWarning warning in result.Warnings)
    {
        Console.WriteLine($"  warning: {warning.Message}");
    }

    long archiveDecoded = 0;
    if (decode)
    {
        foreach (EbEntry entry in archive.Entries)
        {
            Result<byte[]> data = archive.ReadDecoded(entry);
            if (!data.IsSuccess)
            {
                failures.Add($"{file.Path}!{entry.Path}: {data.Error}");
                continue;
            }

            archiveDecoded += data.Value!.Length;
            string magic = Magic(data.Value);
            magics[magic] = magics.GetValueOrDefault(magic) + 1;
            if (RpsglReader.IsRpsgl(data.Value))
            {
                Result<RpsglContainer> rpsgl = RpsglReader.Parse(data.Value, entry.Path);
                if (rpsgl.IsSuccess)
                {
                    rpsglOk++;
                    rpsglTextures += rpsgl.Value!.Textures.Count;
                }
                else
                {
                    rpsglFailed++;
                    if (rpsglFailed <= 20)
                    {
                        failures.Add($"RPSGL {entry.Path}: {rpsgl.Error}");
                    }
                }
            }
        }
    }

    totalDecoded += archiveDecoded;
    string sample = string.Join(", ", archive.Entries.Take(2).Select(e => e.Path));
    Console.WriteLine($"{file.Path,-44} {file.Length,14:N0}  {file.Extents.Count} ext  0x{archive.Variant:X}  {archive.Entries.Count,6:N0} entries  {archiveDecoded,15:N0} decoded  [{sample}]");
}

Console.WriteLine();
Console.WriteLine($"{archives} EB archives, {totalEntries:N0} entries, {clock.Elapsed.TotalSeconds:N1}s");
if (decode)
{
    Console.WriteLine($"{totalDecoded:N0} bytes decoded");
    Console.WriteLine($"RPSGL: {rpsglOk:N0} parsed ({rpsglTextures:N0} textures), {rpsglFailed:N0} failed");
    Console.WriteLine("Top payload types:");
    foreach ((string magic, long count) in magics.OrderByDescending(m => m.Value).Take(15))
    {
        Console.WriteLine($"  {magic,-12} {count,8:N0}");
    }
}

Console.WriteLine($"{failures.Count} failures");
foreach (string failure in failures.Take(40))
{
    Console.WriteLine($"  {failure}");
}

return failures.Count == 0 ? 0 : 1;

static int RunIndex(string[] options)
{
    var clock = Stopwatch.StartNew();
    Result<GameFileIndex> built = GameInstallLocator.Open(options[0]);
    if (!built.IsSuccess)
    {
        Console.WriteLine(built.Error);
        return 1;
    }

    using GameFileIndex index = built.Value!;
    double buildSeconds = clock.Elapsed.TotalSeconds;
    Console.WriteLine($"Layers: {string.Join(" > ", index.Layers.Select(l => $"{l.Name} ({l.Source.Files.Count:N0} files)"))}");
    Console.WriteLine($"{index.Files.Count:N0} paths, {index.MountedArchives.Count()} mounted archives, built in {buildSeconds:N2}s");

    int looseWinners = index.Files.Count(f => f.Winner.IsLoose);
    GameFile[] multi = index.Files.Where(f => f.Copies.Count > 1).ToArray();
    GameFile[] uncertain = index.Files.Where(f => f.WinnerIsUncertain).ToArray();
    Console.WriteLine($"Loose winners: {looseWinners:N0}. Paths with several copies: {multi.Length:N0}. Uncertain winners: {uncertain.Length:N0}.");
    foreach (GameFile file in uncertain.Take(10))
    {
        Console.WriteLine($"  {file.Path}: {string.Join(", ", file.Copies.Select(c => c.ArchivePath ?? "loose"))}");
    }

    foreach (ParseWarning warning in index.Warnings.Take(20))
    {
        Console.WriteLine($"  warning: {warning.Source}: {warning.Message}");
    }

    if (options.Contains("--types"))
    {
        Console.WriteLine("By type:");
        foreach (var group in index.Files.GroupBy(f => Path.GetExtension(f.Path)).OrderByDescending(g => g.Count()).Take(14))
        {
            Console.WriteLine($"  {group.Key,-10} {group.Count(),7:N0}");
        }

        Console.WriteLine("By folder:");
        foreach (var group in index.Files
            .GroupBy(f => string.Join('/', f.Path.Split('/').Take(2)) + (f.Path.Count(c => c == '/') > 1 ? "/" : string.Empty))
            .Where(g => g.Count() >= 20)
            .OrderByDescending(g => g.Count())
            .Take(40))
        {
            string sample = Path.GetFileName(group.First().Path);
            Console.WriteLine($"  {group.Key,-34} {group.Count(),7:N0}  e.g. {sample}");
        }
    }

    int failures = 0;
    if (options.Contains("--nested"))
    {
        clock.Restart();
        long archives = 0, members = 0, unreadable = 0;
        var mounted = index.MountedArchives.ToHashSet();
        foreach (GameFile file in index.Files)
        {
            // Mounted archives are already expanded in the index, and can be over a gigabyte each.
            if (mounted.Contains(file.Path))
            {
                continue;
            }

            Result<byte[]> data = index.ReadDecoded(file.Winner);
            if (!data.IsSuccess)
            {
                failures++;
                Console.WriteLine($"  FAILED {file.Path}: {data.Error}");
                continue;
            }

            if (!NestedArchive.IsArchive(data.Value!))
            {
                continue;
            }

            Result<NestedArchive> nested = NestedArchive.Open(data.Value!, file.Path);
            if (!nested.IsSuccess)
            {
                failures++;
                Console.WriteLine($"  FAILED {file.Path}: {nested.Error}");
                continue;
            }

            archives++;
            members += nested.Value!.Entries.Count;
            foreach (NestedEntry entry in nested.Value.Entries)
            {
                if (!nested.Value.Read(entry.Name).IsSuccess)
                {
                    unreadable++;
                    failures++;
                    if (unreadable <= 20)
                    {
                        Console.WriteLine($"  FAILED {file.Path}!{entry.Name}");
                    }
                }
            }
        }

        Console.WriteLine($"Nested: {archives:N0} archives, {members:N0} members, {unreadable:N0} unreadable, {clock.Elapsed.TotalSeconds:N1}s");
    }

    string query = string.Join(' ', options.Skip(1).Where(o => !o.StartsWith("--")));
    if (query.Length > 0)
    {
        IReadOnlyList<GameFile> found = index.Search(query, 25);
        Console.WriteLine($"Search \"{query}\": {found.Count} shown");
        foreach (GameFile file in found)
        {
            GameFileCopy winner = file.Winner;
            Console.WriteLine($"  {file.Path}  [{winner.Layer.Name}{(winner.IsLoose ? ", loose" : $", {winner.ArchivePath}")}]{(file.WinnerIsUncertain ? " uncertain" : string.Empty)}");
        }
    }

    return failures == 0 ? 0 : 1;
}

static int RunTeamDb(string source)
{
    using GameFileIndex index = GameInstallLocator.Open(source).Value!;
    Result<TeamCatalog> built = TeamCatalog.Build(index);
    if (!built.IsSuccess)
    {
        Console.WriteLine(built.Error);
        return 1;
    }

    foreach (GameTeam team in built.Value!.Teams)
    {
        Console.WriteLine($"{team.League,-24} art={team.ArtId,-6} {team.Abbreviation,-6} {team.Name,-34} kits={team.Kits.Count(k => k.IsListed)}+{team.Kits.Count(k => !k.IsListed)} art={team.Art.Count} arena='{team.ArenaName}'");
    }

    var real = built.Value.Teams.Where(t => t.HasDatabaseRow).ToList();
    Console.WriteLine($"teams {real.Count}, unlisted art numbers {built.Value.Teams.Count - real.Count}");
    Console.WriteLine($"listed kits {real.Sum(t => t.Kits.Count(k => k.IsListed))}, complete {real.Sum(t => t.Kits.Count(k => k.IsListed && new[] { "Jersey", "Pants", "Socks" }.All(p => k.Pieces.Any(x => x.Label == p))))}");
    foreach (var group in real.SelectMany(t => t.Art).Concat(real.SelectMany(t => t.Kits.SelectMany(k => k.Pieces))).GroupBy(a => a.Label.Split(' ')[0]).OrderBy(g => g.Key))
    {
        Console.WriteLine($"  {group.Key,-14} {group.Count()}");
    }

    return 0;
}

// rosterdb <disc, install or SYS-DATA file> [more SYS-DATA files]: read-only look at the tables that make up a roster.
static int RunRosterDb(string source, string[] more)
{
    var dbs = new List<(string Name, NhlWorkbench.Core.Tdb.TdbDatabase Db)>();
    if (File.Exists(source) && !source.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
    {
        more = [source, .. more];
    }
    else
    {
        using GameFileIndex index = GameInstallLocator.Open(source).Value!;
        GameFile file = index.Find("db/nhlng.db")!;
        dbs.Add(("nhlng.db", NhlWorkbench.Core.Tdb.TdbDatabase.Open(index.ReadDecoded(TextureCatalog.OriginalCopy(file)!).Value!).Value!));
    }

    foreach (string path in more)
    {
        var saved = NhlWorkbench.Core.Tdb.TdbSaveContainer.Parse(File.ReadAllBytes(path));
        Console.WriteLine($"{path}: {(saved.IsSuccess ? "ok" : saved.Error)}");
        if (saved.IsSuccess)
        {
            var opened = NhlWorkbench.Core.Tdb.TdbDatabase.Open(saved.Value!.InnerDatabase);
            Console.WriteLine($"  file {saved.Value.Original.Length} inner {saved.Value.InnerDatabase.Length} schema problems {opened.Value?.SchemaProblems.Count} {string.Join("; ", opened.Value?.SchemaProblems.Take(4) ?? [])} crc problems {opened.Value?.ChecksumProblems.Count}");
            dbs.Add((Path.GetFileName(Path.GetDirectoryName(path)!) ?? path, opened.Value!));
        }
    }

    foreach ((string name, var db) in dbs)
    {
        Console.WriteLine($"== {name}: {db.File.Tables.Count} tables");
        Console.WriteLine("  " + string.Join(" ", db.File.Tables.Select(t => $"{db.Meta.FindTable(t.Id)?.Name ?? t.Id}:{t.RowCount}/{t.MaxRecords}")));
    }

    var main = dbs[^1].Db;
    foreach ((string id, string[] names) in new[] { ("dSvy", new[] { "c_speed", "c_passing", "c_defawareness" }), ("mHuy", new[] { "c_gsh", "c_ssl", "c_positioning" }) })
    {
        var t = main.GetTable(id)!;
        int blank = 0, blankWithRatings = 0;
        for (int r = 0; r < t.RowCount; r++)
        {
            if (t.GetInt(r, "c_overall") == 0)
            {
                blank++;
                blankWithRatings += names.Any(n => t.GetInt(r, n) != 0) ? 1 : 0;
            }
        }

        Console.WriteLine($"OVERALL {id}: rows {t.RowCount}, overall blank {blank}, of which have other ratings {blankWithRatings}");
    }

    foreach (string table in new[] { "exhibitionplayers", "exhibitionplayerbiotable", "exhibitionrostertable", "exhibitionskaterai", "exhibitiongoalieai", "exhibitionskaterequipment", "exhibitiongoalieequipment" })
    {
        var meta = main.Meta.Tables.FirstOrDefault(t => t.Name == table);
        if (meta is null)
        {
            Console.WriteLine($"-- {table} missing");
            continue;
        }

        Console.WriteLine($"-- {table} ({meta.Id}) " + string.Join(" ", meta.Fields.Select(f => f.Name + (f.IsText ? "$" : $":{f.Depth}"))));
    }

    return 0;
}

// abk <disc or install>: read-only survey of every sound bank.
// banks <disc or install> <ffmpeg>: read-only survey of the sound banks' layout, names and audio.
static int RunBanks(string source, string ffmpeg)
{
    ffmpeg = Path.GetFullPath(ffmpeg);
    using GameFileIndex index = GameInstallLocator.Open(source).Value!;
    var banks = index.Files.Where(f => f.Path.EndsWith(".abk", StringComparison.Ordinal)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    var asts = index.Files.Where(f => f.Path.EndsWith(".ast", StringComparison.Ordinal)).ToList();
    Console.WriteLine("asts: " + string.Join(" ", asts.Select(a => Path.GetFileName(a.Path) + ":" + (a.Winner.StoredSize / 1048576) + "MB/" + (a.Winner.ArchivePath is null ? "loose" : Path.GetFileName(a.Winner.ArchivePath)))));
    int failed = 0, unnamed = 0, named = 0, stream = 0, ram = 0, looped = 0, ramLoop2 = 0;
    long ramBytes = 0;
    var gaps = new Dictionary<string, int>();
    var starts = new Dictionary<string, int>();
    var offs = new Dictionary<string, int>();
    var pads = new Dictionary<string, int>();
    var bad = new List<string>();
    NhlWorkbench.Core.Formats.Abk.AbkBank? sample = null;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    foreach (GameFile file in banks)
    {
        byte[] data = index.ReadDecoded(file.Winner).Value!;
        var parsed = NhlWorkbench.Core.Formats.Abk.AbkBank.Parse(data);
        if (!parsed.IsSuccess) { failed++; Console.WriteLine($"FAIL {file.Path}: {parsed.Error}"); continue; }
        var bank = parsed.Value!;
        uint Be(int at) => (uint)((data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3]);
        long table = Be(0x18), size = Be(0x24), a = Be(0x30), b = Be(0x34), c = Be(0x38);
        string pad = $"tableMod16={table % 16} dataEndToA={a - (table + size)} aMod16={a % 16} bMod16={b % 16} cMod16={c % 16} aToB={Math.Min(b - a, 64)}";
        pads[pad] = pads.GetValueOrDefault(pad) + 1;
        var ordered = bank.Records.OrderBy(r => r.Offset).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            int next = i + 1 < ordered.Count ? ordered[i + 1].Offset : (int)(table + size);
            string gap = (next - ordered[i].End).ToString() + (i + 1 < ordered.Count ? "" : " last");
            gaps[gap] = gaps.GetValueOrDefault(gap) + 1;
            string st = $"recMod16={(ordered[i].Offset - table) % 16}";
            starts[st] = starts.GetValueOrDefault(st) + 1;
            var r = ordered[i];
            if (r.IsStream) stream++; else { ram++; ramBytes += r.Segments.Sum(x => x.Length); }
            if (r.IsLooped) looped++;
            if (r.Segments.Count == 2) ramLoop2++;
            if (r.Name.Length == 0) unnamed++; else named++;
            if (!r.IsStream && r.CodecId == 5)
            {
                var frames = NhlWorkbench.Core.Formats.Abk.EaLayer3.Read(data, r.Segments[^1].Offset, r.Segments[^1].Length);
                if (!frames.IsSuccess) { bad.Add($"{file.Path}#{r.Index}: {frames.Error}"); continue; }
                var list = frames.Value!;
                int ee = list.FindIndex(g => g.Pcm is not null);
                string o = $"ch{r.ChannelConfig} segs{r.Segments.Count} ee@{ee} pcm{(ee >= 0 ? list[ee].PcmSamples : -1)} extra={(list.Count * 576L + (ee >= 0 ? list[ee].PcmSamples : 0) - r.Segments[^1].Samples) / 576}";
                offs[o] = offs.GetValueOrDefault(o) + 1;
                // A parse and rewrite of the frames has to give back the same bytes.
                byte[] again = NhlWorkbench.Core.Formats.Abk.EaLayer3.Write(list);
                if (!again.AsSpan().SequenceEqual(data.AsSpan(r.Segments[^1].Offset, again.Length))) bad.Add($"{file.Path}#{r.Index}: rewrite differs");
            }
        }

        if (Path.GetFileName(file.Path).Contains("goal", StringComparison.OrdinalIgnoreCase) && bank.Records.Count > 0) sample ??= bank;
    }

    Console.WriteLine($"read {banks.Count} banks in {sw.ElapsedMilliseconds} ms, failed {failed}, records ram {ram} ({ramBytes / 1048576} MB) stream {stream}, looped {looped}, two-part {ramLoop2}, named {named} unnamed {unnamed}");
    foreach (var (name, table) in new[] { ("layout", pads), ("gap after record", gaps), ("record start", starts), ("frames", offs) })
    {
        Console.WriteLine(name);
        foreach (var kv in table.OrderByDescending(k => k.Value).Take(14)) Console.WriteLine($"  {kv.Value} x {kv.Key}");
    }

    Console.WriteLine($"problems {bad.Count}");
    foreach (string line in bad.Take(10)) Console.WriteLine("  " + line);
    Console.WriteLine("names: " + string.Join(" | ", banks.Select(f => Path.GetFileNameWithoutExtension(f.Path)).Take(217)));
    foreach (GameFile file in banks.Where(f => f.Path.Contains("goal") || f.Path.Contains("chant")).Take(8))
    {
        var bank = NhlWorkbench.Core.Formats.Abk.AbkBank.Parse(index.ReadDecoded(file.Winner).Value!).Value!;
        Console.WriteLine(file.Path + ": " + string.Join(", ", bank.Records.Take(5).Select(r => $"{r.Index}:{r.Name} {r.Seconds:0.0}s c{r.CodecId} ch{r.ChannelConfig} {r.SampleRate} {(r.IsStream ? "stream" : "ram")}{(r.IsLooped ? " loop" : "")}")));
    }

    // Decode: the raw opening samples should run on smoothly into the decoder's output.
    var jumps = new List<double>();
    int decodedOk = 0, decodeFailed = 0;
    foreach (GameFile file in banks.Where(f => !f.Path.Contains("crowd")).Take(40))
    {
        var bank = NhlWorkbench.Core.Formats.Abk.AbkBank.Parse(index.ReadDecoded(file.Winner).Value!).Value!;
        foreach (var r in bank.Records.Where(r => !r.IsStream && r.CodecId == 5 && r.ChannelConfig <= 1 && !r.IsLooped).Take(2))
        {
            var audio = NhlWorkbench.Core.Formats.Abk.AbkAudio.Decode(bank, r, null, ffmpeg);
            if (!audio.IsSuccess) { decodeFailed++; Console.WriteLine($"DECODE FAIL {file.Path}#{r.Index}: {audio.Error}"); continue; }
            decodedOk++;
            short[] pcm = new short[audio.Value!.Pcm16.Length / 2];
            Buffer.BlockCopy(audio.Value.Pcm16, 0, pcm, 0, audio.Value.Pcm16.Length);
            int ch = audio.Value.Channels;
            if (pcm.Length / ch != r.SampleCount) Console.WriteLine($"LEN {file.Path}#{r.Index}: {pcm.Length / ch} vs {r.SampleCount}");
            if (pcm.Length / ch > 60)
            {
                double typical = 0;
                for (int i = 1; i < 40; i++) typical += Math.Abs(pcm[(i + 40) * ch] - pcm[(i + 39) * ch]);
                typical /= 39;
                jumps.Add(Math.Abs(pcm[47 * ch] - pcm[46 * ch]) - typical);
            }
        }
    }

    Console.WriteLine($"decoded {decodedOk}, failed {decodeFailed}, jump at the join minus typical step: median {(jumps.Count > 0 ? jumps.OrderBy(x => x).ElementAt(jumps.Count / 2) : 0):0.0} max {(jumps.Count > 0 ? jumps.Max() : 0):0.0}");
    return 0;
}

static int RunAbk(string source)
{
    using GameFileIndex index = GameInstallLocator.Open(source).Value!;
    var banks = index.Files.Where(f => f.Path.EndsWith(".abk", StringComparison.Ordinal)).ToList();
    var asts = index.Files.Where(f => f.Path.EndsWith(".ast", StringComparison.Ordinal)).ToList();
    Console.WriteLine($"banks {banks.Count}, streams {asts.Count}");
    Console.WriteLine("folders " + string.Join(" ", banks.GroupBy(b => Path.GetDirectoryName(b.Path)).Select(g => $"{g.Key}:{g.Count()}")));
    var codecs = new Dictionary<string, int>();
    int failed = 0, sounds = 0, dummies = 0;
    foreach (GameFile bank in banks)
    {
        var data = index.ReadDecoded(bank.Winner);
        if (!data.IsSuccess) { failed++; Console.WriteLine($"READFAIL {bank.Path}: {data.Error}"); continue; }
        var parsed = NhlWorkbench.Core.Formats.Abk.AbkContainer.Parse(data.Value!);
        if (!parsed.IsSuccess) { failed++; Console.WriteLine($"PARSEFAIL {bank.Path}: {parsed.Error}"); continue; }
        foreach (var sound in parsed.Value!.Sounds)
        {
            if (sound.IsDummy) { dummies++; continue; }
            sounds++;
            string key = $"codec{sound.CodecId}/{sound.Channels}ch/{sound.SampleRate}";
            codecs[key] = codecs.GetValueOrDefault(key) + 1;
        }
    }

    Console.WriteLine($"parsed sounds {sounds}, dummies {dummies}, failed banks {failed}");
    foreach (var kv in codecs.OrderByDescending(k => k.Value)) { Console.WriteLine("  " + kv.Key + " x" + kv.Value); }
    foreach (GameFile bank in banks.Take(12))
    {
        var data = index.ReadDecoded(bank.Winner).Value!;
        var parsed = NhlWorkbench.Core.Formats.Abk.AbkContainer.Parse(data);
        Console.WriteLine($"{bank.Path} {data.Length} {Convert.ToHexString(data.AsSpan(0, 64))} sounds={(parsed.IsSuccess ? parsed.Value!.Sounds.Count : -1)} {(parsed.IsSuccess ? "" : parsed.Error)}");
    }

    Console.WriteLine("asts: " + string.Join(" ", asts.Take(10).Select(a => a.Path + ":" + a.Winner.StoredSize)));
    return 0;
}

static int RunTextureMap(string source)
{
    using GameFileIndex game = GameInstallLocator.Open(source).Value!;
    var groups = new SortedDictionary<string, (int Files, int NoTextures, int Several, int Textures, int Largest, string Sample)>();
    var names = new List<string>();
    var clock = Stopwatch.StartNew();
    foreach (GameFile file in game.Files.Where(f => f.Path.EndsWith(".rpsgl", StringComparison.Ordinal)))
    {
        string[] parts = file.Path.Split('/');
        string key = string.Join('/', parts.Take(Math.Min(2, parts.Length - 1)));
        Result<byte[]> data = game.ReadDecoded(file.Winner);
        Result<RpsglContainer> parsed = data.IsSuccess ? RpsglReader.Parse(data.Value!, file.Path) : Result.Failure<RpsglContainer>("unreadable");
        int count = parsed.IsSuccess ? parsed.Value!.Textures.Count : 0;
        int largest = parsed.IsSuccess && count > 0 ? parsed.Value!.Textures.Max(t => Math.Max(t.Width, t.Height)) : 0;
        (int Files, int NoTextures, int Several, int Textures, int Largest, string Sample) g =
            groups.TryGetValue(key, out var existing) ? existing : (0, 0, 0, 0, 0, Path.GetFileName(file.Path));
        groups[key] = (g.Files + 1, g.NoTextures + (count == 0 ? 1 : 0), g.Several + (count > 1 ? 1 : 0), g.Textures + count, Math.Max(g.Largest, largest), g.Sample);
        if (count > 1 && names.Count < 12)
        {
            names.Add($"{file.Path}: {string.Join(" | ", parsed.Value!.Textures.Take(5).Select(t => $"{t.Name} {t.Width}x{t.Height}"))}");
        }
    }

    Console.WriteLine($"Read in {clock.Elapsed.TotalSeconds:N0}s");
    Console.WriteLine($"{"folder",-36} files  none  many textures  max  e.g.");
    foreach ((string key, var g) in groups)
    {
        Console.WriteLine($"{key,-36} {g.Files,5} {g.NoTextures,5} {g.Several,5} {g.Textures,8} {g.Largest,4}  {g.Sample}");
    }

    names.ForEach(n => Console.WriteLine("  " + n));

    Console.WriteLine("Inside .big packs:");
    foreach (var group in game.Files.Where(f => f.Path.EndsWith(".big", StringComparison.Ordinal))
        .GroupBy(f => string.Join('/', f.Path.Split('/').Take(4))).OrderByDescending(g => g.Count()).Take(14))
    {
        GameFile first = group.First();
        Result<byte[]> data = game.ReadDecoded(first.Winner);
        string magic = data.IsSuccess ? Convert.ToHexString(data.Value!.AsSpan(0, Math.Min(4, data.Value!.Length))) : "unreadable";
        int rw4 = 0;
        if (data.IsSuccess)
        {
            byte[] marker = [0x89, (byte)'R', (byte)'W', (byte)'4'];
            for (int at = data.Value!.AsSpan().IndexOf(marker), from = 0; at >= 0; from += at + 4, at = data.Value!.AsSpan(from).IndexOf(marker))
            {
                rw4++;
            }
        }

        Console.WriteLine($"  {group.Key,-44} {group.Count(),6}  {first.Path}  {(data.IsSuccess ? data.Value!.Length : 0)} bytes  magic {magic}  RW4 markers {rw4}");
    }

    return 0;
}

static int RunDds(string source, string query, string? saveTo)
{
    using GameFileIndex game = GameInstallLocator.Open(source).Value!;
    var codec = new NhlWorkbench.Core.Dds.DdsCodec();
    IEnumerable<GameFile> files = query == "all" ? game.Files : game.Search(query, 100000);
    int rpsglFiles = 0, textures = 0, identical = 0, different = 0, noTextures = 0, saved = 0;
    var failures = new Dictionary<string, int>();
    var formats = new Dictionary<string, int>();
    var examples = new List<string>();

    void Fail(string reason, string path)
    {
        failures[reason] = failures.GetValueOrDefault(reason) + 1;
        if (examples.Count < 15)
        {
            examples.Add($"{path}: {reason}");
        }
    }

    foreach (GameFile file in files.Where(f => f.Path.EndsWith(".rpsgl", StringComparison.Ordinal)))
    {
        Result<byte[]> data = game.ReadDecoded(file.Winner);
        if (!data.IsSuccess || !RpsglReader.IsRpsgl(data.Value!))
        {
            Fail("not readable", file.Path);
            continue;
        }

        rpsglFiles++;
        Result<IReadOnlyList<NhlWorkbench.Core.Mods.ExportedDds>> all = NhlWorkbench.Core.Mods.TextureDds.ExportAll(data.Value!, file.Path, codec);
        if (!all.IsSuccess)
        {
            if (all.Error!.Contains("no textures", StringComparison.Ordinal))
            {
                noTextures++;
            }
            else
            {
                Fail("export: " + all.Error![(all.Error!.IndexOf(": ", StringComparison.Ordinal) + 2)..], file.Path);
            }

            continue;
        }

        textures += all.Value!.Count;
        foreach (NhlWorkbench.Core.Mods.ExportedDds dds in all.Value!)
        {
            formats[dds.Format.ToString()] = formats.GetValueOrDefault(dds.Format.ToString()) + 1;
            if (saveTo is not null && saved < 12)
            {
                Directory.CreateDirectory(saveTo);
                File.WriteAllBytes(Path.Combine(saveTo, dds.FileName(Path.GetFileNameWithoutExtension(file.Path), all.Value!.Count == 1)), dds.Bytes);
                saved++;
            }
        }

        Result<NhlWorkbench.Core.Mods.ExportedDds> main = NhlWorkbench.Core.Mods.TextureDds.ExportMain(data.Value!, file.Path, codec);
        Result<byte[]> back = NhlWorkbench.Core.Mods.TextureDds.Import(data.Value!, file.Path, main.Value!.Bytes, codec);
        if (!back.IsSuccess)
        {
            Fail("import: " + back.Error, file.Path);
        }
        else if (back.Value!.AsSpan().SequenceEqual(data.Value!))
        {
            identical++;
        }
        else
        {
            different++;
            Fail("round trip changed bytes" + (back.Warnings.Count > 0 ? " (with warning)" : string.Empty), file.Path);
        }
    }

    Console.WriteLine($"{rpsglFiles} RPSGL files, {textures} textures, {noTextures} with no textures. Round trip: {identical} identical, {different} different.");
    Console.WriteLine("Formats: " + string.Join(", ", formats.OrderByDescending(f => f.Value).Select(f => $"{f.Key} {f.Value}")));
    foreach ((string reason, int count) in failures.OrderByDescending(f => f.Value))
    {
        Console.WriteLine($"{count,6}  {reason}");
    }

    examples.ForEach(e => Console.WriteLine("  " + e));
    return 0;
}

static int RunTextures(string source, string query)
{
    using GameFileIndex game = GameInstallLocator.Open(source).Value!;
    var codec = new NhlWorkbench.Core.Dds.DdsCodec();
    IReadOnlyList<GameFile> found = game.Search(query, 5000);
    var shapes = new Dictionary<string, int>();
    int failed = 0;
    foreach (GameFile file in found)
    {
        Result<byte[]> data = game.ReadDecoded(file.Winner);
        Result<RpsglContainer> rpsgl = data.IsSuccess && RpsglReader.IsRpsgl(data.Value!) ? RpsglReader.Parse(data.Value!, file.Path) : Result.Failure<RpsglContainer>("not RPSGL");
        if (!rpsgl.IsSuccess)
        {
            failed++;
            continue;
        }

        string shape = string.Join(" + ", rpsgl.Value!.Textures.Select(t =>
        {
            var pixels = codec.Decode(t.LinearPixelData, t.Width, t.Height, t.Format).Value?.Pixels ?? [];
            int transparent = 0, partial = 0;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] == 0) { transparent++; } else if (pixels[i] < 255) { partial++; }
            }

            string alpha = transparent > 0 && partial > 0 ? "soft-alpha" : transparent > 0 ? "cutout" : partial > 0 ? "partial" : "opaque";
            return $"{t.Width}x{t.Height} {t.Format} mips={t.MipCount} {alpha}";
        }));
        shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
    }

    Console.WriteLine($"{found.Count} files, {failed} not readable as RPSGL. Names: {string.Join(", ", found.Take(6).Select(f => Path.GetFileName(f.Path)))}");
    foreach ((string shape, int count) in shapes.OrderByDescending(s => s.Value))
    {
        Console.WriteLine($"{count,5}  {shape}");
    }

    return 0;
}

static int RunModCheck(string discPath, string gamePath, string work, string modName)
{
    Result<GameFileIndex> built = GameInstallLocator.Open(discPath);
    if (!built.IsSuccess)
    {
        Console.WriteLine(built.Error);
        return 1;
    }

    using GameFileIndex game = built.Value!;
    GameFile? file = game.Find(gamePath);
    if (file is null)
    {
        Console.WriteLine($"{gamePath} isn't in the game.");
        return 1;
    }

    Result<byte[]> original = game.ReadDecoded(file.Winner);
    if (!original.IsSuccess)
    {
        Console.WriteLine(original.Error);
        return 1;
    }

    // Changes the last byte only: enough to tell the copies apart. Not meant to be played.
    byte[] edited = (byte[])original.Value!.Clone();
    edited[^1] ^= 0xFF;
    Console.WriteLine($"{file.Path}: {edited.Length:N0} bytes, from {file.Winner.ArchivePath ?? "loose"}");

    Result<ModProject> created = ModProject.Create(Path.Combine(work, "project"), modName);
    if (!created.IsSuccess)
    {
        Console.WriteLine(created.Error);
        return 1;
    }

    ModProject project = created.Value!;
    Result<ProjectFile> added = project.SetFile(file.Path, edited, game);
    if (!added.IsSuccess)
    {
        Console.WriteLine(added.Error);
        return 1;
    }

    Console.WriteLine($"Base: {added.Value!.Base?.Layer}, {added.Value.Base?.ArchivePath}, {added.Value.Base?.Sha256}");
    foreach (ModIssue issue in ModValidator.Check(project, game))
    {
        Console.WriteLine($"  {issue.Severity} {issue.Code}: {issue.Message}");
    }

    string folder = Path.Combine(work, "exports", modName);
    string zip = Path.Combine(work, "exports", modName + ".zip");
    foreach (Result<ModExportResult> export in new[] { ModExporter.ExportFolder(project, folder, game), ModExporter.ExportZip(project, zip, game) })
    {
        Console.WriteLine(export.IsSuccess ? $"Exported {export.Value!.Path}" : export.Error);
        if (!export.IsSuccess)
        {
            return 1;
        }
    }

    Console.WriteLine($"Zip size: {new FileInfo(zip).Length:N0} bytes");

    // Same priority the recomp uses: this mod above the disc.
    Result<Iso9660Image> disc = Iso9660Image.Open(discPath);
    Result<GameFileIndex> withMod = GameFileIndex.Build(
    [
        new GameLayer(GameLayerKind.Mod, modName, new DirectoryGameFileSource(Path.Combine(folder, "USRDIR"), modName)),
        new GameLayer(GameLayerKind.Disc, "Disc", new IsoGameFileSource(disc.Value!, "Disc")),
    ]);
    if (!withMod.IsSuccess)
    {
        Console.WriteLine(withMod.Error);
        return 1;
    }

    using GameFileIndex after = withMod.Value!;
    GameFile result = after.Find(file.Path)!;
    bool modWins = result.Winner.Layer.Kind == GameLayerKind.Mod && result.Winner.IsLoose;
    bool modBytesMatch = after.ReadDecoded(result.Winner).Value?.AsSpan().SequenceEqual(edited) == true;
    bool discUnchanged = after.ReadDecoded(result.Copies.First(c => c.Layer.Kind == GameLayerKind.Disc)).Value?.AsSpan().SequenceEqual(original.Value) == true;
    Console.WriteLine($"Mod wins: {modWins}. Mod bytes match: {modBytesMatch}. Disc copy unchanged: {discUnchanged}.");
    Console.WriteLine($"Other paths still served by the disc: {after.Files.Count(f => f.Winner.Layer.Kind == GameLayerKind.Disc):N0} of {after.Files.Count:N0}");
    return modWins && modBytesMatch && discUnchanged ? 0 : 1;
}

static string Magic(byte[] data)
{
    var text = new StringBuilder();
    foreach (byte b in data.AsSpan(0, Math.Min(4, data.Length)))
    {
        text.Append(b is >= 32 and < 127 ? (char)b : '.');
    }

    return text.ToString();
}
