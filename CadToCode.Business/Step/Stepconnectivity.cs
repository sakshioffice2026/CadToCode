namespace CadToCode.Step;

internal sealed record ConnectivityReport(int ComponentCount, IReadOnlyList<string> IsolatedNames)
{
    public bool IsConnected => ComponentCount <= 1;
}

internal static class StepConnectivity
{
    public const double DefaultTolerance = 1.0;

    public static ConnectivityReport Analyze(IReadOnlyList<Solid> solids, double tolerance = DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(solids);

        var count = solids.Count;

        if (count <= 1)
        {
            return new ConnectivityReport(count, Array.Empty<string>());
        }

        var parent = new int[count];

        for (var i = 0; i < count; i++)
        {
            parent[i] = i;
        }

        int Find(int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }

            return index;
        }

        var boxes = new (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)[count];

        for (var i = 0; i < count; i++)
        {
            boxes[i] = solids[i].Bounds;
        }

        for (var i = 0; i < count; i++)
        {
            for (var j = i + 1; j < count; j++)
            {
                if (!Overlaps(boxes[i], boxes[j], tolerance))
                {
                    continue;
                }

                var rootI = Find(i);
                var rootJ = Find(j);

                if (rootI != rootJ)
                {
                    parent[rootJ] = rootI;
                }
            }
        }

        var groups = new Dictionary<int, List<int>>();

        for (var i = 0; i < count; i++)
        {
            var root = Find(i);

            if (!groups.TryGetValue(root, out var members))
            {
                members = new List<int>();
                groups[root] = members;
            }

            members.Add(i);
        }

        var largest = groups.Values.OrderByDescending(g => g.Count).First();
        var isolated = new List<string>();

        foreach (var group in groups.Values)
        {
            if (ReferenceEquals(group, largest))
            {
                continue;
            }

            foreach (var index in group)
            {
                isolated.Add(string.IsNullOrWhiteSpace(solids[index].Name) ? "Solid " + (index + 1) : solids[index].Name);
            }
        }

        return new ConnectivityReport(groups.Count, isolated);
    }

    private static bool Overlaps(
        (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) a,
        (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) b,
        double tolerance)
    {
        return a.MinX <= b.MaxX + tolerance && b.MinX <= a.MaxX + tolerance
            && a.MinY <= b.MaxY + tolerance && b.MinY <= a.MaxY + tolerance
            && a.MinZ <= b.MaxZ + tolerance && b.MinZ <= a.MaxZ + tolerance;
    }
}