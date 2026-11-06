using System.Buffers.Binary;
using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// Repositions a movie's root-level placements in place -- the first write-side use of the
/// placement/transform layer decoded earlier this session (previously read-only). Shifting only
/// the root Movie's own DIRECT placements (not recursing into Sprites) is enough to move
/// everything: a Sprite's own children are positioned relative to that Sprite's own placement,
/// so moving the Sprite moves its whole subtree with it -- exactly the same composition rule
/// <see cref="AptSceneComposer"/> reads by, just applied in the write direction.
///
/// Patches every frame's item array, not just frame 0: real data shows later frames' arrays
/// often share the same underlying 64-byte records as frame 0 (a 2-frame movie's frame-1 array
/// was observed to be exactly frame 0's array with the first entry dropped -- see
/// <see cref="AptFrameTableReader"/>'s remarks), so a record can appear in more than one frame's
/// array. Each real record is patched exactly once even if reachable from multiple frames.
///
/// Deliberately does NOT skip "Move" (characterId 0xFFFFFFFF) or clip-mask records the way the
/// read-side composer does for a static preview: every one of these still carries a real
/// translateX/Y that represents a real on-screen position (an animation keyframe, or a mask's own
/// position), and leaving any of them unshifted would leave that content behind when the rest of
/// the movie moves.
/// </summary>
public static class AptPlacementEditor
{
    private const int TranslateXOffset = 32;
    private const int TranslateYOffset = 36;
    private const int NamePtrOffset = 52;
    private const uint RemoveObjectTag = 4;

    /// <summary>
    /// Instance names (resolved via a placement's own <c>namePtr</c>) whose real on-screen `_x` is
    /// confirmed, by directly disassembling this file's own ActionScript bytecode (2026-08-22 --
    /// see `project_scorebug_editor_investigation.md`, "MAJOR DISCOVERY" section), to be set by
    /// `gAnimator.Animate(target, "_x", VALUE, ...)` at runtime -- meaning a static translateX/Y
    /// edit to these is silently overwritten on load and accomplishes nothing. Excluded here for
    /// two reasons: (1) there is no point writing bytes that have zero visual effect, and (2)
    /// including their baked (and visually meaningless) position in <see
    /// cref="ComputeRootPlacementBounds"/>'s min/max would skew the anchor-snap target away from
    /// where the elements that DO respond (see below) actually are.
    ///
    /// Confirmed by decoding every position-related function found in the movie's root controller
    /// class (`Init`, `InitPanels`, `LoadPanels`, `InitTeamLogos`, `AnimateTeamLogos`,
    /// `PositionPanels`, `RepositionPanels`) and checking which instance names appear as an
    /// Animate `_x` target in any of them.
    ///
    /// This is a real architectural fact about the file, not a single-element guess the way
    /// earlier exclusions in this class's history were -- see git history / memory for the earlier
    /// mcAd/cid=149 single-element theories this superseded.
    /// </summary>
    private static readonly HashSet<string> ScriptControlledInstanceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "mcClockText", "mcQuarterText", "mcAwayText", "mcHomeText", "mcAwayScore", "mcHomeScore",
        "mcTeamBackgroundAway", "mcTeamBackgroundHome", "mcPowerplayHighlightAway", "mcPowerplayHighlightHome",
        "mcPeriodBackground", "mcLogoSeparator", "mcGlossTween", "mcSeparatorMain",
    };

    /// <summary>
    /// "mcAd" (characterId 59) -- kept excluded separately from <see
    /// cref="ScriptControlledInstanceNames"/> because its status is different: no decoded function
    /// touches its `_x` either (so by the same logic it belongs in the static-only tier that
    /// SHOULD move), but two real, controlled live tests (2026-08-20/21, one variable changed each
    /// time) empirically confirmed that FREEZING it -- not shifting it -- produces the better
    /// visual result. This may mean its true position is governed by something outside this
    /// file's own bytecode (an ad-rotation system, possibly in the shared `globalclasses` base
    /// class), or that it's genuinely static but authored to sit at a fixed offset from the bar
    /// rather than move with it. Kept frozen on the empirical result, not the (incomplete)
    /// theoretical one.
    /// </summary>
    private static readonly HashSet<string> EmpiricallyFrozenInstanceNames = new(StringComparer.OrdinalIgnoreCase) { "mcAd" };

    /// <summary>
    /// Shifts root-level placements' translateX/Y by (deltaX, deltaY), mutating <paramref
    /// name="movieData"/> in place. Returns the number of distinct placement records patched (0
    /// if the movie has no root Movie tag, no frame table, or nothing to patch).
    ///
    /// <paramref name="containersOnly"/> (added 2026-08-20 after a real in-game test): when true,
    /// only placements whose characterId resolves to a Sprite or Movie are shifted -- "Move"
    /// records (characterId 0xFFFFFFFF) and ALL direct Shape/EditText root placements are left
    /// untouched, regardless of their own translate value.
    ///
    /// History: a real saved+reloaded file with EVERY root placement shifted uniformly (the
    /// original, simpler behavior) showed visible distortion in the real game even though the
    /// byte edit itself was confirmed surgically clean -- a direct investigation found roughly 60
    /// of ~150 root placements are direct Shape/EditText characters, almost all sitting at LOCAL
    /// (0,0), which is not consistent with a simple "translate = final screen position" model and
    /// strongly suggests they serve a role (mask, hit-area, template) this workbench doesn't
    /// fully understand -- shifting them broke that role. Restricting the edit to Sprite/Movie
    /// containers only was a real improvement in a live test (a coherent, readable bar) though
    /// content still spilled slightly past the right edge of the bar's own background frame.
    ///
    /// A refinement was tried (2026-08-20, same day) that additionally shifted the small number of
    /// direct Shape/EditText placements sitting at a real NONZERO position (e.g. a Shape at
    /// ~(470, 0), suspected to be the bar's right-edge border cap) on the theory that they were
    /// real visible frame pieces left behind by the blanket exclusion. A live test showed this
    /// made the result WORSE (a larger corrupted/hatched region on the right), so it was reverted
    /// -- whatever that nonzero Shape actually is, moving it independently of its sibling/parent
    /// broke something else (likely mask or relative-transform alignment), not fixed the overflow.
    /// Back to the plain Sprite/Movie-only rule as the best-supported-by-testing state. The right-
    /// edge overflow this rule still shows is unexplained and needs a different theory, not
    /// another blanket nonzero/zero split.
    /// </summary>
    public static int RepositionRootPlacements(byte[] movieData, AptMovie movie, float deltaX, float deltaY, bool containersOnly = false, bool finalDisplayListOnly = false)
    {
        AptTagRecord? movieTag = movie.Tags.FirstOrDefault(t => t.Type == AptCharacterType.Movie);
        if (movieTag is null)
        {
            return 0;
        }

        const int frameCountOffset = 16;
        const int frameTablePointerOffset = 20;
        int characterTagOffset = movieTag.Offset;
        if (characterTagOffset + frameTablePointerOffset + 4 > movieData.Length)
        {
            return 0;
        }

        uint frameCount = BigEndianReader.ReadUInt32BEAt(movieData, characterTagOffset + frameCountOffset);
        uint frameTablePointer = BigEndianReader.ReadUInt32BEAt(movieData, characterTagOffset + frameTablePointerOffset);
        if (frameCount == 0 || frameTablePointer + 4 > movieData.Length)
        {
            return 0;
        }

        HashSet<int>? finalOffsets = finalDisplayListOnly
            ? new HashSet<int>(ComputeFinalDisplayListByOffset(movieData, characterTagOffset)
                .Where(kv => RespondsToStaticEdit(movieData, movie, kv.Key))
                .Select(kv => kv.Key))
            : null;

        var patchedOffsets = new HashSet<int>();
        for (int frame = 0; frame < frameCount; frame++)
        {
            int pairOffset = (int)frameTablePointer + 4 + (frame * 8);
            if (pairOffset + 4 > movieData.Length)
            {
                break;
            }

            uint itemArrayOffset = BigEndianReader.ReadUInt32BEAt(movieData, pairOffset);
            if (itemArrayOffset == 0 || itemArrayOffset >= movieData.Length)
            {
                continue;
            }

            const int maxItemsPerFrame = 4096; // generous real-world cap against a corrupt/garbage array running away
            int p = (int)itemArrayOffset;
            for (int i = 0; i < maxItemsPerFrame && p + 4 <= movieData.Length; i++, p += 4)
            {
                uint itemOffset = BigEndianReader.ReadUInt32BEAt(movieData, p);
                if (itemOffset == 0 || itemOffset >= movieData.Length)
                {
                    break;
                }

                if (!patchedOffsets.Add((int)itemOffset))
                {
                    continue; // already patched via an earlier frame's array -- same underlying record
                }

                AptPlaceObjectRecord? record = AptPlaceObjectRecord.TryParse(movieData, (int)itemOffset);
                if (record is null)
                {
                    patchedOffsets.Remove((int)itemOffset); // not actually a placement record here -- don't count it
                    continue;
                }

                if (finalDisplayListOnly && !finalOffsets!.Contains((int)itemOffset))
                {
                    patchedOffsets.Remove((int)itemOffset); // superseded/removed before frame end -- not really on screen, see finalDisplayListOnly's remarks
                    continue;
                }

                if (containersOnly)
                {
                    int childIndex = record.CharacterId == AptPlaceObjectRecord.MoveCharacterId ? -1 : movie.ResolveCharacterTagIndex(record.CharacterId);
                    bool isContainer = childIndex >= 0 && movie.Tags[childIndex].Type is AptCharacterType.Sprite or AptCharacterType.Movie;
                    if (!isContainer)
                    {
                        patchedOffsets.Remove((int)itemOffset); // deliberately left unshifted -- see containersOnly's remarks
                        continue;
                    }
                }

                float newX = record.TranslateX + deltaX;
                float newY = record.TranslateY + deltaY;
                BinaryPrimitives.WriteSingleBigEndian(movieData.AsSpan((int)itemOffset + TranslateXOffset, 4), newX);
                BinaryPrimitives.WriteSingleBigEndian(movieData.AsSpan((int)itemOffset + TranslateYOffset, 4), newY);
            }
        }

        return patchedOffsets.Count;
    }

    /// <summary>
    /// Estimates the on-stage extent of a movie's root-level content, for computing an anchor
    /// delta (<see cref="ComputeAnchorDelta"/>). Deliberately an approximation, not a real visual
    /// bounding box: it spans the raw translateX/Y of every real (non-Move, non-mask) root-level
    /// placement -- the same set <see cref="AptFrameTableReader.ReadFrameZeroPlacements"/> already
    /// curates for the read-side composer -- without accounting for each character's own width or
    /// height, or any placement scale. A true visual bounding box would need full geometry
    /// decoding per shape (only rectangles are decoded, see <see cref="AptShapeGeometry"/>), which
    /// is unnecessary precision for "put the bug in a corner." Returns null if there's no root
    /// Movie tag or no real placements to measure.
    ///
    /// <paramref name="containersOnly"/> should match whatever value is passed to <see
    /// cref="RepositionRootPlacements"/> for the same edit: if only Sprite/Movie placements are
    /// actually going to move, the bounds used to compute the target delta should be measured
    /// from that same subset -- otherwise the ~60 direct Shape/EditText placements clustered at
    /// local (0,0) (which won't move) would pull the computed center/edges toward a point that
    /// isn't representative of what's actually being repositioned.
    /// </summary>
    public static (float MinX, float MinY, float MaxX, float MaxY)? ComputeRootPlacementBounds(byte[] movieData, AptMovie movie, bool containersOnly = false, bool finalDisplayListOnly = false)
    {
        AptTagRecord? movieTag = movie.Tags.FirstOrDefault(t => t.Type == AptCharacterType.Movie);
        if (movieTag is null)
        {
            return null;
        }

        IEnumerable<AptPlaceObjectRecord> placements;
        if (finalDisplayListOnly)
        {
            placements = ComputeFinalDisplayListByOffset(movieData, movieTag.Offset)
                .Where(kv => RespondsToStaticEdit(movieData, movie, kv.Key))
                .Select(kv => kv.Value);
        }
        else
        {
            placements = AptFrameTableReader.ReadFrameZeroPlacements(movieData, movieTag.Offset);
            if (containersOnly)
            {
                placements = placements.Where(p =>
                {
                    int idx = movie.ResolveCharacterTagIndex(p.CharacterId);
                    return idx >= 0 && movie.Tags[idx].Type is AptCharacterType.Sprite or AptCharacterType.Movie;
                });
            }
        }

        var list = placements.ToList();
        if (list.Count == 0)
        {
            return null;
        }

        float minX = list.Min(p => p.TranslateX);
        float minY = list.Min(p => p.TranslateY);
        float maxX = list.Max(p => p.TranslateX);
        float maxY = list.Max(p => p.TranslateY);
        return (minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Resolves a placement's own instance name (the string its <c>namePtr</c> field points at,
    /// only meaningful when <see cref="AptPlaceObjectRecord.FlagHasName"/> is set), by looking
    /// that offset up in the movie's own string table. Returns "" (never null) when there's no
    /// name -- so callers can safely check it against a name set without a null check.
    /// </summary>
    /// <summary>
    /// True if a placement is expected to visibly respond to a static translateX/Y edit -- false
    /// for the confirmed script-controlled tier (<see cref="ScriptControlledInstanceNames"/>) and
    /// the empirically-frozen tier (<see cref="EmpiricallyFrozenInstanceNames"/>). See both
    /// fields' remarks for the evidence behind each.
    /// </summary>
    private static bool RespondsToStaticEdit(byte[] movieData, AptMovie movie, int placementOffset)
    {
        return IsKnownStaticResponsive(ResolveInstanceName(movieData, movie, placementOffset));
    }

    /// <summary>
    /// Public for reuse by <see cref="AptPositionedPreviewBuilder"/> -- lets a preview show, per
    /// element, whether a Move/anchor edit will actually affect it. Returns true for anything not
    /// on the confirmed-frozen lists (the default assumption for a name this workbench hasn't
    /// specifically checked, since most real chrome/logo elements ARE static-responsive -- see
    /// <see cref="ScriptControlledInstanceNames"/>'s remarks for how that list was built).
    /// </summary>
    public static bool IsKnownStaticResponsive(string instanceName)
    {
        return !ScriptControlledInstanceNames.Contains(instanceName) && !EmpiricallyFrozenInstanceNames.Contains(instanceName);
    }

    public static string ResolveInstanceName(byte[] movieData, AptMovie movie, int placementOffset)
    {
        if (placementOffset + NamePtrOffset + 4 > movieData.Length)
        {
            return string.Empty;
        }

        uint namePtr = BigEndianReader.ReadUInt32BEAt(movieData, placementOffset + NamePtrOffset);
        foreach ((int offset, string value) in movie.AllStrings)
        {
            if (offset == namePtr)
            {
                return value;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Replays frame 0's item array in order -- PlaceObject/PlaceObject3 (new or replacing
    /// content at a depth), "Move" (re-transforms whatever's currently at a depth), and
    /// RemoveObject (tag 4, <c>[tag][depth]</c>, clears a depth) -- onto a depth-to-record map,
    /// the way a real player actually builds a frame's display list. Returns only the WINNING
    /// record for each depth, keyed by the byte offset that determines its final on-screen
    /// translateX/Y (the original PlaceObject's offset, unless a later Move at the same depth
    /// took over -- in which case the Move's own offset is the one whose translate actually
    /// matters, and is what gets returned/patched).
    ///
    /// Added 2026-08-20 after discovering (via `DepthReplay`-style scratch tooling on a real
    /// overlay) that the naive "every non-Move, non-clip-mask record in the array is real content"
    /// reading every previous version of this editor relied on (matching <see
    /// cref="AptFrameTableReader.ReadFrameZeroPlacements"/>'s own model) is WRONG for this file:
    /// depth 1 alone is placed 67 times in one frame's array, with only 3 explicit RemoveObject
    /// calls anywhere -- meaning the overwhelming majority of same-depth placements are the
    /// "place a new character at an already-occupied depth without an intervening RemoveObject"
    /// pattern the external APT community research explicitly documents as legal, real content
    /// silently superseding earlier content at that depth. Confirmed concretely: naive reading
    /// found ~153 "real" root placements (with the ~60-near-origin oddity that drove the
    /// containers-only/nonzero-split theories); replaying properly resolves to only 48 TRUE final
    /// placements, only 1 of which sits at exact local (0,0) -- and one specific record
    /// (characterId 58) that both prior theories treated as important "background chrome" turns
    /// out to have been superseded entirely (never actually on screen), while its apparent tile
    /// partner (characterId 59) is the one real survivor. This is a materially different, better-
    /// supported picture of "what's actually visible" than any previous theory used, though
    /// (like every theory before it) it is still unconfirmed against a real in-game screenshot.
    /// </summary>
    /// <summary>
    /// Public for reuse by <see cref="AptPositionedPreviewBuilder"/> -- the same true-final-display
    /// resolution this class uses for repositioning is also the best available source of "what's
    /// really on screen" for a positioned preview, and re-deriving it separately would risk drift.
    /// </summary>
    public static Dictionary<int, AptPlaceObjectRecord> ComputeFinalDisplayListByOffset(byte[] movieData, int characterTagOffset)
    {
        var result = new Dictionary<int, AptPlaceObjectRecord>();
        const int frameTablePointerOffset = 20;
        if (characterTagOffset + frameTablePointerOffset + 4 > movieData.Length)
        {
            return result;
        }

        uint frameTablePointer = BigEndianReader.ReadUInt32BEAt(movieData, characterTagOffset + frameTablePointerOffset);
        if (frameTablePointer + 8 > movieData.Length)
        {
            return result;
        }

        uint itemArrayOffset = BigEndianReader.ReadUInt32BEAt(movieData, (int)frameTablePointer + 4);
        if (itemArrayOffset == 0 || itemArrayOffset >= movieData.Length)
        {
            return result;
        }

        var depthToOffset = new Dictionary<uint, int>();
        const int maxItems = 4096; // generous real-world cap against a corrupt/garbage array running away
        int p = (int)itemArrayOffset;
        for (int i = 0; i < maxItems && p + 4 <= movieData.Length; i++, p += 4)
        {
            uint itemOffset = BigEndianReader.ReadUInt32BEAt(movieData, p);
            if (itemOffset == 0 || itemOffset >= movieData.Length)
            {
                break;
            }

            uint rawTag = (int)itemOffset + 4 <= movieData.Length ? BigEndianReader.ReadUInt32BEAt(movieData, (int)itemOffset) : 0;
            if (rawTag == RemoveObjectTag)
            {
                if ((int)itemOffset + 8 <= movieData.Length)
                {
                    uint removeDepth = BigEndianReader.ReadUInt32BEAt(movieData, (int)itemOffset + 4);
                    if (depthToOffset.TryGetValue(removeDepth, out int removedOffset))
                    {
                        result.Remove(removedOffset);
                        depthToOffset.Remove(removeDepth);
                    }
                }

                continue;
            }

            AptPlaceObjectRecord? record = AptPlaceObjectRecord.TryParse(movieData, (int)itemOffset);
            if (record is null)
            {
                continue; // Action/FrameLabel/other frame-item kind -- doesn't affect the depth map
            }

            if (record.CharacterId == AptPlaceObjectRecord.MoveCharacterId)
            {
                if (depthToOffset.TryGetValue(record.Depth, out int existingOffset) && result.TryGetValue(existingOffset, out AptPlaceObjectRecord? existingRecord))
                {
                    result.Remove(existingOffset);
                    result[(int)itemOffset] = existingRecord with { TranslateX = record.TranslateX, TranslateY = record.TranslateY };
                    depthToOffset[record.Depth] = (int)itemOffset;
                }

                continue;
            }

            if (depthToOffset.TryGetValue(record.Depth, out int oldOffset))
            {
                result.Remove(oldOffset); // this new placement supersedes whatever was here before
            }

            result[(int)itemOffset] = record;
            depthToOffset[record.Depth] = (int)itemOffset;
        }

        return result;
    }

    /// <summary>
    /// The (deltaX, deltaY) that would move <paramref name="bounds"/> (from <see
    /// cref="ComputeRootPlacementBounds"/>) so it sits at <paramref name="anchor"/> on a
    /// <paramref name="stageWidth"/> x <paramref name="stageHeight"/> stage -- edges flush with
    /// the stage edge, center aligned to the stage center on that axis.
    /// </summary>
    public static (float DeltaX, float DeltaY) ComputeAnchorDelta(
        (float MinX, float MinY, float MaxX, float MaxY) bounds,
        float stageWidth,
        float stageHeight,
        AptAnchorPosition anchor)
    {
        float centerX = (bounds.MinX + bounds.MaxX) / 2f;
        float centerY = (bounds.MinY + bounds.MaxY) / 2f;

        float deltaX = anchor switch
        {
            AptAnchorPosition.TopLeft or AptAnchorPosition.MiddleLeft or AptAnchorPosition.BottomLeft => -bounds.MinX,
            AptAnchorPosition.TopCenter or AptAnchorPosition.Center or AptAnchorPosition.BottomCenter => (stageWidth / 2f) - centerX,
            AptAnchorPosition.TopRight or AptAnchorPosition.MiddleRight or AptAnchorPosition.BottomRight => stageWidth - bounds.MaxX,
            _ => 0f,
        };

        float deltaY = anchor switch
        {
            AptAnchorPosition.TopLeft or AptAnchorPosition.TopCenter or AptAnchorPosition.TopRight => -bounds.MinY,
            AptAnchorPosition.MiddleLeft or AptAnchorPosition.Center or AptAnchorPosition.MiddleRight => (stageHeight / 2f) - centerY,
            AptAnchorPosition.BottomLeft or AptAnchorPosition.BottomCenter or AptAnchorPosition.BottomRight => stageHeight - bounds.MaxY,
            _ => 0f,
        };

        return (deltaX, deltaY);
    }
}
