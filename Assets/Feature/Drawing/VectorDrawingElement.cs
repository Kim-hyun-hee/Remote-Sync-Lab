using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// UI Toolkit 기반 "벡터 드로잉" VisualElement.
///
/// 기존 동시 드로잉 문제의 핵심 원인:
/// - "현재 스트로크 1개"만 갖고 AddPoint를 하면
/// - 서로 다른 사용자/서로 다른 스트로크의 포인트가 뒤섞여서
///   선이 이어 붙는 문제가 발생한다.
///
/// 해결:
/// - OverlayStrokeKey(authorId, strokeId)로 스트로크를 분리 관리한다.
/// - activeStrokeIndex[key] = strokes 리스트 인덱스를 저장
/// - AddPoint는 "마지막 스트로크"가 아니라 "해당 key의 스트로크"에 포인트를 넣는다.
///
/// 좌표계(중요):
/// - UIToolkitOverlayAnnotator.TryScreenToNormalized에서 v를 뒤집어 v=1-localY/h로 만들었다.
/// - 따라서 여기서 렌더링 할 때는 다시 yPx = (1 - normY) * h로 픽셀 Y를 구성해야
///   "클릭한 위치"와 "그려지는 위치"가 일치한다.
/// </summary>
public class VectorDrawingElement : VisualElement
{
    /// <summary>
    /// 렌더링할 스트로크 목록.
    /// - Stroke는 struct이지만 pointsNorm(List)는 참조 타입이라 내부 변경 가능.
    /// - 그래도 struct 리스트는 "꺼내서 수정 후 다시 넣기" 패턴이 안전하다.
    /// </summary>
    private readonly List<Stroke> strokes = new();

    /// <summary>
    /// 현재 진행 중인 스트로크를 빠르게 찾기 위한 맵.
    /// - key -> strokes 인덱스
    /// - 동시 입력이면 key가 여러 개 동시에 존재할 수 있다.
    /// </summary>
    private readonly Dictionary<OverlayStrokeKey, int> activeStrokeIndex = new();

    /// <summary>
    /// 스트로크 데이터.
    /// - color/width는 렌더링 스타일
    /// - pointsNorm은 (0~1) 정규화 좌표 목록
    /// </summary>
    private struct Stroke
    {
        public Color color;
        public float widthPx;
        public List<Vector2> pointsNorm;
    }

    public VectorDrawingElement()
    {
        // 화면 전체를 덮는 오버레이처럼 쓰기 위해 Absolute + Stretch
        style.position = Position.Absolute;
        style.left = 0;
        style.top = 0;
        style.right = 0;
        style.bottom = 0;

        // 입력 이벤트는 통과(클릭/드래그는 Controller가 처리)
        pickingMode = PickingMode.Ignore;

        // UI Toolkit이 렌더링 할 때 호출하는 콜백
        generateVisualContent += OnGenerate;

        // 학습용: 오버레이 영역이 보이게 약한 배경
        style.backgroundColor = new Color(0, 0, 0, 0.05f);
    }

    /// <summary>
    /// 특정 key(authorId, strokeId)의 스트로크 시작.
    ///
    /// 설계 결정:
    /// - 같은 key로 BeginStroke가 다시 호출되면 "새 스트로크를 추가"한다.
    ///   (이전 것을 덮어쓰지 않는다)
    /// - 네트워크 지연/재전송 환경에서는 같은 key가 중복될 수 있는데,
    ///   학습용 구현에서는 단순하게 "새로 시작"으로 처리한다.
    ///
    /// 실무적으로 더 엄격히 하려면:
    /// - 같은 key가 active일 땐 무시하거나
    /// - 기존 스트로크를 리셋하는 정책을 넣을 수 있다.
    /// </summary>
    public void BeginStroke(OverlayStrokeKey key, Color color, float widthPx)
    {
        var s = new Stroke
        {
            color = color,
            widthPx = widthPx,
            pointsNorm = new List<Vector2>(128)
        };

        strokes.Add(s);
        activeStrokeIndex[key] = strokes.Count - 1;

        MarkDirtyRepaint();
    }

    /// <summary>
    /// 특정 key 스트로크에 포인트 추가.
    ///
    /// 가장 중요한 부분:
    /// - "마지막 스트로크"에 넣지 않는다.
    /// - activeStrokeIndex[key]로 정확히 해당 스트로크를 찾아 넣는다.
    /// </summary>
    public void AddPoint(OverlayStrokeKey key, Vector2 norm)
    {
        if (!activeStrokeIndex.TryGetValue(key, out int index))
            return;

        if (index < 0 || index >= strokes.Count)
            return;

        // struct 리스트이므로 꺼내서 수정 후 다시 넣는다.
        var s = strokes[index];
        s.pointsNorm.Add(norm);
        strokes[index] = s;

        MarkDirtyRepaint();
    }

    /// <summary>
    /// 특정 key 스트로크 종료.
    /// - strokes 데이터는 남겨서 계속 렌더링됨(그려진 결과 유지)
    /// - activeStrokeIndex에서만 제거해서 더 이상 포인트가 붙지 않게 한다.
    /// </summary>
    public void EndStroke(OverlayStrokeKey key)
    {
        activeStrokeIndex.Remove(key);
        MarkDirtyRepaint();
    }

    /// <summary>
    /// 전체 제거(스트로크 + 텍스트 라벨).
    /// - 네트워크 Clear가 오면 이 함수가 호출되어야 한다.
    /// </summary>
    public void ClearAll()
    {
        strokes.Clear();
        activeStrokeIndex.Clear();
        ClearTextLabels();
        MarkDirtyRepaint();
    }

    /// <summary>
    /// 정규화 위치에 텍스트 라벨 추가.
    ///
    /// 좌표계 주의:
    /// - norm.y는 UIToolkitOverlayAnnotator에서 v를 뒤집은 값(= 위쪽이 1, 아래쪽이 0)
    /// - UI Toolkit의 top은 "위에서부터 내려오는 값"이므로,
    ///   topPercent = (1 - norm.y) * 100 이 일관된다.
    /// </summary>
    public void AddLabel(Vector2 norm, string text)
    {
        var label = new Label(text);

        label.style.position = Position.Absolute;

        // x는 그대로 percent
        label.style.left = Length.Percent(norm.x * 100f);

        // y는 뒤집어서 percent
        label.style.top = Length.Percent(norm.y * 100f);

        label.style.color = Color.white;
        label.style.unityFontStyleAndWeight = FontStyle.Bold;

        Add(label);
    }

    /// <summary>
    /// 텍스트 라벨만 제거.
    /// - 라벨은 child VisualElement로 붙어있고,
    ///   스트로크는 OnGenerate에서 painter2D로 그려지므로 별개다.
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
    /// UI Toolkit이 이 요소를 그릴 때 호출.
    /// strokes에 있는 모든 스트로크를 painter2D로 렌더링한다.
    ///
    /// painter2D 좌표계:
    /// - (0,0)은 요소의 좌상단
    /// - x는 오른쪽으로 증가
    /// - y는 아래로 증가
    ///
    /// norm 좌표계(현재 프로젝트 규칙):
    /// - x: 0(left) ~ 1(right)
    /// - y: 0(bottom) ~ 1(top)  (TryScreenToNormalized에서 뒤집었기 때문)
    ///
    /// 따라서:
    /// - xPx = norm.x * w
    /// - yPx = (1 - norm.y) * h
    /// 로 변환해야 "입력과 렌더가 일치"한다.
    /// </summary>
    private void OnGenerate(MeshGenerationContext ctx)
    {
        float w = resolvedStyle.width;
        float h = resolvedStyle.height;

        if (w <= 1f || h <= 1f)
            return;

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

            Vector2 P(int i)
            {
                var n = s.pointsNorm[i];
                return new Vector2(n.x * w, n.y * h);
            }

            painter.MoveTo(P(0));

            for (int i = 1; i < s.pointsNorm.Count; i++)
            {
                painter.LineTo(P(i));
            }

            painter.Stroke();
        }
    }
}
