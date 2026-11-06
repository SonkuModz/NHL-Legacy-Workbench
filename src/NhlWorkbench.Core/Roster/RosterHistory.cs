namespace NhlWorkbench.Core.Roster;

/// <summary>
/// Undo and redo for a file held as bytes. Each step keeps only the bytes that changed, so hundreds of small edits to a file
/// of a few megabytes stay small.
/// </summary>
public sealed class RosterHistory
{
    private const int MostSteps = 500;

    // Changes closer than this become one region. The bytes between them cost less than another region would.
    private const int JoinGap = 32;

    private readonly List<Step> _undo = [];
    private readonly List<Step> _redo = [];

    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;

    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>Remembers a change. Nothing is kept if the bytes are the same.</summary>
    public void Record(string description, byte[] before, byte[] after)
    {
        Step? step = Step.Between(description, before, after);
        if (step is null)
        {
            return;
        }

        _undo.Add(step);
        _redo.Clear();
        if (_undo.Count > MostSteps)
        {
            _undo.RemoveAt(0);
        }
    }

    /// <summary>The bytes as they were before the last change, or null if there's nothing to undo.</summary>
    public byte[]? Undo(byte[] current)
    {
        if (_undo.Count == 0)
        {
            return null;
        }

        Step step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(step);
        return step.Back(current);
    }

    /// <summary>The bytes with the last undone change put back, or null if there's nothing to redo.</summary>
    public byte[]? Redo(byte[] current)
    {
        if (_redo.Count == 0)
        {
            return null;
        }

        Step step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(step);
        return step.Forward(current);
    }

    private sealed record Region(int Offset, byte[] Before, byte[] After);

    private sealed record Step(string Description, IReadOnlyList<Region> Regions, byte[]? WholeBefore, byte[]? WholeAfter)
    {
        public static Step? Between(string description, byte[] before, byte[] after)
        {
            if (before.Length != after.Length)
            {
                return new Step(description, [], before, after);
            }

            var regions = new List<Region>();
            int at = 0;
            while (at < before.Length)
            {
                if (before[at] == after[at])
                {
                    at++;
                    continue;
                }

                int start = at;
                int end = at + 1;
                for (int scan = end; scan < before.Length && scan - end <= JoinGap; scan++)
                {
                    if (before[scan] != after[scan])
                    {
                        end = scan + 1;
                    }
                }

                regions.Add(new Region(start, before[start..end], after[start..end]));
                at = end;
            }

            return regions.Count == 0 ? null : new Step(description, regions, null, null);
        }

        public byte[] Back(byte[] current) => WholeBefore ?? Patch(current, r => r.Before);

        public byte[] Forward(byte[] current) => WholeAfter ?? Patch(current, r => r.After);

        private byte[] Patch(byte[] current, Func<Region, byte[]> pick)
        {
            byte[] result = (byte[])current.Clone();
            foreach (Region region in Regions)
            {
                pick(region).CopyTo(result, region.Offset);
            }

            return result;
        }
    }
}
