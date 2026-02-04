using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// UI Toolkit 위에 VectorDrawingElement를 붙여서 드로잉하는 Annotator 구현체.
/// 
/// 변경점:
/// - IOverlayAnnotator의 Begin/Add/End에 OverlayStrokeKey가 추가됨.
/// - drawing.BeginStroke/AddPoint/EndStroke가 key 기반으로 동작하도록 변경.
/// </summary>
public class UIToolkitOverlayAnnotator : MonoBehaviour, IOverlayAnnotator
{
    [Header("UI Toolkit")]

    /// <summary>
    /// UI Toolkit 트리의 루트가 되는 UIDocument.
    /// </summary>
    [SerializeField] private UIDocument uiDocument;

    /// <summary>
    /// drawing을 붙일 루트 이름.
    /// - 비워두면 rootVisualElement에 바로 붙임.
    /// </summary>
    [SerializeField] private string attachToRootName = "";

    /// <summary>
    /// 실제 드로잉을 수행하는 VisualElement.
    /// </summary>
    private VectorDrawingElement drawing;

    /// <summary>
    /// drawing이 붙어 있는 부모 VisualElement.
    /// </summary>
    private VisualElement attachRoot;

    /// <summary>
    /// 외부에서 SetActive로 설정하는 활성 플래그.
    /// </summary>
    private bool active;

    /// <summary>
    /// UIDocument/panel 준비를 기다리며 drawing을 생성/부착하는 코루틴.
    /// </summary>
    private Coroutine ensureRoutine;

    /// <summary>
    /// 준비 완료 여부.
    /// - active가 true이고
    /// - uiDocument/root/panel/drawing/attachRoot가 모두 유효할 때 true.
    /// </summary>
    public bool IsReady =>
        active &&
        uiDocument != null &&
        uiDocument.rootVisualElement != null &&
        uiDocument.rootVisualElement.panel != null &&
        drawing != null &&
        attachRoot != null;

    private void OnEnable()
    {
        // 자주 "켜진 상태에서 바로 사용"하는 케이스가 많아서 OnEnable에서 준비 시작.
        if (ensureRoutine == null)
            ensureRoutine = StartCoroutine(EnsureBuilt());
    }

    private void OnDisable()
    {
        // 코루틴 정리
        if (ensureRoutine != null)
        {
            StopCoroutine(ensureRoutine);
            ensureRoutine = null;
        }
    }

    /// <summary>
    /// 오버레이 표시 On/Off.
    /// </summary>
    public void SetActive(bool active)
    {
        this.active = active;

        // 켤 때 준비가 안 되어 있으면 다시 빌드 시도
        if (active && ensureRoutine == null)
            ensureRoutine = StartCoroutine(EnsureBuilt());

        // 실제 표시 여부 반영
        if (drawing != null)
            drawing.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
    }

    /// <summary>
    /// UIDocument의 root/panel이 준비될 때까지 기다린 후,
    /// VectorDrawingElement를 생성해서 attachRoot에 붙인다.
    /// </summary>
    private IEnumerator EnsureBuilt()
    {
        // UIDocument 할당 대기
        while (uiDocument == null)
            yield return null;

        // rootVisualElement 준비 대기
        while (uiDocument.rootVisualElement == null)
            yield return null;

        // panel 준비 대기 (UI Toolkit에서 매우 중요)
        while (uiDocument.rootVisualElement.panel == null)
            yield return null;

        var root = uiDocument.rootVisualElement;

        // attachRoot 탐색
        attachRoot = string.IsNullOrWhiteSpace(attachToRootName)
            ? root
            : root.Q<VisualElement>(attachToRootName);

        // 이름으로 못 찾으면 root에 붙임
        if (attachRoot == null)
            attachRoot = root;

        // drawing 생성 및 부착
        if (drawing == null)
        {
            drawing = new VectorDrawingElement();
            attachRoot.Add(drawing);
        }

        // 활성 상태 반영
        drawing.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;

        ensureRoutine = null;
    }

    /// <summary>
    /// Screen 좌표를 Overlay 기준 정규화 좌표(0~1)로 변환.
    /// </summary>
    public bool TryScreenToNormalized(Vector2 screenPos, out Vector2 normalized)
    {
        normalized = default;
        if (!IsReady) return false;

        var panel = uiDocument.rootVisualElement.panel;

        // Screen -> Panel 좌표
        Vector2 panelPos = RuntimePanelUtils.ScreenToPanel(panel, screenPos);

        // Panel(world) -> drawing local 좌표
        Vector2 local = drawing.WorldToLocal(panelPos);

        float w = Mathf.Max(1f, drawing.resolvedStyle.width);
        float h = Mathf.Max(1f, drawing.resolvedStyle.height);

        float u = local.x / w;

        // UI Toolkit local.y는 위에서 아래로 증가하므로 v를 뒤집어준다.
        float v = 1f - (local.y / h);

        if (u < 0f || u > 1f || v < 0f || v > 1f)
            return false;

        normalized = new Vector2(u, v);
        return true;
    }

    /// <summary>
    /// 렌더링 영역 픽셀 크기 반환.
    /// </summary>
    public Vector2 GetRenderSizePx()
    {
        if (drawing == null) return Vector2.one;
        return new Vector2(
            Mathf.Max(1f, drawing.resolvedStyle.width),
            Mathf.Max(1f, drawing.resolvedStyle.height)
        );
    }

    /// <summary>
    /// key 기반 스트로크 시작.
    /// </summary>
    public void BeginStroke(OverlayStrokeKey key, Color color, float widthPx)
    {
        if (!IsReady) return;
        drawing.BeginStroke(key, color, widthPx);
    }

    /// <summary>
    /// key 기반 점 추가.
    /// </summary>
    public void AddStrokePoint(OverlayStrokeKey key, Vector2 normalized)
    {
        if (!IsReady) return;
        drawing.AddPoint(key, normalized);
    }

    /// <summary>
    /// key 기반 스트로크 종료.
    /// </summary>
    public void EndStroke(OverlayStrokeKey key)
    {
        if (!IsReady) return;
        drawing.EndStroke(key);
    }

    /// <summary>
    /// 텍스트 추가.
    /// </summary>
    public void AddText(Vector2 normalized, string text)
    {
        if (!IsReady) return;
        drawing.AddLabel(normalized, text);
    }

    /// <summary>
    /// 전체 삭제.
    /// </summary>
    public void Clear()
    {
        if (!IsReady) return;
        drawing.ClearAll();
    }
}
