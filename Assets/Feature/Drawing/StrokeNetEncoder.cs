using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 네트워크 전송량을 줄이기 위한 포인트 인코더.
/// 
/// - Vector2(0~1) 정규화 좌표를
/// - ushort(0~65535) 2개로 양자화하여
/// - 1 포인트당 4바이트로 직렬화.
/// </summary>
public static class StrokeNetEncoder
{
    /// <summary>
    /// float 0~1 값을 ushort(0~65535)로 변환.
    /// </summary>
    public static ushort Float01ToU16(float v)
        => (ushort)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(v) * 65535f), 0, 65535);

    /// <summary>
    /// ushort(0~65535)을 float 0~1로 변환.
    /// </summary>
    public static float U16ToFloat01(ushort v)
        => v / 65535f;

    /// <summary>
    /// 포인트 리스트를 바이트 배열로 패킹.
    /// - 1 point = 4 bytes (x ushort + y ushort, little endian)
    /// </summary>
    public static byte[] PackPoints(IReadOnlyList<Vector2> points)
    {
        int n = points.Count;
        var bytes = new byte[n * 4];
        int o = 0;

        for (int i = 0; i < n; i++)
        {
            ushort x = Float01ToU16(points[i].x);
            ushort y = Float01ToU16(points[i].y);

            bytes[o + 0] = (byte)(x & 0xFF);
            bytes[o + 1] = (byte)((x >> 8) & 0xFF);
            bytes[o + 2] = (byte)(y & 0xFF);
            bytes[o + 3] = (byte)((y >> 8) & 0xFF);
            o += 4;
        }

        return bytes;
    }

    /// <summary>
    /// 바이트 배열을 포인트 리스트로 언팩.
    /// </summary>
    public static void UnpackPoints(byte[] bytes, List<Vector2> outPoints)
    {
        outPoints.Clear();
        if (bytes == null) return;

        for (int o = 0; o + 3 < bytes.Length; o += 4)
        {
            ushort x = (ushort)(bytes[o + 0] | (bytes[o + 1] << 8));
            ushort y = (ushort)(bytes[o + 2] | (bytes[o + 3] << 8));
            outPoints.Add(new Vector2(U16ToFloat01(x), U16ToFloat01(y)));
        }
    }

    /// <summary>
    /// points를 maxPointsPerChunk 단위로 나눠서 콜백으로 전달.
    /// - RPC 페이로드 크기를 제한하기 위해 사용.
    /// </summary>
    public static void ForEachChunk(IReadOnlyList<Vector2> points, int maxPointsPerChunk, System.Action<int, int> onChunk)
    {
        int n = points.Count;
        int start = 0;
        while (start < n)
        {
            int count = Mathf.Min(maxPointsPerChunk, n - start);
            onChunk(start, count);
            start += count;
        }
    }
}
