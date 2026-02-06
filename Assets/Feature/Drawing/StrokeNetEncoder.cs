using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 네트워크 payload를 줄이기 위한 포인트 인코더.
/// 
/// 핵심 아이디어:
/// - 정규화 좌표(0..1)를 ushort(0..65535)로 변환하면
///   포인트 1개당 x,y 각각 2바이트 = 4바이트로 고정.
/// - float(4바이트) 두 개를 보내면 8바이트인데 절반으로 감소.
/// 
/// 주의:
/// - ushort 패킹은 '정밀도'가 약간 줄어들지만 (65536단계)
///   드로잉에는 보통 충분히 자연스럽다.
/// </summary>
public static class StrokeNetEncoderOptimized
{
    public const int BytesPerPoint = 4;

    public static ushort Float01ToU16(float v)
        => (ushort)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(v) * 65535f), 0, 65535);

    public static float U16ToFloat01(ushort v)
        => v / 65535f;

    /// <summary>
    /// points[start..start+count) 를 byte[]로 패킹.
    /// - 1 point = 4 bytes
    /// 
    /// 왜 새 배열을 생성하나?
    /// - 이상적으로는 ArrayPool로 재사용하고 싶지만,
    ///   Fusion RPC가 내부에서 언제까지 byte[]를 참조하는지(버전별) 확실치 않으면
    ///   재사용이 위험할 수 있다.
    /// - 안전 우선으로 "새 배열"을 기본으로 둔다.
    /// </summary>
    public static byte[] PackPoints(IReadOnlyList<Vector2> points, int start, int count)
    {
        var bytes = new byte[count * BytesPerPoint];
        int o = 0;

        for (int i = 0; i < count; i++)
        {
            var p = points[start + i];
            ushort x = Float01ToU16(p.x);
            ushort y = Float01ToU16(p.y);

            bytes[o + 0] = (byte)(x & 0xFF);
            bytes[o + 1] = (byte)((x >> 8) & 0xFF);
            bytes[o + 2] = (byte)(y & 0xFF);
            bytes[o + 3] = (byte)((y >> 8) & 0xFF);
            o += 4;
        }

        return bytes;
    }

    /// <summary>
    /// byte[]에서 points를 복원.
    /// - outPoints는 재사용(List.Clear 후 Add)로 GC를 줄인다.
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
    /// "바이트 예산" 기반 청킹 반복.
    /// 
    /// 왜 포인트 개수 기준이 아니라 바이트 기준인가?
    /// - 네트워크 제한은 보통 'payload byte 크기'로 걸린다.
    /// - 포인트 개수만으로 자르면 색/폭/추가 데이터가 섞일 때 계산이 틀어질 수 있다.
    /// - 바이트 예산으로 잘라야 안정적이다.
    /// </summary>
    public static void ForEachChunkByBytes(int totalPoints, int maxBytesPerChunk, Action<int, int> onChunk)
    {
        int maxPointsPerChunk = Mathf.Max(1, maxBytesPerChunk / BytesPerPoint);

        int start = 0;
        while (start < totalPoints)
        {
            int count = Mathf.Min(maxPointsPerChunk, totalPoints - start);
            onChunk(start, count);
            start += count;
        }
    }
}
