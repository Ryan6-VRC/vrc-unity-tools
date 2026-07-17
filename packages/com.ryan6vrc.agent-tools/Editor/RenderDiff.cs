using System;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    // Pure-logic pixel-buffer helpers for RenderAvatar diffing. Buffers are RGBA32 Color32[], row-major,
    // index = y*w + x. The caller's tiles are bottom-origin, but that's irrelevant here — these functions
    // report bounding boxes in raw buffer coords and leave origin interpretation to the caller.
    // Compares are EXACT on R,G,B (alpha ignored — composed tiles are always a=255); no tolerance, by design:
    // a silent tolerance would let real 1-LSB changes vanish and would turn near-colors into false matches.
    internal static class RenderDiff
    {
        // EXACT per-pixel rgb compare. changed = count of pixels whose rgb differ; bbox bounds them
        // (0,0,0,0 when none). Returns identical == (changed == 0).
        internal static bool Compare(Color32[] a, Color32[] b, int w, int h, out int changed, out RectInt bbox)
        {
            if (a == null || b == null || a.Length != b.Length || a.Length != w * h)
                throw new ArgumentException(
                    $"RenderDiff.Compare: buffers must be non-null and length w*h ({w}*{h}={w * h}); " +
                    $"got a={(a == null ? "null" : a.Length.ToString())}, b={(b == null ? "null" : b.Length.ToString())}");

            changed = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (int i = 0; i < a.Length; i++)
            {
                Color32 p = a[i], q = b[i];
                if (p.r != q.r || p.g != q.g || p.b != q.b)
                {
                    changed++;
                    int x = i % w, y = i / w;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }

            bbox = changed == 0
                ? new RectInt(0, 0, 0, 0)
                : new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return changed == 0;
        }
    }
}
