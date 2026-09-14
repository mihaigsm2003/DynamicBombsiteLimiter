using System.Drawing;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace DynamicBombsiteLimiter;

public sealed class Barrier
{
    private const float PlayerHalfWidth = 16.0f;
    private const float PlayerHeight = 72.0f;

    public string Name { get; set; } = string.Empty;
    public VectorData Min { get; set; } = new();
    public VectorData Max { get; set; } = new();

    [JsonIgnore]
    public List<CBeam> Beams { get; } = [];

    public bool IntersectsPlayer(Vector origin) =>
        origin.X + PlayerHalfWidth >= Min.X && origin.X - PlayerHalfWidth <= Max.X &&
        origin.Y + PlayerHalfWidth >= Min.Y && origin.Y - PlayerHalfWidth <= Max.Y &&
        origin.Z + PlayerHeight >= Min.Z && origin.Z <= Max.Z;

    public static Barrier Create(string name, Vector first, Vector second) => new()
    {
        Name = name,
        Min = new VectorData(MathF.Min(first.X, second.X), MathF.Min(first.Y, second.Y), MathF.Min(first.Z, second.Z)),
        Max = new VectorData(MathF.Max(first.X, second.X), MathF.Max(first.Y, second.Y), MathF.Max(first.Z, second.Z))
    };

    public void Draw()
    {
        ClearDraw();

        var points = new[]
        {
            new Vector(Min.X, Min.Y, Min.Z), new Vector(Max.X, Min.Y, Min.Z),
            new Vector(Min.X, Max.Y, Min.Z), new Vector(Max.X, Max.Y, Min.Z),
            new Vector(Min.X, Min.Y, Max.Z), new Vector(Max.X, Min.Y, Max.Z),
            new Vector(Min.X, Max.Y, Max.Z), new Vector(Max.X, Max.Y, Max.Z)
        };

        DrawBeam(points[0], points[1]);
        DrawBeam(points[0], points[2]);
        DrawBeam(points[3], points[1]);
        DrawBeam(points[3], points[2]);
        DrawBeam(points[4], points[5]);
        DrawBeam(points[4], points[6]);
        DrawBeam(points[7], points[5]);
        DrawBeam(points[7], points[6]);

        for (var index = 0; index < 4; index++)
        {
            DrawBeam(points[index], points[index + 4]);
        }
    }

    public void ClearDraw()
    {
        foreach (var beam in Beams)
        {
            if (beam.IsValid)
            {
                beam.AcceptInput("Kill");
            }
        }

        Beams.Clear();
    }

    private void DrawBeam(Vector start, Vector end)
    {
        var beam = Utilities.CreateEntityByName<CBeam>("beam");
        if (beam is null)
        {
            return;
        }

        beam.Render = Color.Lime;
        beam.Width = 1.0f;
        beam.Teleport(start);
        beam.EndPos.X = end.X;
        beam.EndPos.Y = end.Y;
        beam.EndPos.Z = end.Z;
        beam.DispatchSpawn();
        Beams.Add(beam);
    }
}

public sealed class VectorData
{
    public VectorData()
    {
    }

    public VectorData(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}