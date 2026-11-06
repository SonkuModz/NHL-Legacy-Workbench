using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rx2;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// There is no standalone runnable reference for this parsing path (it ports the shipped
/// Noesis Python model importer's material pass, but Noesis python plugins require the
/// compiled host, unlike the texture-editor reference tool used elsewhere in this suite) --
/// so these tests verify by internal consistency instead: every texture name a resolved
/// material references must correspond to a real texture <see cref="Rx2Reader"/> already
/// extracts from that same file, and the reader must never throw across the real sample
/// corpus. Confirmed directly against real files: combined mesh+texture containers (stadium/
/// arena, ice surfaces) carry real material data with descriptive names (e.g.
/// "BoardConcreteAmbient_boards_trim_mat2_Clone_188"); plain mesh files (jersey/player/glove/
/// hand) carry none at all -- both are expected, not bugs.
/// </summary>
public class Rx2MaterialReaderTests
{
    [Fact]
    public void ParseBySubmeshName_NeverThrows_OverTheFullRealSampleCorpus()
    {
        if (!SampleFileCatalog.ReferencesAvailable)
        {
            return;
        }

        var files = new List<string>(SampleFileCatalog.AllSampleRx2Files());
        if (SampleFileCatalog.ArenaAssetsAvailable)
        {
            files.AddRange(Directory.EnumerateFiles(SampleFileCatalog.ArenaStadiumRoot, "*.rx2", SearchOption.AllDirectories));
            files.AddRange(Directory.EnumerateFiles(SampleFileCatalog.ArenaIceSurfaceRoot, "*.rx2", SearchOption.AllDirectories));
        }

        if (files.Count == 0)
        {
            return;
        }

        foreach (string path in files)
        {
            byte[] data = File.ReadAllBytes(path);
            var textureResult = Rx2Reader.Parse(data, path);
            var textures = textureResult.IsSuccess ? textureResult.Value!.Textures : Array.Empty<TextureEntry>();
            var warnings = new List<ParseWarning>();

            Exception? thrown = Record.Exception(() => Rx2MaterialReader.ParseBySubmeshName(data, textures, warnings));

            Assert.Null(thrown);
        }
    }

    [Fact]
    public void ParseBySubmeshName_ResolvedTextureReferences_AlwaysMatchARealTextureInTheSameFile()
    {
        if (!SampleFileCatalog.ArenaAssetsAvailable)
        {
            return;
        }

        IReadOnlyList<string> files = Directory.EnumerateFiles(SampleFileCatalog.ArenaStadiumRoot, "*.rx2", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);

        int totalTextureRefs = 0;
        int filesWithMaterials = 0;

        foreach (string path in files)
        {
            byte[] data = File.ReadAllBytes(path);
            var textureResult = Rx2Reader.Parse(data, path);
            Assert.True(textureResult.IsSuccess, $"{path}: {textureResult.Error}");
            var textures = textureResult.Value!.Textures;

            var warnings = new List<ParseWarning>();
            Dictionary<string, Rx2Material> materials = Rx2MaterialReader.ParseBySubmeshName(data, textures, warnings);

            if (materials.Count > 0)
            {
                filesWithMaterials++;
            }

            foreach (Rx2Material material in materials.Values)
            {
                foreach (string? textureName in new[] { material.DiffuseTextureName, material.NormalTextureName, material.SpecularTextureName })
                {
                    if (textureName is null)
                    {
                        continue;
                    }

                    totalTextureRefs++;
                    Assert.Contains(textures, t => t.Name == textureName);
                }
            }
        }

        Assert.True(filesWithMaterials > 0, "No stadium file produced any resolved material -- this test isn't exercising the material path at all.");
        Assert.True(totalTextureRefs > 0, "No material referenced any texture -- this test isn't exercising texture-reference resolution at all.");
    }
}
