using System.Globalization;
using System.Text;

namespace BoxPlanLib.Svg;

public sealed class MarchingSquaresVectorizer : IRasterVectorizer
{
    // For each of the 16 marching-squares cases (bit3=TL, bit2=TR, bit1=BR, bit0=BL),
    // pairs of active edges (0=top, 1=right, 2=bottom, 3=left) to connect.
    // Two pairs → two segments (saddle cases 5 and 10).
    private static readonly int[][] Cases =
    [
        [],               // 0  – all light
        [3, 2],           // 1  – BL
        [2, 1],           // 2  – BR
        [3, 1],           // 3  – BL+BR
        [1, 0],           // 4  – TR
        [3, 0, 2, 1],     // 5  – TR+BL saddle: L-T and B-R
        [0, 2],           // 6  – TR+BR
        [3, 0],           // 7  – TR+BR+BL
        [0, 3],           // 8  – TL
        [0, 2],           // 9  – TL+BL
        [0, 1, 3, 2],     // 10 – TL+BR saddle: T-R and L-B
        [0, 1],           // 11 – TL+BL+BR
        [1, 3],           // 12 – TL+TR
        [2, 1],           // 13 – TL+TR+BL
        [3, 2],           // 14 – TL+TR+BR
        [],               // 15 – all dark
    ];

    public string Vectorize(bool[,] dark, int width, int height)
    {
        // Edge midpoints are represented in half-integer coordinates (multiply actual coords by 2)
        // to keep dictionary keys as exact integers.
        // Edge midpoints for cell (cx, cy):
        //   Top    = (2*cx+1, 2*cy)
        //   Right  = (2*cx+2, 2*cy+1)
        //   Bottom = (2*cx+1, 2*cy+2)
        //   Left   = (2*cx,   2*cy+1)
        var adj = new Dictionary<(int x, int y), List<(int x, int y)>>();

        // Iterate cells from (-1,-1) so out-of-bounds pixels (treated as light) pad all edges.
        for (var cy = -1; cy < height; cy++)
        {
            for (var cx = -1; cx < width; cx++)
            {
                var tl = Get(dark, width, height, cx,     cy);
                var tr = Get(dark, width, height, cx + 1, cy);
                var br = Get(dark, width, height, cx + 1, cy + 1);
                var bl = Get(dark, width, height, cx,     cy + 1);

                var caseIndex = (tl ? 8 : 0) | (tr ? 4 : 0) | (br ? 2 : 0) | (bl ? 1 : 0);
                var edges = Cases[caseIndex];

                for (var i = 0; i < edges.Length; i += 2)
                {
                    var p1 = EdgeMidpoint(cx, cy, edges[i]);
                    var p2 = EdgeMidpoint(cx, cy, edges[i + 1]);
                    AddUndirectedEdge(adj, p1, p2);
                }
            }
        }

        if (adj.Count == 0)
            return string.Empty;

        return BuildPaths(adj);
    }

    private static (int x, int y) EdgeMidpoint(int cx, int cy, int edge) => edge switch
    {
        0 => (2 * cx + 1, 2 * cy),         // top
        1 => (2 * cx + 2, 2 * cy + 1),     // right
        2 => (2 * cx + 1, 2 * cy + 2),     // bottom
        _ => (2 * cx,     2 * cy + 1),     // left (3)
    };

    private static void AddUndirectedEdge(
        Dictionary<(int, int), List<(int, int)>> adj,
        (int x, int y) a, (int x, int y) b)
    {
        if (!adj.TryGetValue(a, out var la)) adj[a] = la = [];
        la.Add(b);
        if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = [];
        lb.Add(a);
    }

    private static string BuildPaths(Dictionary<(int x, int y), List<(int x, int y)>> adj)
    {
        var visited = new HashSet<(int, int)>();
        var dSb = new StringBuilder();
        var hasAny = false;

        foreach (var startKey in adj.Keys)
        {
            if (!visited.Add(startKey))
                continue;

            var neighbors = adj[startKey];
            if (neighbors.Count == 0)
                continue;

            // Follow the closed loop starting from startKey.
            var chain = new List<(int x, int y)> { startKey };
            var prev = startKey;
            var curr = neighbors[0];

            while (curr != startKey)
            {
                chain.Add(curr);
                visited.Add(curr);

                var next = FindNext(adj[curr], prev);
                prev = curr;
                curr = next;
            }

            if (chain.Count < 3)
                continue;

            AppendSubpath(dSb, chain);
            hasAny = true;
        }

        if (!hasAny)
            return string.Empty;

        // All contours share one <path> so that nested contours act as holes
        // under the evenodd fill rule.
        return "<path fill-rule=\"evenodd\" d=\"" + dSb + "\"/>";
    }

    private static (int x, int y) FindNext(List<(int x, int y)> neighbors, (int x, int y) prev)
    {
        // Each contour node has exactly 2 neighbors; return the one that isn't prev.
        foreach (var n in neighbors)
            if (n != prev)
                return n;
        return neighbors[0]; // degenerate: return any (handles isolated points safely)
    }

    private static void AppendSubpath(StringBuilder sb, List<(int x, int y)> chain)
    {
        sb.Append("M ");
        sb.Append(Fmt(chain[0].x));
        sb.Append(' ');
        sb.Append(Fmt(chain[0].y));

        for (var i = 1; i < chain.Count; i++)
        {
            sb.Append(" L ");
            sb.Append(Fmt(chain[i].x));
            sb.Append(' ');
            sb.Append(Fmt(chain[i].y));
        }

        sb.Append(" Z ");
    }

    // Converts a half-integer coordinate (e.g. 3 → "1.5", 4 → "2") to its SVG string.
    private static string Fmt(int halfInt) =>
        (halfInt / 2.0).ToString("0.#", CultureInfo.InvariantCulture);

    private static bool Get(bool[,] dark, int w, int h, int x, int y) =>
        x >= 0 && y >= 0 && x < w && y < h && dark[y, x];
}
