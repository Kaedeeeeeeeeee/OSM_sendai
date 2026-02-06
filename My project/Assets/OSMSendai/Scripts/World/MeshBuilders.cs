using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace OsmSendai.World
{
    internal sealed class MeshBuilder
    {
        private readonly List<Vector3> _vertices = new List<Vector3>(4096);
        private readonly List<Vector3> _normals = new List<Vector3>(4096);
        private readonly List<Vector2> _uvs = new List<Vector2>(4096);
        private readonly List<int> _triangles = new List<int>(8192);

        public int VertexCount => _vertices.Count;

        public void Clear()
        {
            _vertices.Clear();
            _normals.Clear();
            _uvs.Clear();
            _triangles.Clear();
        }

        public void AddBox(Vector3 center, Vector3 size)
        {
            var hx = size.x * 0.5f;
            var hy = size.y * 0.5f;
            var hz = size.z * 0.5f;

            var min = center - new Vector3(hx, hy, hz);
            var max = center + new Vector3(hx, hy, hz);

            // 6 faces, 4 verts each (flat normals)
            AddQuad(
                new Vector3(min.x, min.y, max.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(max.x, max.y, max.z),
                new Vector3(min.x, max.y, max.z),
                Vector3.forward);
            AddQuad(
                new Vector3(max.x, min.y, min.z),
                new Vector3(min.x, min.y, min.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(max.x, max.y, min.z),
                Vector3.back);
            AddQuad(
                new Vector3(min.x, min.y, min.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(min.x, max.y, min.z),
                Vector3.left);
            AddQuad(
                new Vector3(max.x, min.y, max.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(max.x, max.y, max.z),
                Vector3.right);
            AddQuad(
                new Vector3(min.x, max.y, max.z),
                new Vector3(max.x, max.y, max.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(min.x, max.y, min.z),
                Vector3.up);
            AddQuad(
                new Vector3(min.x, min.y, min.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(min.x, min.y, max.z),
                Vector3.down);
        }

        public void AddRibbon(IReadOnlyList<Vector3> points, float width)
        {
            if (points == null || points.Count < 2) return;

            var half = width * 0.5f;
            for (var i = 0; i < points.Count - 1; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                var dir = b - a;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.0001f) continue;
                dir.Normalize();
                var right = new Vector3(-dir.z, 0f, dir.x);

                var v0 = a - right * half;
                var v1 = a + right * half;
                var v2 = b + right * half;
                var v3 = b - right * half;
                AddQuad(v0, v1, v2, v3, Vector3.up);
            }
        }

        public void AddPrism(Vector2[] footprintXZ, float baseY, float heightY)
        {
            if (footprintXZ == null || footprintXZ.Length < 3) return;

            // Ensure clockwise winding (in XZ) so the top face is front-facing when viewed from above.
            var pts = EnsureClockwise(footprintXZ);
            var n = pts.Length;

            var baseVerts = new Vector3[n];
            var topVerts = new Vector3[n];
            for (var i = 0; i < n; i++)
            {
                var p = pts[i];
                baseVerts[i] = new Vector3(p.x, baseY, p.y);
                topVerts[i] = new Vector3(p.x, baseY + heightY, p.y);
            }

            // Top face (up)
            AddFaceFan(topVerts, Vector3.up, clockwise: true);
            // Bottom face (down) so you can look from below without holes.
            AddFaceFan(baseVerts, Vector3.down, clockwise: false);

            // Side faces
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                var a = baseVerts[i];
                var b = baseVerts[j];
                var edge = b - a;
                edge.y = 0f;
                // For clockwise footprints, edge x up points outward.
                var outward = Vector3.Cross(edge, Vector3.up).normalized;
                if (outward.sqrMagnitude < 1e-8f) outward = Vector3.forward;

                // Use vertex order that makes front face point outward
                AddQuad(a, topVerts[i], topVerts[j], b, outward);
            }
        }

        public bool TryAddExtrudedPolygon(Vector2[] footprintXZ, float baseY, float heightY)
        {
            if (footprintXZ == null || footprintXZ.Length < 3) return false;

            // Remove duplicate closure if present.
            var pts2 = CleanPolygon(StripDuplicateClosure(footprintXZ));
            if (pts2.Length < 3) return false;

            // Ensure clockwise winding for consistent outward normals on side faces
            pts2 = EnsureClockwise(pts2);

            if (!PolygonTriangulator.TryTriangulate(pts2, out var tris)) return false;

            var baseIndexTop = _vertices.Count;
            for (var i = 0; i < pts2.Length; i++)
            {
                var p = pts2[i];
                _vertices.Add(new Vector3(p.x, baseY + heightY, p.y));
                _normals.Add(Vector3.up);
                _uvs.Add(new Vector2(0f, 0f));
            }

            // Top face triangles (already clockwise indices from triangulator)
            for (var i = 0; i < tris.Length; i += 3)
            {
                _triangles.Add(baseIndexTop + tris[i + 0]);
                _triangles.Add(baseIndexTop + tris[i + 1]);
                _triangles.Add(baseIndexTop + tris[i + 2]);
            }

            // Bottom face (reverse winding)
            var baseIndexBottom = _vertices.Count;
            for (var i = 0; i < pts2.Length; i++)
            {
                var p = pts2[i];
                _vertices.Add(new Vector3(p.x, baseY, p.y));
                _normals.Add(Vector3.down);
                _uvs.Add(new Vector2(0f, 0f));
            }

            for (var i = 0; i < tris.Length; i += 3)
            {
                _triangles.Add(baseIndexBottom + tris[i + 0]);
                _triangles.Add(baseIndexBottom + tris[i + 2]);
                _triangles.Add(baseIndexBottom + tris[i + 1]);
            }

            // Side faces (quads per edge)
            // Vertex order: bottom-left, top-left, top-right, bottom-right (when viewed from outside)
            for (var i = 0; i < pts2.Length; i++)
            {
                var j = (i + 1) % pts2.Length;
                var a2 = pts2[i];
                var b2 = pts2[j];
                var a = new Vector3(a2.x, baseY, a2.y);
                var b = new Vector3(b2.x, baseY, b2.y);
                var c = new Vector3(b2.x, baseY + heightY, b2.y);
                var d = new Vector3(a2.x, baseY + heightY, a2.y);
                var edge = b - a;
                edge.y = 0f;
                // For clockwise footprints, edge x up points outward.
                var outward = Vector3.Cross(edge, Vector3.up).normalized;
                if (outward.sqrMagnitude < 1e-8f) outward = Vector3.forward;
                // Use vertex order that makes front face point outward: a, d, c, b
                AddQuad(a, d, c, b, outward);
            }

            return true;
        }

        public bool TryAddFlatPolygon(Vector2[] footprintXZ, float y, Vector3 normal)
        {
            if (footprintXZ == null || footprintXZ.Length < 3) return false;
            var pts2 = CleanPolygon(StripDuplicateClosure(footprintXZ));
            if (pts2.Length < 3) return false;
            if (!PolygonTriangulator.TryTriangulate(pts2, out var tris)) return false;

            var baseIndex = _vertices.Count;
            for (var i = 0; i < pts2.Length; i++)
            {
                var p = pts2[i];
                _vertices.Add(new Vector3(p.x, y, p.y));
                _normals.Add(normal);
                _uvs.Add(new Vector2(0f, 0f));
            }

            // If normal points down, flip winding.
            var flip = Vector3.Dot(normal, Vector3.up) < 0f;
            for (var i = 0; i < tris.Length; i += 3)
            {
                var a = baseIndex + tris[i + 0];
                var b = baseIndex + tris[i + 1];
                var c = baseIndex + tris[i + 2];
                if (!flip)
                {
                    _triangles.Add(a);
                    _triangles.Add(b);
                    _triangles.Add(c);
                }
                else
                {
                    _triangles.Add(a);
                    _triangles.Add(c);
                    _triangles.Add(b);
                }
            }

            return true;
        }

        public void AddFlatPolygonAabb(Vector2[] vertices2D, float y, float paddingMeters = 0f)
        {
            if (vertices2D == null || vertices2D.Length < 3) return;
            var minX = float.PositiveInfinity;
            var minZ = float.PositiveInfinity;
            var maxX = float.NegativeInfinity;
            var maxZ = float.NegativeInfinity;
            for (var i = 0; i < vertices2D.Length; i++)
            {
                var v = vertices2D[i];
                if (v.x < minX) minX = v.x;
                if (v.y < minZ) minZ = v.y;
                if (v.x > maxX) maxX = v.x;
                if (v.y > maxZ) maxZ = v.y;
            }

            minX -= paddingMeters;
            minZ -= paddingMeters;
            maxX += paddingMeters;
            maxZ += paddingMeters;

            AddQuad(
                new Vector3(minX, y, minZ),
                new Vector3(maxX, y, minZ),
                new Vector3(maxX, y, maxZ),
                new Vector3(minX, y, maxZ),
                Vector3.up);
        }

        private static Vector2[] EnsureClockwise(Vector2[] pts)
        {
            // In XZ plane, positive signed area corresponds to CCW. We want clockwise.
            var area = 0f;
            for (var i = 0; i < pts.Length; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Length];
                area += a.x * b.y - b.x * a.y;
            }

            if (area <= 0f) return pts;

            var rev = new Vector2[pts.Length];
            for (var i = 0; i < pts.Length; i++)
            {
                rev[i] = pts[pts.Length - 1 - i];
            }
            return rev;
        }

        private static Vector2[] StripDuplicateClosure(Vector2[] pts)
        {
            if (pts.Length >= 2 && pts[0] == pts[pts.Length - 1])
            {
                var outPts = new Vector2[pts.Length - 1];
                for (var i = 0; i < outPts.Length; i++) outPts[i] = pts[i];
                return outPts;
            }
            return pts;
        }

        private static Vector2[] CleanPolygon(Vector2[] pts, float epsilonMeters = 0.05f)
        {
            if (pts == null || pts.Length == 0) return Array.Empty<Vector2>();
            if (pts.Length < 3) return pts;

            var cleaned = new List<Vector2>(pts.Length);
            var prev = pts[0];
            cleaned.Add(prev);
            for (var i = 1; i < pts.Length; i++)
            {
                var p = pts[i];
                if ((p - prev).sqrMagnitude < (epsilonMeters * epsilonMeters)) continue;
                cleaned.Add(p);
                prev = p;
            }

            // If last equals first after cleaning, drop it.
            if (cleaned.Count >= 2 && cleaned[0] == cleaned[cleaned.Count - 1])
            {
                cleaned.RemoveAt(cleaned.Count - 1);
            }

            return cleaned.Count < 3 ? Array.Empty<Vector2>() : cleaned.ToArray();
        }

        private void AddFaceFan(Vector3[] verts, Vector3 normal, bool clockwise)
        {
            if (verts == null || verts.Length < 3) return;

            // Add vertices once per face fan.
            var baseIndex = _vertices.Count;
            for (var i = 0; i < verts.Length; i++)
            {
                _vertices.Add(verts[i]);
                _normals.Add(normal);
                _uvs.Add(new Vector2(0f, 0f));
            }

            for (var i = 1; i < verts.Length - 1; i++)
            {
                if (clockwise)
                {
                    _triangles.Add(baseIndex + 0);
                    _triangles.Add(baseIndex + i);
                    _triangles.Add(baseIndex + i + 1);
                }
                else
                {
                    _triangles.Add(baseIndex + 0);
                    _triangles.Add(baseIndex + i + 1);
                    _triangles.Add(baseIndex + i);
                }
            }
        }

        private void AddQuad(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, Vector3 normal)
        {
            // Ensure vertex winding matches the requested outward normal so backface culling works as expected.
            // Unity's front faces are determined by winding, not by stored normals.
            // Cross(v1-v0, v2-v0) points toward the triangle's front face direction.
            // If it points opposite to the desired normal, flip the winding.
            var face = Vector3.Cross(v1 - v0, v2 - v0);
            if (face.sqrMagnitude > 1e-8f && Vector3.Dot(face, normal) < 0f)
            {
                // Flip winding by swapping v1 and v3.
                (v1, v3) = (v3, v1);
            }

            var baseIndex = _vertices.Count;
            _vertices.Add(v0);
            _vertices.Add(v1);
            _vertices.Add(v2);
            _vertices.Add(v3);

            _normals.Add(normal);
            _normals.Add(normal);
            _normals.Add(normal);
            _normals.Add(normal);

            _uvs.Add(new Vector2(0f, 0f));
            _uvs.Add(new Vector2(1f, 0f));
            _uvs.Add(new Vector2(1f, 1f));
            _uvs.Add(new Vector2(0f, 1f));

            _triangles.Add(baseIndex + 0);
            _triangles.Add(baseIndex + 1);
            _triangles.Add(baseIndex + 2);
            _triangles.Add(baseIndex + 0);
            _triangles.Add(baseIndex + 2);
            _triangles.Add(baseIndex + 3);
        }

        public Mesh ToMesh(string name)
        {
            var mesh = new Mesh { name = name };
            if (_vertices.Count > 65535)
            {
                mesh.indexFormat = IndexFormat.UInt32;
            }
            mesh.SetVertices(_vertices);
            mesh.SetNormals(_normals);
            mesh.SetUVs(0, _uvs);
            mesh.SetTriangles(_triangles, 0, true);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
