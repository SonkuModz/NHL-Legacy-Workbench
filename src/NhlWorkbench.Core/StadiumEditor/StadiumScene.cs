using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rx2;

namespace NhlWorkbench.Core.StadiumEditor;

/// <summary>
/// Composes a stadium .rx2 file's mesh geometry, materials, and textures into one ready-to-render
/// scene. Pure composition of three already-proven Core readers -- no new byte-level parsing:
/// <see cref="Rx2ModelReader"/> for geometry, <see cref="Rx2Reader"/> for the textures embedded in
/// the same combined mesh+texture container, and <see cref="Rx2MaterialReader"/> for the
/// submesh-name -> material -> texture-name resolution that was previously only used to label
/// exported OBJ/glTF materials, never to actually decode and bind a texture for live rendering.
/// </summary>
public sealed class StadiumScene
{
    private StadiumScene(string sourcePath, IReadOnlyList<StadiumSubmesh> submeshes, IReadOnlyList<ParseWarning> warnings)
    {
        SourcePath = sourcePath;
        Submeshes = submeshes;
        Warnings = warnings;
    }

    public string SourcePath { get; }

    public IReadOnlyList<StadiumSubmesh> Submeshes { get; }

    public IReadOnlyList<ParseWarning> Warnings { get; }

    public static Result<StadiumScene> Load(byte[] data, string sourcePath)
    {
        try
        {
            Result<Rx2Model> modelResult = Rx2ModelReader.Parse(data, sourcePath);
            if (!modelResult.IsSuccess)
            {
                return Result.Failure<StadiumScene>(modelResult.Error!);
            }

            var warnings = new List<ParseWarning>(modelResult.Warnings);

            // Textures are embedded in the same file for combined stadium/arena containers. A
            // failure here just means "no textures found" (e.g. a mesh-only file was opened by
            // mistake) -- geometry is still worth showing, so this doesn't fail the whole load.
            Result<Rx2Container> textureResult = Rx2Reader.Parse(data, sourcePath);
            IReadOnlyList<TextureEntry> textures = textureResult.IsSuccess ? textureResult.Value!.Textures : Array.Empty<TextureEntry>();
            if (!textureResult.IsSuccess)
            {
                warnings.Add(new ParseWarning($"No textures could be read from this file: {textureResult.Error}"));
            }

            // Keyed by submesh ORDINAL, not name -- submesh names in this format are generic
            // material-type labels reused across many geometrically-distinct instances (a real
            // stadium file has 156 submeshes sharing only 22 distinct names), so the name-keyed
            // ParseBySubmeshName silently collapses same-named submeshes onto whichever one's
            // material was resolved last. See Rx2MaterialReader.ParseBySubmeshName's doc comment.
            Dictionary<int, Rx2Material> materialsByIndex = Rx2MaterialReader.ParseBySubmeshIndex(data, textures, warnings);
            Dictionary<string, TextureEntry> texturesByName = new(StringComparer.OrdinalIgnoreCase);
            foreach (TextureEntry texture in textures)
            {
                texturesByName.TryAdd(texture.Name, texture);
            }

            var ddsCodec = new DdsCodec();
            var submeshes = new List<StadiumSubmesh>(modelResult.Value!.Meshes.Count);

            for (int i = 0; i < modelResult.Value.Meshes.Count; i++)
            {
                Rx2Mesh mesh = modelResult.Value.Meshes[i];
                materialsByIndex.TryGetValue(i, out Rx2Material? material);

                TextureEntry? diffuseTexture = FindNamedTexture(material?.DiffuseTextureName, texturesByName);
                TextureEntry? normalTexture = FindNamedTexture(material?.NormalTextureName, texturesByName);
                TextureEntry? specularTexture = FindNamedTexture(material?.SpecularTextureName, texturesByName);

                submeshes.Add(new StadiumSubmesh
                {
                    Index = i,
                    Mesh = mesh,
                    Material = material,
                    DiffuseTexture = diffuseTexture,
                    NormalTexture = normalTexture,
                    SpecularTexture = specularTexture,
                    DiffuseMap = DecodeTexture(diffuseTexture, ddsCodec, warnings),
                    NormalMap = DecodeTexture(normalTexture, ddsCodec, warnings),
                    SpecularMap = DecodeTexture(specularTexture, ddsCodec, warnings),
                });
            }

            return Result.Success(new StadiumScene(sourcePath, submeshes, warnings), warnings);
        }
        catch (Exception ex)
        {
            return Result.Failure<StadiumScene>($"Failed to load stadium scene: {ex.Message}", ex);
        }
    }

    private static TextureEntry? FindNamedTexture(string? textureName, IReadOnlyDictionary<string, TextureEntry> texturesByName) =>
        textureName is not null && texturesByName.TryGetValue(textureName, out TextureEntry? entry) ? entry : null;

    private static Rgba32Image? DecodeTexture(TextureEntry? entry, DdsCodec ddsCodec, List<ParseWarning> warnings)
    {
        if (entry is null)
        {
            return null;
        }

        Result<Rgba32Image> decoded = ddsCodec.Decode(entry.LinearPixelData, entry.Width, entry.Height, entry.Format);
        if (!decoded.IsSuccess)
        {
            warnings.Add(new ParseWarning($"Could not decode texture '{entry.Name}': {decoded.Error}"));
            return null;
        }

        return decoded.Value;
    }
}
