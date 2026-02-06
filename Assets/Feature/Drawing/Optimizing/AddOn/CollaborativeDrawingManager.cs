//using System.Collections.Generic;
//using Fusion;
//using UnityEngine;
//using UnityEngine.UIElements;

///// <summary>
///// 입력 수집, Painter2D 고성능 렌더링, 상태 관리를 통합 수행합니다.
///// </summary>
//public class CollaborativeDrawingManager : NetworkBehaviour
//{

//    public UIDocument uiDocument;
//    public DrawingSyncBuffer syncBuffer;

//    private VisualElement _canvas;
//    private bool _isReady = false;

//    // 로컬 브러시 설정 (기존 기능 유지)
//    private Color _brushColor = Color.white;
//    private float _thickness = 2.0f;

//    public override void Spawned()
//    {
//        if (uiDocument == null) return;

//        _canvas = uiDocument.rootVisualElement.Q<VisualElement>("DrawingCanvas");
//        if (_canvas != null)
//        {
//            // Unity 6 Painter2D 렌더링 콜백 등록
//            _canvas.generateVisualContent += OnGenerateVisualContent;

//            // 데이터 변경 시 MarkDirtyRepaint 호출 (Image 1 에러 수정)
//            syncBuffer.OnDataChanged += () => _canvas.MarkDirtyRepaint();
//            _isReady = true;
//        }
//    }

//    /// <summary> 외부 UI 등에서 색상/두께 변경 시 호출 </summary>
//    public void SetBrush(Color color, float thickness) { _brushColor = color; _thickness = thickness; }

//    /// <summary> 마우스/펜 입력을 정규화하여 버퍼에 추가 </summary>
//    public void AddPoint(Vector2 screenPos, bool isNewPath)
//    {
//        if (!_isReady) return;

//        Rect bounds = _canvas.worldBound;
//        DrawingPoint p = new DrawingPoint
//        {
//            X = (screenPos.x - bounds.xMin) / bounds.width,
//            Y = (screenPos.y - bounds.yMin) / bounds.height,
//            Pressure = _thickness,
//            PointColor = _brushColor,
//            AuthorId = Runner.LocalPlayer.PlayerId,
//            IsNewPath = isNewPath
//        };

//        syncBuffer.AddEntry(p); // 링버퍼에 추가 (자동 배치 전송)
//    }

//    /// <summary> Unity 6 Painter2D Native 벡터 렌더링 루프 </summary>
//    private void OnGenerateVisualContent(MeshGenerationContext mgc)
//    {
//        var points = syncBuffer.GetCompleteData();
//        if (points == null |

//| points.Count == 0) return;

//        var painter = mgc.painter2D;
//        painter.lineCap = LineCap.Round;
//        painter.lineJoin = LineJoin.Round;
//        Rect rect = _canvas.contentRect;

//        bool activePath = false;

//        foreach (var p in points)
//        {
//            if (p.IsClear) { if (activePath) painter.Stroke(); activePath = false; continue; }

//            // 정규화 좌표 -> 로컬 픽셀 좌표 복원
//            Vector2 pos = new Vector2(p.X * rect.width, p.Y * rect.height);

//            if (p.IsNewPath || !activePath)
//            {
//                if (activePath) painter.Stroke();
//                painter.strokeColor = p.PointColor;
//                painter.lineWidth = p.Pressure;
//                painter.BeginPath();
//                painter.MoveTo(pos);
//                activePath = true;
//            }
//            else
//            {
//                painter.LineTo(pos);
//            }
//        }
//        if (activePath) painter.Stroke();
//    }
//}