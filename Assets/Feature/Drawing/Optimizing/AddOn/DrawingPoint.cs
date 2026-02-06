//using System;
//using Fusion;
//using UnityEngine;
//using Fusion.Addons.DataSyncHelpers;

///// <summary>
///// 드로잉 포인트 정보를 담는 최적화 구조체입니다.
///// IRingBufferEntry를 구현하여 Payload Chunking을 지원합니다.
///// </summary>
//public struct DrawingPoint : IRingBufferEntry
//{
//    public float X;          // 정규화 X (0~1)
//    public float Y;          // 정규화 Y (0~1)
//    public float Pressure;   // 펜 압력 또는 두께
//    public Color PointColor; // 색상
//    public int AuthorId;     // 작성자 ID
//    public bool IsNewPath;   // 선의 시작점 여부 (Batching 구분자)
//    public bool IsClear;     // 지우기 신호

//    // 바이트 배열 직렬화 (SerializationTools 활용)
//    public byte AsByteArray => SerializationTools.AsByteArray(X, Y, Pressure, PointColor, AuthorId, IsNewPath, IsClear);

//    // 바이트 배열에서 데이터 복원 (Image 3의 pos 로직 적용)
//    public void FillFromBytes(byte entryBytes)
//    {
//        int pos = 0;
//        SerializationTools.Unserialize(entryBytes, ref pos, out X);
//        SerializationTools.Unserialize(entryBytes, ref pos, out Y);
//        SerializationTools.Unserialize(entryBytes, ref pos, out Pressure);
//        SerializationTools.Unserialize(entryBytes, ref pos, out PointColor);
//        SerializationTools.Unserialize(entryBytes, ref pos, out AuthorId);
//        SerializationTools.Unserialize(entryBytes, ref pos, out IsNewPath);
//        SerializationTools.Unserialize(entryBytes, ref pos, out IsClear);
//    }
//}