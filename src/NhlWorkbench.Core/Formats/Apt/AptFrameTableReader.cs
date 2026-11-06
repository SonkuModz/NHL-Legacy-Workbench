using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// Reads a Movie or Sprite character's own frame table -- the piece that actually
/// positions characters onto the stage, decoded 2026-08-19 after the character table
/// alone (Shape/EditText/etc's own authored local bounds) turned out not to be enough
/// to reproduce the real in-game layout. Confirmed against real NHL overlay bytes:
///
/// A Movie or Sprite record's raw+16 is a frame count (u32) and raw+20 is a byte
/// offset to that character's own frame table (matches the external NCAA14 research's
/// documented "Sprite character timelines use movie-style fields: frame count at
/// character offset +16, frame table pointer at +20" -- References/aptresearch/APT_DISCOVERIES.md).
/// The frame table itself is a u32 header count followed by that many (itemArrayOffset,
/// flag) u32 pairs, one pair per frame. Each frame's itemArrayOffset points to a run of
/// u32 offsets (terminated by a zero or out-of-range entry) into individual 64-byte <see
/// cref="AptPlaceObjectRecord"/>s -- confirmed by resolving every referenced characterId
/// (via <see cref="AptMovie.ResolveCharacterTagIndex"/>) against real Shape/Sprite/EditText
/// tags and finding sensible, spread-across-the-stage translate values (not the
/// clustered-at-origin values the character table's own bounds gave).
///
/// Sprites do not own a separate character table -- their frame items reference the
/// SAME movie-wide character list their own definition lives in (confirmed by the
/// external research and cross-checked directly: a Sprite's placed characterIds
/// resolve into the same tag sequence a Movie's do).
///
/// Only frame 0 is read. Later frames are typically incremental adds/removes/moves on
/// top of frame 0 (real data shows this: a 2-frame movie's frame 1 array is exactly
/// frame 0's array with the first entry dropped) -- for a single static preview, frame
/// 0 is the most complete starting point.
/// </summary>
public static class AptFrameTableReader
{
    /// <summary>
    /// Reads the given character's frame-0 placements. Returns empty if the character has no
    /// frame table (frame count 0) or the table doesn't resolve to real data. Two kinds of real
    /// records are deliberately excluded from the result, both confirmed 2026-08-19 against real
    /// flags values: <see cref="AptPlaceObjectRecord.MoveCharacterId"/> ("Move" placements that
    /// re-transform something already placed, carrying no new character) and <see
    /// cref="AptPlaceObjectRecord.IsClipMask"/> (objects whose own flags mark them as a clip mask
    /// rather than visible content -- their authored bounds are sized to cover what they clip,
    /// which produced a giant, wrongly-visible near-full-stage rectangle in the preview before
    /// this was found and excluded).
    /// </summary>
    public static IReadOnlyList<AptPlaceObjectRecord> ReadFrameZeroPlacements(byte[] movieData, int characterTagOffset)
    {
        const int frameCountFieldOffset = 16;
        const int frameTablePointerFieldOffset = 20;

        if (characterTagOffset + frameTablePointerFieldOffset + 4 > movieData.Length)
        {
            return Array.Empty<AptPlaceObjectRecord>();
        }

        uint frameCount = BigEndianReader.ReadUInt32BEAt(movieData, characterTagOffset + frameCountFieldOffset);
        uint frameTablePointer = BigEndianReader.ReadUInt32BEAt(movieData, characterTagOffset + frameTablePointerFieldOffset);
        if (frameCount == 0 || frameTablePointer + 8 > movieData.Length)
        {
            return Array.Empty<AptPlaceObjectRecord>();
        }

        uint headerCount = BigEndianReader.ReadUInt32BEAt(movieData, (int)frameTablePointer);
        if (headerCount == 0 || frameTablePointer + 4 + 8 > movieData.Length)
        {
            return Array.Empty<AptPlaceObjectRecord>();
        }

        uint frameZeroItemArrayOffset = BigEndianReader.ReadUInt32BEAt(movieData, (int)frameTablePointer + 4);
        if (frameZeroItemArrayOffset >= movieData.Length)
        {
            return Array.Empty<AptPlaceObjectRecord>();
        }

        var placements = new List<AptPlaceObjectRecord>();
        int p = (int)frameZeroItemArrayOffset;
        const int maxItems = 4096; // generous real-world cap against a corrupt/garbage array running away
        for (int i = 0; i < maxItems && p + 4 <= movieData.Length; i++, p += 4)
        {
            uint itemOffset = BigEndianReader.ReadUInt32BEAt(movieData, p);
            if (itemOffset == 0 || itemOffset >= movieData.Length)
            {
                break;
            }

            AptPlaceObjectRecord? record = AptPlaceObjectRecord.TryParse(movieData, (int)itemOffset);
            if (record is null)
            {
                // Not a recognizable PlaceObject/PlaceObject3 head -- real data interleaves other
                // frame-item kinds here too (Action=1, FrameLabel=2, RemoveObject=4). The item
                // array is a flat list of 4-byte pointers regardless of what each one points to,
                // so skip just this one slot and keep walking rather than stopping the whole scan.
                continue;
            }

            if (record.CharacterId != AptPlaceObjectRecord.MoveCharacterId && !record.IsClipMask)
            {
                placements.Add(record);
            }
        }

        return placements;
    }
}
