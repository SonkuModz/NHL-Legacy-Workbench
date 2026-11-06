using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rpsgl;
using NhlWorkbench.Core.Formats.Rx2;

namespace NhlWorkbench.Core.EquipmentPreview;

public enum EquipmentPreviewKind
{
    Gloves,
    Helmet,
}

/// <summary>
/// Everything the 3D preview needs for one equipment type: the model, the zone-mask textures
/// (R/G/B channel of each = up to three paintable zones), and an optional detail overlay.
/// File names and the helmet's UV shift come from Heck's equipment editor config.
/// </summary>
public sealed class EquipmentPreviewAssets
{
    private sealed record Config(string Rx2, string[] ZoneMasks, string? Detail, float UvVOffset);

    private static readonly IReadOnlyDictionary<EquipmentPreviewKind, Config> Configs = new Dictionary<EquipmentPreviewKind, Config>
    {
        [EquipmentPreviewKind.Gloves] = new("feprop_glove_0.rx2", new[] { "feglove_15_0_tm.rpsgl", "feglove_15_1_tm.rpsgl", "feglove_15_2_tm.rpsgl" }, null, 0f),
        [EquipmentPreviewKind.Helmet] = new("helmet_14_2.rx2", new[] { "helmet_0_15_tm.rpsgl" }, "helmet_0_15_0_dm.rpsgl", -0.07f),
    };

    private EquipmentPreviewAssets()
    {
    }

    public required EquipmentPreviewKind Kind { get; init; }

    public required Rx2Mesh Mesh { get; init; }

    /// <summary>One entry per zone-mask file; null where that file was missing or couldn't be decoded.</summary>
    public required IReadOnlyList<Rgba32Image?> ZoneMasks { get; init; }

    public Rgba32Image? Detail { get; init; }

    /// <summary>Empirical V shift the mesh needs against the zone-mask atlas (helmet only; carried over from Heck's editor, not derived from the format).</summary>
    public required float UvVOffset { get; init; }

    public static IReadOnlyList<string> RequiredFiles(EquipmentPreviewKind kind) =>
        new[] { Configs[kind].Rx2 }.Concat(Configs[kind].ZoneMasks).ToArray();

    public static Result<EquipmentPreviewAssets> Load(string folder, EquipmentPreviewKind kind)
    {
        try
        {
            Config config = Configs[kind];
            string rx2Path = Path.Combine(folder, config.Rx2);
            if (!File.Exists(rx2Path))
            {
                return Result.Failure<EquipmentPreviewAssets>($"'{config.Rx2}' wasn't found in '{folder}'.");
            }

            Result<Rx2Model> model = Rx2ModelReader.Parse(File.ReadAllBytes(rx2Path), rx2Path);
            if (!model.IsSuccess)
            {
                return Result.Failure<EquipmentPreviewAssets>(model.Error!);
            }

            Rx2Mesh? mesh = model.Value!.Meshes.OrderByDescending(m => m.Vertices.Length).FirstOrDefault();
            if (mesh is null || mesh.Vertices.Length == 0)
            {
                return Result.Failure<EquipmentPreviewAssets>($"'{config.Rx2}' contains no geometry.");
            }

            var codec = new DdsCodec();
            var masks = config.ZoneMasks.Select(file => LoadFirstTexture(Path.Combine(folder, file), codec)).ToList();
            if (masks.All(m => m is null))
            {
                return Result.Failure<EquipmentPreviewAssets>($"None of the zone-mask textures ({string.Join(", ", config.ZoneMasks)}) could be read from '{folder}'.");
            }

            Rgba32Image? detail = config.Detail is null ? null : LoadFirstTexture(Path.Combine(folder, config.Detail), codec);

            return Result.Success(new EquipmentPreviewAssets
            {
                Kind = kind,
                Mesh = mesh,
                ZoneMasks = masks,
                Detail = detail,
                UvVOffset = config.UvVOffset,
            });
        }
        catch (Exception ex)
        {
            return Result.Failure<EquipmentPreviewAssets>($"Failed to load equipment preview assets: {ex.Message}", ex);
        }
    }

    private static Rgba32Image? LoadFirstTexture(string path, DdsCodec codec)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        Result<RpsglContainer> container = RpsglReader.Parse(File.ReadAllBytes(path), path);
        if (!container.IsSuccess || container.Value!.Textures.Count == 0)
        {
            return null;
        }

        TextureEntry entry = container.Value.Textures[0];
        Result<Rgba32Image> decoded = codec.Decode(entry.LinearPixelData, entry.Width, entry.Height, entry.Format);
        return decoded.IsSuccess ? decoded.Value : null;
    }
}
