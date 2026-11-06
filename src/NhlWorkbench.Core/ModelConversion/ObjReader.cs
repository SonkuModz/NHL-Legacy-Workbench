using System.Globalization;
using System.Numerics;
using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.ModelConversion;

/// <summary>
/// Reads a Wavefront OBJ back into per-object geometry. OBJ decouples position/UV indices (the
/// same vertex position can carry different UVs on different faces), so this splits on the
/// (position-index, uv-index) pair the same way any indexed-mesh OBJ importer does -- each
/// unique pair becomes one local vertex. Faces with more than 3 vertices are fan-triangulated.
/// Tolerant of files hand-edited in Blender/Maya/3ds Max: missing UV components, negative
/// (relative) indices, and n-gon faces are all handled; anything else unrecognized is ignored
/// rather than treated as an error, since this only needs geometry ObjWriter itself emits, plus
/// whatever an external tool re-saves around it (materials, comments, unsupported directives).
/// </summary>
public static class ObjReader
{
    public static Result<IReadOnlyList<ImportedSubmesh>> Read(string objPath)
    {
        try
        {
            var globalPositions = new List<Vector3>();
            var globalUvs = new List<Vector2>();
            var submeshes = new List<ImportedSubmesh>();

            string? currentName = null;
            var localPositions = new List<Vector3>();
            var localUvs = new List<Vector2>();
            var localTriangles = new List<int>();
            var vertexKeyToLocalIndex = new Dictionary<(int PosIndex, int UvIndex), int>();

            void FlushCurrent()
            {
                if (currentName is not null && localPositions.Count > 0)
                {
                    submeshes.Add(new ImportedSubmesh(currentName, localPositions.ToArray(), localUvs.ToArray(), localTriangles.ToArray()));
                }

                localPositions = new List<Vector3>();
                localUvs = new List<Vector2>();
                localTriangles = new List<int>();
                vertexKeyToLocalIndex = new Dictionary<(int, int), int>();
            }

            foreach (string rawLine in File.ReadLines(objPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    continue;
                }

                switch (parts[0])
                {
                    case "o":
                    case "g":
                        FlushCurrent();
                        currentName = parts.Length > 1 ? string.Join(' ', parts, 1, parts.Length - 1) : "unnamed";
                        break;

                    case "v" when parts.Length >= 4:
                        globalPositions.Add(new Vector3(
                            ParseFloat(parts[1]),
                            ParseFloat(parts[2]),
                            ParseFloat(parts[3])));
                        break;

                    case "vt" when parts.Length >= 2:
                        float u = ParseFloat(parts[1]);
                        float v = parts.Length >= 3 ? ParseFloat(parts[2]) : 0f;
                        globalUvs.Add(new Vector2(u, 1f - v)); // undo ObjWriter's bottom-left-origin flip
                        break;

                    case "f" when parts.Length >= 4:
                        currentName ??= "unnamed";
                        AppendFace(parts, globalPositions, globalUvs, localPositions, localUvs, localTriangles, vertexKeyToLocalIndex);
                        break;
                }
            }

            FlushCurrent();

            if (submeshes.Count == 0)
            {
                return Result.Failure<IReadOnlyList<ImportedSubmesh>>("No usable geometry (\"o\"/\"g\" groups with faces) found in the OBJ file.");
            }

            return Result.Success<IReadOnlyList<ImportedSubmesh>>(submeshes);
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<ImportedSubmesh>>($"Failed to parse OBJ file: {ex.Message}", ex);
        }
    }

    private static void AppendFace(
        string[] parts,
        List<Vector3> globalPositions,
        List<Vector2> globalUvs,
        List<Vector3> localPositions,
        List<Vector2> localUvs,
        List<int> localTriangles,
        Dictionary<(int PosIndex, int UvIndex), int> vertexKeyToLocalIndex)
    {
        var faceLocalIndices = new List<int>(parts.Length - 1);

        for (int i = 1; i < parts.Length; i++)
        {
            string[] components = parts[i].Split('/');
            int posIndex = ResolveIndex(components[0], globalPositions.Count);
            if (posIndex < 0)
            {
                return; // malformed face vertex -- drop the whole face rather than guess
            }

            int uvIndex = -1;
            if (components.Length > 1 && components[1].Length > 0)
            {
                uvIndex = ResolveIndex(components[1], globalUvs.Count);
            }

            var key = (posIndex, uvIndex);
            if (!vertexKeyToLocalIndex.TryGetValue(key, out int localIndex))
            {
                localIndex = localPositions.Count;
                localPositions.Add(posIndex < globalPositions.Count ? globalPositions[posIndex] : Vector3.Zero);
                localUvs.Add(uvIndex >= 0 && uvIndex < globalUvs.Count ? globalUvs[uvIndex] : Vector2.Zero);
                vertexKeyToLocalIndex[key] = localIndex;
            }

            faceLocalIndices.Add(localIndex);
        }

        // Fan-triangulate n-gons (n == 3 is the common case and produces exactly one triangle).
        for (int i = 1; i + 1 < faceLocalIndices.Count; i++)
        {
            localTriangles.Add(faceLocalIndices[0]);
            localTriangles.Add(faceLocalIndices[i]);
            localTriangles.Add(faceLocalIndices[i + 1]);
        }
    }

    /// <summary>OBJ indices are 1-based, with negative values meaning "relative to the current end of the list".</summary>
    private static int ResolveIndex(string token, int currentCount)
    {
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int raw))
        {
            return -1;
        }

        return raw > 0 ? raw - 1 : currentCount + raw;
    }

    private static float ParseFloat(string token) => float.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
}
