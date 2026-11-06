using System.Buffers.Binary;
using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>
/// Resolves per-submesh material names and diffuse/normal/specular texture references for
/// RX2-as-mesh files. Ported directly from the shipped Noesis Python model importer's material
/// pass (References/Noesis/plugins/python/mdl_NHLLegacy_X360_rx2.py, lines 53-159) -- there is no
/// standalone runnable reference for this path (Noesis python plugins require the compiled host),
/// so this is verified by internal consistency instead: every resolved texture index must
/// correspond to a real texture already extracted by <see cref="Rx2Reader"/> for the same file.
/// </summary>
public static class Rx2MaterialReader
{
    private static readonly string[] DiffuseSamplerNamesInPriorityOrder = { "diffuseTexture", "map1", "map2" };

    /// <summary>
    /// Returns a dictionary of submesh name (matching <see cref="Rx2Mesh.Name"/>, i.e. the same
    /// name <see cref="Rx2ModelReader"/> assigns) to its resolved <see cref="Rx2Material"/>.
    /// Submeshes with no resolvable material are simply absent from the result -- callers should
    /// treat a missing entry the same as "no material data available", not an error.
    ///
    /// WARNING: submesh names in this format are generic material-type labels, not unique
    /// per-instance identifiers -- a real stadium container was found with 156 submeshes sharing
    /// only 22 distinct names (e.g. 8 separate "concrete" submeshes, each a different physical
    /// structure with its own correct material in-engine). Keying by name means only the LAST
    /// same-named submesh's material survives in this dictionary; every earlier one gets silently
    /// overwritten, even though the underlying resolution below computes each one correctly. This
    /// was fine for this method's original use (labeling exported OBJ/glTF materials, where a
    /// wrong-but-plausible name for a duplicate went unnoticed) but is visibly wrong once used to
    /// actually bind a texture for rendering -- use <see cref="ParseBySubmeshIndex"/> instead for
    /// anything that needs the real per-instance material.
    /// </summary>
    public static Dictionary<string, Rx2Material> ParseBySubmeshName(byte[] data, IReadOnlyList<TextureEntry> textures, List<ParseWarning> warnings)
    {
        var result = new Dictionary<string, Rx2Material>();

        ForEachRenderable(data, textures, warnings, (_, meshName, material) => result[meshName] = material);

        return result;
    }

    /// <summary>
    /// Returns a dictionary of submesh ORDINAL (0-based, in the same order <see cref="Rx2ModelReader.Parse"/>
    /// builds <see cref="Rx2Model.Meshes"/> -- i.e. matches that list's index 1:1) to its resolved
    /// <see cref="Rx2Material"/>. Unlike <see cref="ParseBySubmeshName"/>, this never collapses
    /// distinct submeshes that happen to share a generic name -- see that method's doc comment
    /// for why that matters. Prefer this whenever the caller has (or can get) each submesh's index.
    /// </summary>
    public static Dictionary<int, Rx2Material> ParseBySubmeshIndex(byte[] data, IReadOnlyList<TextureEntry> textures, List<ParseWarning> warnings)
    {
        var result = new Dictionary<int, Rx2Material>();

        ForEachRenderable(data, textures, warnings, (index, _, material) => result[index] = material);

        return result;
    }

    private static void ForEachRenderable(byte[] data, IReadOnlyList<TextureEntry> textures, List<ParseWarning> warnings, Action<int, string, Rx2Material> onResolved)
    {
        if (!Rx2Reader.IsRx2(data) || data.Length < Rx2HeaderLayout.HeaderSizeOffset + 4)
        {
            return;
        }

        uint fileCount = BigEndianReader.ReadUInt32BEAt(data, Rx2HeaderLayout.FileCountOffset);
        uint fileTableOffset = BigEndianReader.ReadUInt32BEAt(data, Rx2HeaderLayout.FileTableOffset);
        if (fileTableOffset >= data.Length)
        {
            return;
        }

        int entryBase = (int)fileTableOffset;
        long entryCount = Math.Min(fileCount, (data.Length - entryBase) / (long)Rx2HeaderLayout.FileTableEntrySize);
        var entries = new Rx2FileTableEntry[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            entries[i] = Rx2FileTableEntry.ReadAt(data, entryBase + (i * Rx2HeaderLayout.FileTableEntrySize));
        }

        // Pass 1: resolve every name-table entry's string, keyed by its explicit target
        // file-table index (not table position -- see Rx2NameTable.MergeParseByTargetIndex).
        var namesByEntryIndex = new Dictionary<int, string>();
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].TypeTag is Rx2HeaderLayout.TypeTagNameTableA or Rx2HeaderLayout.TypeTagNameTableB)
            {
                Rx2NameTable.MergeParseByTargetIndex(data, entries[i].OffsetField, namesByEntryIndex, warnings);
            }
        }

        // Pass 2: the same file-table-index-scan order Rx2Reader.Parse uses to build its own
        // texture list, so position N here always matches textures[N] from that same parse.
        var textureIndexEntries = new List<int>();
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].TypeTag is Rx2HeaderLayout.TypeTagDescriptor or Rx2HeaderLayout.TypeTagDescriptorCombined)
            {
                textureIndexEntries.Add(i);
            }
        }

        // Pass 3: MODELRENDER_ARENAID -- maps a renderable's own file-table index to a material's
        // file-table index. See the class doc for why this can't be verified against a runnable
        // reference; the seek arithmetic below is a closed-form derivation of the reference
        // importer's stream-cursor walk (confirmed algebraically, not just transcribed).
        var materialIndexByRenderableIndex = new Dictionary<int, int>();
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].TypeTag == Rx2ModelLayout.TypeTagModelRenderArenaId)
            {
                ParseModelRenderArenaId(data, entries[i].OffsetField, materialIndexByRenderableIndex, warnings);
            }
        }

        // Pass 4: FxMaterial entries -- name + sampler-keyed texture references.
        var materialsByEntryIndex = new Dictionary<int, Rx2Material>();
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].TypeTag != Rx2ModelLayout.TypeTagFxMaterial)
            {
                continue;
            }

            Rx2Material? material = TryParseFxMaterial(data, entries, i, namesByEntryIndex, textureIndexEntries, textures, warnings);
            if (material is not null)
            {
                materialsByEntryIndex[i] = material;
            }
        }

        // Pass 5: for each renderable submesh (in the same order Rx2ModelReader.Parse builds
        // Rx2Model.Meshes, so meshOrdinal here always matches that list's index), resolve its
        // material via passes 3+4 and report it via the caller's chosen key (name or ordinal).
        int meshOrdinal = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].TypeTag != Rx2ModelLayout.TypeTagFxRenderableSimple)
            {
                continue;
            }

            string meshName = Rx2ModelReader.ReadSubmeshName(data, entries[i].OffsetField + 0x0C, meshOrdinal);
            int ordinal = meshOrdinal;
            meshOrdinal++;

            if (materialIndexByRenderableIndex.TryGetValue(i, out int materialEntryIndex)
                && materialsByEntryIndex.TryGetValue(materialEntryIndex, out Rx2Material? material))
            {
                onResolved(ordinal, meshName, material);
            }
        }
    }

    /// <summary>
    /// Ported from the reference importer's MODELRENDER_ARENAID walk. That code reads a small
    /// table of table offsets, then within each sub-table a contiguous array of "idxOff" values;
    /// for each, it peeks a sentinel 8 bytes past (entryBase + idxOff) to decide whether the
    /// shaderIndex/materialIndex pair sits at (entryBase + idxOff) or 32 bytes further along --
    /// this is a closed-form version of that same peek-then-branch, with the stream-cursor
    /// arithmetic worked out ahead of time instead of emulated live.
    /// </summary>
    private static void ParseModelRenderArenaId(byte[] data, int entryBase, Dictionary<int, int> materialIndexByRenderableIndex, List<ParseWarning> warnings)
    {
        if (entryBase < 0 || entryBase + 4 > data.Length)
        {
            return;
        }

        int tableCount = BigEndianReader.ReadInt32BEAt(data, entryBase);
        int tabOffBase = entryBase + 4; // where tabOff[0..3] live, immediately after the count
        if (tableCount < 0 || tableCount > 4)
        {
            warnings.Add(new ParseWarning($"MODELRENDER_ARENAID entry at {entryBase}: unexpected table count {tableCount}; skipping.", "Rx2MaterialReader"));
            return;
        }

        for (int k = 0; k < tableCount; k++)
        {
            int tabOffOffset = tabOffBase + (k * 4);
            if (tabOffOffset + 4 > data.Length)
            {
                continue;
            }

            int tabOff = BigEndianReader.ReadInt32BEAt(data, tabOffOffset);
            int tableBase = tabOffBase + 4 + tabOff;
            if (tableBase < 0 || tableBase + 4 > data.Length)
            {
                continue;
            }

            int contentCount = BigEndianReader.ReadInt32BEAt(data, tableBase);
            int idxOffArrayBase = tableBase + 4;

            for (int j = 0; j < contentCount; j++)
            {
                int idxOffSlot = idxOffArrayBase + (j * 4);
                if (idxOffSlot + 4 > data.Length)
                {
                    break;
                }

                int idxOff = BigEndianReader.ReadInt32BEAt(data, idxOffSlot);
                int sentinelPos = entryBase + idxOff + 8;
                if (sentinelPos < 0 || sentinelPos + 4 > data.Length)
                {
                    continue;
                }

                int sentinel = BigEndianReader.ReadInt32BEAt(data, sentinelPos);
                int shaderIndexPos = sentinel == -1 ? entryBase + idxOff : entryBase + idxOff + 32;
                if (shaderIndexPos < 0 || shaderIndexPos + 8 > data.Length)
                {
                    continue;
                }

                int shaderIndex = BigEndianReader.ReadInt32BEAt(data, shaderIndexPos);
                int materialIndex = BigEndianReader.ReadInt32BEAt(data, shaderIndexPos + 4);
                materialIndexByRenderableIndex[shaderIndex] = materialIndex;
            }
        }
    }

    private static Rx2Material? TryParseFxMaterial(
        byte[] data,
        Rx2FileTableEntry[] entries,
        int entryIndex,
        Dictionary<int, string> namesByEntryIndex,
        List<int> textureIndexEntries,
        IReadOnlyList<TextureEntry> textures,
        List<ParseWarning> warnings)
    {
        if (entryIndex < 2)
        {
            warnings.Add(new ParseWarning($"FxMaterial entry {entryIndex}: not enough preceding entries for the sampler-name table; skipping.", "Rx2MaterialReader"));
            return null;
        }

        string materialName = namesByEntryIndex.TryGetValue(entryIndex, out string? name) ? name : $"material_{entryIndex}";

        // Sampler name list lives 2 entries before this one, and (unlike the rest of this
        // big-endian format) its offsets are stored little-endian -- confirmed against the
        // reference importer's explicit "<i" reads at this exact spot.
        int sampleListBase = entries[entryIndex - 2].OffsetField + 8;
        if (sampleListBase < 0 || sampleListBase + 4 > data.Length)
        {
            return new Rx2Material(materialName, null, null, null);
        }

        int sampleCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(sampleListBase, 4));
        if (sampleCount < 0 || sampleCount > 64)
        {
            warnings.Add(new ParseWarning($"FxMaterial entry {entryIndex}: unexpected sampler count {sampleCount}; skipping sampler resolution.", "Rx2MaterialReader"));
            return new Rx2Material(materialName, null, null, null);
        }

        int sampleOffsetArrayBase = sampleListBase + 4 + (sampleCount * 4) + 4;
        var sampleNames = new List<string>(sampleCount);
        for (int j = 0; j < sampleCount; j++)
        {
            int slot = sampleOffsetArrayBase + (j * 4);
            if (slot + 4 > data.Length)
            {
                break;
            }

            int sampleOff = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(slot, 4));
            if (sampleOff < 0 || sampleOff >= data.Length)
            {
                sampleNames.Add(string.Empty);
                continue;
            }

            sampleNames.Add(BigEndianReader.ReadNullTerminatedAsciiAt(data, sampleOff));
        }

        // Sampler-to-texture-index resolution lives 1 entry before this one; unlike the sampler
        // name list above, these offsets are big-endian (also confirmed against the reference).
        int samplerTableBase = entries[entryIndex - 1].OffsetField;

        string? diffuseTextureName = null;
        foreach (string samplerName in DiffuseSamplerNamesInPriorityOrder)
        {
            string? resolved = TryResolveSamplerTexture(data, samplerTableBase, sampleNames, samplerName, textureIndexEntries, textures);
            if (resolved is not null)
            {
                diffuseTextureName = resolved;
            }
        }

        string? normalTextureName = TryResolveSamplerTexture(data, samplerTableBase, sampleNames, "normalMap", textureIndexEntries, textures);
        string? specularTextureName = TryResolveSamplerTexture(data, samplerTableBase, sampleNames, "specularMap", textureIndexEntries, textures);

        return new Rx2Material(materialName, diffuseTextureName, normalTextureName, specularTextureName);
    }

    private static string? TryResolveSamplerTexture(byte[] data, int samplerTableBase, List<string> sampleNames, string samplerName, List<int> textureIndexEntries, IReadOnlyList<TextureEntry> textures)
    {
        int samplerSlot = sampleNames.IndexOf(samplerName);
        if (samplerSlot < 0)
        {
            return null;
        }

        int pointerOffset = samplerTableBase + 0x10 + (samplerSlot * 4);
        if (pointerOffset < 0 || pointerOffset + 4 > data.Length)
        {
            return null;
        }

        int stringOffset = BigEndianReader.ReadInt32BEAt(data, pointerOffset);
        if (stringOffset < 0 || stringOffset + 4 > data.Length)
        {
            return null;
        }

        int textureFileTableIndex = BigEndianReader.ReadInt32BEAt(data, stringOffset);
        int position = textureIndexEntries.IndexOf(textureFileTableIndex);
        return position >= 0 && position < textures.Count ? textures[position].Name : null;
    }
}
