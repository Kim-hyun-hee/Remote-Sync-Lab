using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// UI Toolkit 기반 벡터 드로잉 VisualElement.
/// 
/// 기존 문제:
/// - "current 1개" + "strokes 마지막 요소에 AddPoint" 구조라서
///   네트워크로 포인트가 섞여 들어오면 서로 다른 사용자의 선이 이어져 버림.
/// 
/// 해결:
/// - OverlayStrokeKey(작성자+strokeId)로 스트로크를 분리해서 관리.
/// - activeStrokeIndex[key] = strokes 리스트의 인덱스를 저장.
/// - AddPoint는 "마지막 스트로크"가 아니라 "해당 key의 스트로크"를 찾아서 추가.
/// </summary>
public class VectorDrawingElement : VisualElement
{
    /// <summary>
    /// 렌더링할 스트로크 목록.
    /// - Stroke는 struct이지만 pointsNorm(List)는 참조 타입이라 내부 변경 가능.
    /// </summary>
    private readonly List<Stroke> strokes = new();

    /// <summary>
    /// 현재 진행 중인 스트로크를 찾기 위한 맵.
    /// - key -> strokes 인덱스
    /// - 동시에 여러 명이 그리면 key가 여러 개 존재.
    /// </summary>
    private readonly Dictionary<OverlayStrokeKey, int> activeStrokeIndex = new();

    /// <summary>
    /// 한 개의 스트로크 데이터.
    /// </summary>
    private struct Stroke
    {
        /// <summary>선 색상</summary>
        public Color color;

        /// <summary>선 두께(px)</summary>
        public float widthPx;

        /// <summary>
        /// 스트로크를 구성하는 점 목록(정규화 0~1).
        /// </summary>
        public List<Vector2> pointsNorm;
    }

    /// <summary>
    /// 생성자: UI Toolkit element 기본 스타일 설정 및 렌더 콜백 연결.
    /// </summary>
    public VectorDrawingElement()
    {
        // 화면 전체를 덮도록 절대 좌표.
        style.position = Position.Absolute;
        style.left = 0;
        style.top = 0;
        style.right = 0;
        style.bottom = 0;

        // 입력 이벤트는 통과 (드로잉만 표시)
        pickingMode = PickingMode.Ignore;

        // UI Toolkit 그리기 콜백
        generateVisualContent += OnGenerate;

        // 배경을 아주 약하게 표시 (원하면 제거 가능)
        style.backgroundColor = new Color(0, 0, 0, 0.05f);
    }

    /// <summary>
    /// 특정 스트로크(key)에 대한 시작.
    /// - 이미 같은 key가 active면 덮어쓰는 대신 "새 스트로크로 재시작" 하도록 설계.
    /// </summary>
    public void BeginStroke(OverlayStrokeKey key, Color color, float widthPx)
    {
        var s = new Stroke
        {
            color = color,
            widthPx = widthPx,
            pointsNorm = new List<Vector2>(128)
        };

        // 새 스트로크를 리스트에 추가하고, 해당 인덱스를 key에 매핑.
        strokes.Add(s);
        activeStrokeIndex[key] = strokes.Count - 1;

        MarkDirtyRepaint();
    }

    /// <summary>
    /// 특정 스트로크(key)에 점 추가.
    /// </summary>
    public void AddPoint(OverlayStrokeKey key, Vector2 norm)
    {
        // key에 해당하는 스트로크가 진행 중인지 확인.
        if (!activeStrokeIndex.TryGetValue(key, out int index))
            return;

        if (index < 0 || index >= strokes.Count)
            return;

        // strokes는 struct 리스트이므로 꺼내서 수정 후 다시 넣어줘야 함.
        var s = strokes[index];
        s.pointsNorm.Add(norm);
        strokes[index] = s;

        MarkDirtyRepaint();
    }

    /// <summary>
    /// 특정 스트로크(key) 종료.
    /// - 종료해도 strokes 리스트의 데이터는 남아 렌더링됨.
    /// - activeStrokeIndex에서만 제거해서 "더 이상 점이 붙지 않게" 만든다.
    /// </summary>
    public void EndStroke(OverlayStrokeKey key)
    {
        activeStrokeIndex.Remove(key);
        MarkDirtyRepaint();
    }

    /// <summary>
    /// 전체 삭제(스트로크 + 텍스트).
    /// </summary>
    public void ClearAll()
    {
        strokes.Clear();
        activeStrokeIndex.Clear();
        ClearTextLabels();
        MarkDirtyRepaint();
    }

    /// <summary>
    /// 정규화 좌표 위치에 텍스트 라벨을 UI Toolkit Label로 추가.
    /// </summary>
    public void AddLabel(Vector2 norm, string text)
    {
        var label = new Label(text);

        label.style.position = Position.Absolute;

        // Label의 left/top은 픽셀/퍼센트 등을 사용할 수 있음.
        label.style.left = Length.Percent(norm.x * 100f);
        label.style.top = Length.Percent(norm.y * 100f);

        label.style.color = Color.white;
        label.style.unityFontStyleAndWeight = FontStyle.Bold;

        Add(label);
    }

    /// <summary>
    /// Label들만 제거.
    /// - 스트로크는 자체 렌더링이고 Label은 자식 요소이므로,
    ///   childCount를 순회하며 Label 타입만 삭제.
    /// </summary>
    private void ClearTextLabels()
    {
        for (int i = childCount - 1; i >= 0; i--)
        {
            if (ElementAt(i) is Label)
                RemoveAt(i);
        }
    }

    /// <summary>
    /// UI Toolkit이 화면에 그릴 때 호출하는 렌더 함수.
    /// strokes에 저장된 모든 스트로크를 painter2D로 렌더링.
    /// </summary>
    private void OnGenerate(MeshGenerationContext ctx)
    {
        float w = resolvedStyle.width;
        float h = resolvedStyle.height;

        // 너무 작은 경우 렌더링 생략
        if (w <= 1f || h <= 1f) return;

        var painter = ctx.painter2D;
        painter.lineCap = LineCap.Round;
        painter.lineJoin = LineJoin.Round;

        foreach (var s in strokes)
        {
            if (s.pointsNorm == null || s.pointsNorm.Count < 2)
                continue;

            painter.strokeColor = s.color;
            painter.lineWidth = s.widthPx;

            painter.BeginPath();

            // 정규화 -> 픽셀 좌표 변환
            var p0 = new Vector2(s.pointsNorm[0].x * w, s.pointsNorm[0].y * h);
            painter.MoveTo(p0);

            for (int i = 1; i < s.pointsNorm.Count; i++)
            {
                var p = new Vector2(s.pointsNorm[i].x * w, s.pointsNorm[i].y * h);
                painter.LineTo(p);
            }

            painter.Stroke();
        }
    }
}
