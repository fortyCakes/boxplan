using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting.Merging;

internal sealed record GroupedBox(BoxShape Box, Vec3 Dims, Aabb Aabb);

internal sealed record BoxGroup(IReadOnlyList<GroupedBox> Members);

internal static class BoxGrouper
{
    // Partition the list into connected groups of boxes whose AABBs share a
    // face area (or interpenetrate). Singletons go into their own group.
    public static IReadOnlyList<BoxGroup> Compute(IReadOnlyList<BoxShape> shapes)
    {
        var grouped = new List<GroupedBox>();
        foreach (var shape in shapes)
        {
            if (shape.Dimensions is not { } dims) continue;
            grouped.Add(new GroupedBox(shape, dims, Aabb.FromBox(shape, dims)));
        }

        var n = grouped.Count;
        var parent = Enumerable.Range(0, n).ToArray();

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                if (grouped[i].Aabb.TouchesOrOverlaps(grouped[j].Aabb))
                    Union(i, j);
            }
        }

        var groups = new Dictionary<int, List<GroupedBox>>();
        for (var i = 0; i < n; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var list))
            {
                list = new List<GroupedBox>();
                groups[root] = list;
            }
            list.Add(grouped[i]);
        }

        return groups.Values.Select(members => new BoxGroup(members)).ToList();
    }
}
