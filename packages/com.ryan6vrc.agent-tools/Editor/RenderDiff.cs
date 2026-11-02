using System;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    // Pixel-buffer compare for RenderAvatar diffing. Buffers are RGBA32 Color32[], row-major, index = y*w + x.
    // The bounding box is in raw buffer coords; the caller's tiles are bottom-origin, so the origin is the caller's
    // to interpret. Alpha is ignored: composed tiles are always a=255.
    internal static class RenderDiff
    {
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
