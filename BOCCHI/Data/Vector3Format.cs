using System.Numerics;

namespace BOCCHI.Data;

public static class Vector3Format
{
    public static string ToCoordinateString(this Vector3 v)
    {
        return $"{v.X:F2}, {v.Y:F2}, {v.Z:F2}";
    }
}
