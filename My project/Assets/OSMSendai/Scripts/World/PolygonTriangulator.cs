using System;
using System.Collections.Generic;
using UnityEngine;

namespace OsmSendai.World
{
    internal static class PolygonTriangulator
    {
        // Simple ear-clipping triangulation for a single simple polygon (no holes).
        // Returns triangle indices into the input vertex array.
        public static bool TryTriangulate(Vector2[] vertices, out int[] triangles)
        {
            triangles = Array.Empty<int>();
            if (vertices == null || vertices.Length < 3) return false;

            var n = vertices.Length;
            var indices = new List<int>(n);
            for (var i = 0; i < n; i++) indices.Add(i);

            // Ensure clockwise winding for stable "inside" tests.
            if (SignedArea(vertices) > 0f)
            {
                indices.Reverse();
            }

            var result = new List<int>((n - 2) * 3);
            var guard = 0;
            while (indices.Count > 3 && guard++ < 10000)
            {
                var earFound = false;
                for (var i = 0; i < indices.Count; i++)
                {
                    var i0 = indices[(i + indices.Count - 1) % indices.Count];
                    var i1 = indices[i];
                    var i2 = indices[(i + 1) % indices.Count];

                    if (!IsConvex(vertices[i0], vertices[i1], vertices[i2])) continue;
                    if (ContainsAnyPoint(vertices, indices, i0, i1, i2)) continue;

                    result.Add(i0);
                    result.Add(i1);
                    result.Add(i2);
                    indices.RemoveAt(i);
                    earFound = true;
                    break;
                }

                if (!earFound)
                {
                    // Likely self-intersection or degenerate polygon.
                    return false;
                }
            }

            if (indices.Count == 3)
            {
                result.Add(indices[0]);
                result.Add(indices[1]);
                result.Add(indices[2]);
            }

            triangles = result.ToArray();
            return triangles.Length >= 3;
        }

        private static float SignedArea(Vector2[] v)
        {
            var a = 0f;
            for (var i = 0; i < v.Length; i++)
            {
                var p = v[i];
                var q = v[(i + 1) % v.Length];
                a += p.x * q.y - q.x * p.y;
            }
            return a * 0.5f;
        }

        private static bool IsConvex(Vector2 a, Vector2 b, Vector2 c)
        {
            // For clockwise polygons, convex if cross <= 0.
            var ab = b - a;
            var bc = c - b;
            var cross = ab.x * bc.y - ab.y * bc.x;
            return cross <= 0f;
        }

        private static bool ContainsAnyPoint(Vector2[] verts, List<int> poly, int i0, int i1, int i2)
        {
            var a = verts[i0];
            var b = verts[i1];
            var c = verts[i2];
            for (var i = 0; i < poly.Count; i++)
            {
                var idx = poly[i];
                if (idx == i0 || idx == i1 || idx == i2) continue;
                if (PointInTriangle(verts[idx], a, b, c)) return true;
            }
            return false;
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            // Barycentric (works regardless of winding).
            var v0 = c - a;
            var v1 = b - a;
            var v2 = p - a;

            var dot00 = Vector2.Dot(v0, v0);
            var dot01 = Vector2.Dot(v0, v1);
            var dot02 = Vector2.Dot(v0, v2);
            var dot11 = Vector2.Dot(v1, v1);
            var dot12 = Vector2.Dot(v1, v2);

            var denom = dot00 * dot11 - dot01 * dot01;
            if (Mathf.Abs(denom) < 1e-8f) return false;
            var inv = 1f / denom;
            var u = (dot11 * dot02 - dot01 * dot12) * inv;
            var v = (dot00 * dot12 - dot01 * dot02) * inv;
            return (u >= 0f) && (v >= 0f) && (u + v <= 1f);
        }
    }
}

