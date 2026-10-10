using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>A polyline in a RectTransform's local space (the route on the map), clipped by a RectMask2D above it.</summary>
public class UILine : MaskableGraphic
{
    public float thickness = 6f;
    public readonly List<Vector2> points = new List<Vector2>();

    public void SetPoints(List<Vector2> pts)
    {
        points.Clear();
        points.AddRange(pts);
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        float h = thickness * 0.5f;
        for (int i = 0; i + 1 < points.Count; i++)
        {
            Vector2 a = points[i], b = points[i + 1];
            Vector2 d = b - a;
            if (d.sqrMagnitude < 1e-4f) continue;
            Vector2 n = new Vector2(-d.y, d.x).normalized * h;
            // each segment runs a half-thickness past its ends so the joints close
            Vector2 e = d.normalized * h;
            int k = vh.currentVertCount;
            vh.AddVert(a - e - n, color, Vector2.zero);
            vh.AddVert(a - e + n, color, Vector2.zero);
            vh.AddVert(b + e + n, color, Vector2.zero);
            vh.AddVert(b + e - n, color, Vector2.zero);
            vh.AddTriangle(k, k + 1, k + 2);
            vh.AddTriangle(k, k + 2, k + 3);
        }
    }
}
