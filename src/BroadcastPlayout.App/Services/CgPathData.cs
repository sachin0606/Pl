using System.Globalization;

namespace BroadcastPlayout.Services;

/// <summary>Portable normalized point data used by native pen shapes and freeform masks.</summary>
public static class CgPathData
{
    public readonly record struct Point(double X, double Y);

    public static IReadOnlyList<Point> Parse(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return [];
        var points = new List<Point>();
        foreach (var token in data.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = token.Split(',', StringSplitOptions.TrimEntries);
            if (pair.Length != 2 ||
                !double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) continue;
            points.Add(new Point(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1)));
        }
        return points;
    }

    public static string EncodeNormalized(IEnumerable<Point> points) => string.Join(";",
        points.Select(p => $"{Math.Clamp(p.X, 0, 1).ToString("0.######", CultureInfo.InvariantCulture)},{Math.Clamp(p.Y, 0, 1).ToString("0.######", CultureInfo.InvariantCulture)}"));

    public static bool Contains(IReadOnlyList<Point> polygon, double x, double y)
    {
        if (polygon.Count < 3) return false;
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var j = i == 0 ? polygon.Count - 1 : i - 1;
            var a = polygon[i]; var b = polygon[j];
            if ((a.Y > y) != (b.Y > y) &&
                x < (b.X - a.X) * (y - a.Y) / Math.Max(1e-12, b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    public static double DistanceToEdges(IReadOnlyList<Point> polygon, double x, double y)
    {
        if (polygon.Count < 2) return double.MaxValue;
        var best = double.MaxValue;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var length2 = dx * dx + dy * dy;
            var t = length2 <= 1e-12 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / length2, 0, 1);
            var px = a.X + t * dx; var py = a.Y + t * dy;
            best = Math.Min(best, Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py)));
        }
        return best;
    }
}
