using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 네트워크로 "많은 포인트"를 보낼 때 가장 먼저 부딪히는 문제는:
/// 1) 메시지 크기 제한(= 한 번에 너무 큰 payload를 못 보냄)
/// 2) 빈번한 전송(= 초당 수십~수백 번 포인트가 생길 수 있음)
///
/// 그래서 "좌표 데이터의 크기"를 줄이는(압축/인코딩) 기법이 필요하다.
///
/// 이 클래스는:
/// - Vector2 (0~1 정규화 좌표) 를
/// - ushort (0~65535) 두 개로 양자화(quantization)해서
/// - 1 포인트당 4바이트로 직렬화한다.
///   (x:2바이트 + y:2바이트 = 4바이트)
///
/// 장점:
/// - float(4B) 두 개면 8바이트인데 → 4바이트로 반감
/// - 정규화 좌표(0~1)라는 전제가 있으니 가능한 방식
///
/// 단점(Trade-off):
/// - 정밀도가 약간 손실된다(양자화 오차).
///   하지만 드로잉 포인트는 "픽셀 단위" 의미가 강해서 대부분 충분하다.
/// </summary>
public static class StrokeNetEncoder
{
    /// <summary>
    /// float(0~1) 값을 ushort(0~65535)로 변환한다.
    ///
    /// - 네트워크에서는 float를 그대로 보내면 용량이 크다.
    /// - 0~1 범위면 16비트로도 꽤 촘촘하게 표현 가능(65536단계).
    ///
    /// Clamp01 + RoundToInt를 하는 이유:
    /// - 입력이 범위 밖으로 튀는 경우(계산 오차/사용자 입력)를 안전하게 처리
    /// - 중간값을 가장 가까운 정수 단계로 반올림해서 오차를 최소화
    /// </summary>
    public static ushort Float01ToU16(float v)
        => (ushort)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(v) * 65535f), 0, 65535);

    /// <summary>
    /// ushort(0~65535)를 float(0~1)로 되돌린다.
    ///
    /// 주의:
    /// - 원래 float로 "정확히" 복원되는 게 아니라,
    ///   65536 단계 중 하나로 복원된다(양자화).
    /// </summary>
    public static float U16ToFloat01(ushort v)
        => v / 65535f;

    /// <summary>
    /// 정규화 포인트 리스트를 byte[]로 패킹한다.
    ///
    /// 포맷:
    /// - 1 point = 4 bytes (little endian)
    ///   [x_low, x_high, y_low, y_high]
    ///
    /// Little endian을 쓰는 이유:
    /// - 대부분의 PC/모바일 환경에서 자연스러운 메모리 표현과 맞는다.
    /// - Unpack에서도 동일한 규칙으로 읽으면 된다.
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

            // x (2 bytes)
            bytes[o + 0] = (byte)(x & 0xFF);
            bytes[o + 1] = (byte)((x >> 8) & 0xFF);

            // y (2 bytes)
            bytes[o + 2] = (byte)(y & 0xFF);
            bytes[o + 3] = (byte)((y >> 8) & 0xFF);

            o += 4;
        }

        return bytes;
    }

    /// <summary>
    /// byte[]를 정규화 포인트 리스트로 언패킹한다.
    ///
    /// outPoints를 "새로 생성"하지 않고 Clear 후 재사용하는 이유:
    /// - 드로잉은 매우 빈번해서 GC(가비지) 압력을 줄이는 게 중요하다.
    /// - List를 재사용하면 메모리 할당을 크게 줄일 수 있다.
    /// </summary>
    public static void UnpackPoints(byte[] bytes, List<Vector2> outPoints)
    {
        outPoints.Clear();
        if (bytes == null) return;

        // 4바이트 단위로 읽는다.
        for (int o = 0; o + 3 < bytes.Length; o += 4)
        {
            ushort x = (ushort)(bytes[o + 0] | (bytes[o + 1] << 8));
            ushort y = (ushort)(bytes[o + 2] | (bytes[o + 3] << 8));

            outPoints.Add(new Vector2(U16ToFloat01(x), U16ToFloat01(y)));
        }
    }

    /// <summary>
    /// 포인트를 일정 개수(maxPointsPerChunk)로 쪼개서(Chunk) 순회한다.
    ///
    /// 왜 필요하나?
    /// - RPC/메시지에는 크기 제한이 있다.
    /// - 드로잉 포인트가 많아지면 한 번에 보내려다 제한에 걸릴 수 있다.
    ///
    /// onChunk(startIndex, count):
    /// - startIndex부터 count개를 하나의 chunk로 보내면 된다.
    ///
    /// 주의:
    /// - 이 함수는 "데이터를 보내주는" 게 아니라,
    ///   "어떻게 나눌지" 인덱스 구간만 제공한다.
    /// - 실제 전송은 AnnotationHub/RPC 쪽에서 한다.
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
