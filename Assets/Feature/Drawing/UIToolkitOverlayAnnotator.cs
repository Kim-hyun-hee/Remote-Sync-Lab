using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// UI Toolkit 기반 Annotator.
/// 
/// - UIDocument의 root 아래에 VectorDrawingElementOptimized를 붙여서 그림.
/// - UXML 없이 코드로 생성하므로 "스크립트만"으로 실행 가능.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class UIToolkitOverlayAnnotatorOptimized : MonoBehaviour, IOverlayAnnotator
{
    public bool IsReady => _ready;

    [Header("Brush Default")]
    [SerializeField] private float defaultWidthPx = 4f;

    private UIDocument _doc;
    private VisualElement _root;
    private VectorDrawingElementOptimized _drawing;

    private bool _ready;

    private void Awake()
    {
        _doc = GetComponent<UIDocument>();
    }

    private void Start()
    {
        BuildUI();
        _ready = true;
    }

    private void BuildUI()
    {
        _root = _doc.rootVisualElement;
        _root.style.flexGrow = 1;

        _drawing = new VectorDrawingElementOptimized();
        _root.Add(_drawing);
    }

    public bool TryScreenToNormalized(Vector2 screenPos, out Vector2 norm)
    {
        norm = default;

        if (!_ready || _drawing == null)
            return false;

        // screen -> panel 좌표 
        Vector2 panel = RuntimePanelUtils.ScreenToPanel(_doc.rootVisualElement.panel, screenPos);

        Rect r = _drawing.worldBound;
        if (!r.Contains(panel))
            return false;

        // local 좌표 (0..w, 0..h)
        float localX = panel.x - r.xMin;
        float localY = panel.y - r.yMin;

        float w = r.width;
        float h = r.height;
        if (w <= 1f || h <= 1f) return false;

        // y 뒤집기:
        // - UI Toolkit의 y는 위->아래로 증가
        // - 우리는 norm.y를 "위쪽이 1"로 두면 도형/수학적으로 직관적
        float x = Mathf.Clamp01(localX / w);
        float y = Mathf.Clamp01(1f - (localY / h));

        norm = new Vector2(x, y);
        return true;
    }

    public void BeginBulk() => _drawing?.BeginBulk();
    public void EndBulk() => _drawing?.EndBulk();

    public void BeginStroke(OverlayStrokeKey key, Color32 color, float widthPx)
        => _drawing.BeginStroke(key, color, widthPx <= 0 ? defaultWidthPx : widthPx);

    public void AddPoints(OverlayStrokeKey key, IReadOnlyList<Vector2> normPoints)
        => _drawing.AddPoints(key, normPoints);

    public void EndStroke(OverlayStrokeKey key)
        => _drawing.EndStroke(key);

    public void AddLabel(int labelId, Vector2 normPos, string text)
        => _drawing.AddLabel(labelId, normPos, text);

    public void ClearAll()
        => _drawing.ClearAll();
}
