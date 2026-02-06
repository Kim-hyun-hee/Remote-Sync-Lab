//using Fusion;
//using UnityEngine;
//using UnityEngine.UIElements;

///// <summary>
///// 실시간 네트워크 통계와 히스토리 상태를 화면에 출력합니다.
///// </summary>
//public class DrawingDebugOverlay : SimulationBehaviour
//{
//    private CollaborativeDrawingManager manager;
//    private Label _statsLabel;

//    public void Setup(VisualElement root)
//    {
//        _statsLabel = new Label("Initializing...");
//        _statsLabel.style.backgroundColor = new StyleColor(new Color(0, 0, 0, 0.7f));
//        _statsLabel.style.color = Color.cyan;
//        _statsLabel.style.position = Position.Absolute;
//        _statsLabel.style.top = 20; _statsLabel.style.left = 20;
//        root.Add(_statsLabel);
//    }

//    /// <summary> IRender 대신 SimulationBehaviour의 Render 오버라이드 활용 </summary>
//    public override void Render()
//    {
//        if (Runner == null || !Runner.IsRunning || _statsLabel == null) return;

//        double rtt = Runner.GetPlayerRtt(Runner.LocalPlayer) * 1000;
//        int count = manager.GetComponent<DrawingSyncBuffer>().GetCompleteData()?.Count ?? 0;

//        _statsLabel.text = $"\n" +
//                           $"RTT: {rtt:F0}ms\n" +
//                           $"History: {count} pts\n" +
//                           $"Authority: {Object.HasStateAuthority}\n" +
//                           $"Local ID: {Runner.LocalPlayer.PlayerId}";
//    }
//}