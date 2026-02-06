//using Fusion;
//using Fusion.Addons.DataSyncHelpers;
//using UnityEditor.PackageManager.Requests;
//using UnityEngine;

///// <summary>
///// 무손실 데이터 동기화를 담당합니다. Late Join 유저에게 히스토리를 자동 스트리밍합니다.
///// </summary>
//public class DrawingSyncBuffer : RingBufferLossLessSyncBehaviour<DrawingPoint>
//{
//    /// <summary> 데이터가 수신되거나 복구되었을 때 화면을 갱신하기 위한 이벤트 </summary>
//    public System.Action OnDataChanged;

//    protected override void OnNewEntries(byte newPaddingStartBytes, DrawingPoint newEntries)
//    {
//        base.OnNewEntries(newPaddingStartBytes, newEntries);
//        OnDataChanged?.Invoke();
//    }

//    protected override void OnLossRestored(LossRequest request, byte receivedData)
//    {
//        base.OnLossRestored(request, receivedData);
//        OnDataChanged?.Invoke();
//        Debug.Log($"<color=green></color> History Restored: {request.Range}");
//    }
//}