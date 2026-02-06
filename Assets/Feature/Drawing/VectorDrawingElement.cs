using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Painter2D 기반 벡터 드로잉 VisualElement.
/// 
/// 성능 포인트:
/// - AddPoints로 여러 포인트를 한 번에 넣기(호출 수 감소)
/// - 스냅샷 재생 시 BeginBulk/EndBulk로 MarkDirtyRepaint를 묶기(리페인트 폭발 방지)
/// </summary>
public class VectorDrawingElementOptimized : VisualElement
{
    private struct Stroke
    {
        public Color32 color;
        public float widthPx;
        public List<Vector2> pointsNorm; // (0..1), y는 "top=1"
    }

    private readonly List<Stroke> _strokes = new(256);
    private readonly Dictionary<OverlayStrokeKey, int> _activeIndex = new(256);

    private int _bulkDepth;

    public VectorDrawingElementOptimized()
    {
        style.position = Position.Absolute;
        style.left = 0;
        style.top = 0;
        style.right = 0;
        style.bottom = 0;

        pickingMode = PickingMode.Ignore;

        generateVisualContent += OnGenerate;
    }

    public void BeginBulk() => _bulkDepth++;

    public void EndBulk()
    {
        _bulkDepth = Mathf.Max(0, _bulkDepth - 1);
        if (_bulkDepth == 0)
            MarkDirtyRepaint();
    }

    public void BeginStroke(OverlayStrokeKey key, Color32 color, float widthPx)
    {
        Debug.Log("Begin");
        var s = new Stroke
        {
            color = color,
            widthPx = widthPx,
            pointsNorm = new List<Vector2>(256)
        };

        _strokes.Add(s);
        _activeIndex[key] = _strokes.Count - 1;

        DirtyIfNotBulk();
    }

    public void AddPoints(OverlayStrokeKey key, IReadOnlyList<Vector2> normPoints)
    {
        if (!_activeIndex.TryGetValue(key, out int idx)) return;
        if (idx < 0 || idx >= _strokes.Count) return;

        var s = _strokes[idx];
        s.pointsNorm.AddRange(normPoints);
        _strokes[idx] = s;

        DirtyIfNotBulk();
    }

    public void EndStroke(OverlayStrokeKey key)
    {
        _activeIndex.Remove(key);
        DirtyIfNotBulk();
    }

    public void AddLabel(int labelId, Vector2 norm, string text)
    {
        var label = new Label(text);
        label.style.position = Position.Absolute;

        // norm.y는 top=1이므로, UI의 top(0=위)로 변환하려면 (1-norm.y)
        label.style.left = Length.Percent(norm.x * 100f);
        label.style.top = Length.Percent(norm.y * 100f);

        label.style.color = Color.white;
        label.style.unityFontStyleAndWeight = FontStyle.Bold;

        Add(label);
    }

    public void ClearAll()
    {
        _strokes.Clear();
        _activeIndex.Clear();
        ClearLabels();
        MarkDirtyRepaint();
    }

    private void ClearLabels()
    {
        for (int i = childCount - 1; i >= 0; i--)
        {
            if (ElementAt(i) is Label)
                RemoveAt(i);
        }
    }

    private void DirtyIfNotBulk()
    {
        if (_bulkDepth == 0)
            MarkDirtyRepaint();
    }

    private void OnGenerate(MeshGenerationContext ctx)
    {
        float w = resolvedStyle.width;
        float h = resolvedStyle.height;
        if (w <= 1f || h <= 1f) return;

        var painter = ctx.painter2D;
        painter.lineCap = LineCap.Round;
        painter.lineJoin = LineJoin.Round;

        for (int sIdx = 0; sIdx < _strokes.Count; sIdx++)
        {
            var s = _strokes[sIdx];
            if (s.pointsNorm == null || s.pointsNorm.Count < 2) continue;

            painter.strokeColor = s.color;
            painter.lineWidth = s.widthPx;
            painter.BeginPath();

            Vector2 ToPx(Vector2 n)
            {
                // norm.y(top=1) -> painter 좌표(top=0)로: (1-n.y)
                float x = n.x * w;
                float y = n.y * h;
                return new Vector2(x, y);
            }

            painter.MoveTo(ToPx(s.pointsNorm[0]));
            for (int i = 1; i < s.pointsNorm.Count; i++)
                painter.LineTo(ToPx(s.pointsNorm[i]));

            painter.Stroke();
        }
    }
}
