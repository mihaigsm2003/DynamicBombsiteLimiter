using System.Numerics;

namespace DynamicBombsiteLimiter;

public readonly record struct BarrierBox(Vector3 Min, Vector3 Max);

public readonly record struct SegmentHit(Vector3 Point, Vector3 Normal, float Fraction);

public static class BarrierProtection
{
    private const float ParallelEpsilon = 1e-6f;

    public static SegmentHit? Intersect(Vector3 start, Vector3 end, BarrierBox box)
    {
        var delta = end - start;
        if (delta == Vector3.Zero || Contains(box, start))
        {
            return null;
        }

        var entry = 0.0f;
        var exit = 1.0f;
        var normal = Vector3.Zero;
        for (var axis = 0; axis < 3; axis++)
        {
            var origin = start[axis];
            var direction = delta[axis];
            if (MathF.Abs(direction) < ParallelEpsilon)
            {
                if (origin < box.Min[axis] || origin > box.Max[axis])
                {
                    return null;
                }

                continue;
            }

            var first = (box.Min[axis] - origin) / direction;
            var second = (box.Max[axis] - origin) / direction;
            var near = MathF.Min(first, second);
            var far = MathF.Max(first, second);
            if (near > entry)
            {
                entry = near;
                normal = Vector3.Zero;
                normal[axis] = direction > 0 ? -1.0f : 1.0f;
            }

            exit = MathF.Min(exit, far);
            if (entry > exit)
            {
                return null;
            }
        }

        return new SegmentHit(start + delta * entry, normal, entry);
    }

    public static SegmentHit? FindNearestHit(Vector3 start, Vector3 end, IEnumerable<BarrierBox> boxes)
    {
        SegmentHit? nearest = null;
        foreach (var box in boxes)
        {
            var hit = Intersect(start, end, box);
            if (hit is not null && (nearest is null || hit.Value.Fraction < nearest.Value.Fraction))
            {
                nearest = hit;
            }
        }

        return nearest;
    }

    public static Vector3 Reflect(Vector3 velocity, Vector3 normal, float scale) =>
        Vector3.Reflect(velocity, normal) * scale;

    private static bool Contains(BarrierBox box, Vector3 point) =>
        point.X >= box.Min.X && point.X <= box.Max.X &&
        point.Y >= box.Min.Y && point.Y <= box.Max.Y &&
        point.Z >= box.Min.Z && point.Z <= box.Max.Z;
}
