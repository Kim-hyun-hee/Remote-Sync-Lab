using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// UI Toolkit(UIDocument) 기반 오버레이 드로잉 구현체.
///
/// 구조:
/// - UIDocument.rootVisualElement 아래에
/// - VectorDrawingElement(커스텀 VisualElement)를 붙여서
/// - painter2D로 선을 그린다.
///
/// 핵심 포인트:
/// - UI Toolkit은 패널(panel)이 준비되기 전에는 좌표 변환(ScreenToPanel)이 불가능하다.
///   그래서 EnsureBuilt() 코루틴으로 "패널 준비 완료"를 기다린 다음 drawing을 생성/부착한다.
/// - 정규화 좌표의 Y 방향은 UI Toolkit 좌표계(위->아래 증가)와 혼동되기 쉬워서
///   TryScreenToNormalized에서 v를 뒤집고(1 - localY/h),
///   실제 렌더링(VectorDrawingElement)에서도 yPx = (1 - normY) * h로 일관시킨다.
/// </summary>
public class UIToolkitOverlayAnnotator : MonoBehaviour, IOverlayAnnotator
{
    [Header("UI Toolkit")]
    [SerializeField] private UIDocument uiDocument;

    /// <summary>
    /// drawing(VectorDrawingElement)을 붙일 부모 VisualElement의 이름.
    /// - 비어있으면 rootVisualElement에 바로 붙인다.
    /// - 특정 컨테이너에 붙이고 싶을 때만 이름을 지정.
    /// </summary>
    [SerializeField] private string attachToRootName = "";

    /// <summary>
    /// 실제 드로잉을 담당하는 커스텀 VisualElement.
    /// </summary>
    private VectorDrawingElement drawing;

    /// <summary>
    /// drawing이 부착된 부모(컨테이너).
    /// </summary>
    private VisualElement attachRoot;

    /// <summary>
    /// 외부에서 SetActive로 켜고/끄는 상태값.
    /// - UI Toolkit은 "GameObject 활성/비활성"과 별개로
    ///   VisualElement 표시(DisplayStyle)를 제어해야 한다.
    /// </summary>
    private bool active;

    /// <summary>
    /// UIDocument/panel 준비를 기다렸다가 drawing을 생성하기 위한 코루틴 핸들.
    /// </summary>
    private Coroutine ensureRoutine;

    /// <summary>
    /// "지금 입력/렌더가 가능한 상태인지"를 알려준다.
    ///
    /// 체크 항목:
    /// - active가 true인지
    /// - UIDocument가 연결돼 있는지
    /// - rootVisualElement가 있는지
    /// - panel이 준비됐는지 (중요)
    /// - drawing이 생성됐는지
    /// - attachRoot가 유효한지
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
        // 켜질 때 UI Toolkit 패널 준비를 보장하기 위해 빌드 루틴 시작
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
    /// 오버레이 표시 on/off.
    ///
    /// 주의:
    /// - SetActive(true)를 호출했다고 즉시 IsReady가 true가 되는 게 아니다.
    /// - UI Toolkit panel 준비 타이밍이 뒤에 올 수 있어서 EnsureBuilt 코루틴이 필요하다.
    /// </summary>
    public void SetActive(bool active)
    {
        this.active = active;

        // 켜는 순간 빌드가 안 돼 있다면 다시 EnsureBuilt 시도
        if (active && ensureRoutine == null)
            ensureRoutine = StartCoroutine(EnsureBuilt());

        // 실제 표시 여부 적용
        if (drawing != null)
            drawing.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
    }

    /// <summary>
    /// UIDocument의 root/panel이 준비될 때까지 기다렸다가,
    /// VectorDrawingElement를 생성해서 attachRoot에 부착한다.
    ///
    /// panel을 기다리는 이유:
    /// - RuntimePanelUtils.ScreenToPanel(panel, screenPos) 같은 변환은
    ///   panel이 null이면 동작 자체가 불가능하다.
    /// </summary>
    private IEnumerator EnsureBuilt()
    {
        // UIDocument 할당 대기
        while (uiDocument == null)
            yield return null;

        // rootVisualElement 준비 대기
        while (uiDocument.rootVisualElement == null)
            yield return null;

        // panel 준비 대기 (UI Toolkit에서 가장 중요한 준비 단계)
        while (uiDocument.rootVisualElement.panel == null)
            yield return null;

        var root = uiDocument.rootVisualElement;

        // attachRoot 탐색(이름이 지정된 경우)
        attachRoot = string.IsNullOrWhiteSpace(attachToRootName)
            ? root
            : root.Q<VisualElement>(attachToRootName);

        // 못 찾으면 root에 붙인다.
        if (attachRoot == null)
            attachRoot = root;

        // drawing 생성/부착
        if (drawing == null)
        {
            drawing = new VectorDrawingElement();
            attachRoot.Add(drawing);
        }

        // 표시 상태 반영
        drawing.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;

        ensureRoutine = null;
    }

    /// <summary>
    /// Screen 좌표를 정규화 좌표(0~1)로 변환.
    ///
    /// 흐름:
    /// 1) Screen -> Panel 좌표로 변환
    /// 2) Panel(world) -> drawing local 좌표로 변환
    /// 3) local을 drawing 크기로 나눠서 0~1 정규화
    /// 4) UI Toolkit 좌표계 때문에 Y를 뒤집어 v=1-localY/h
    ///
    /// 반환 false인 경우:
    /// - IsReady가 아님
    /// - drawing 영역 밖을 클릭함
    /// </summary>
    public bool TryScreenToNormalized(Vector2 screenPos, out Vector2 normalized)
    {
        normalized = default;
        if (!IsReady) return false;

        var panel = uiDocument.rootVisualElement.panel;

        // 1) Screen -> Panel
        Vector2 panelPos = RuntimePanelUtils.ScreenToPanel(panel, screenPos);

        // 2) Panel(world) -> drawing local
        Vector2 local = drawing.WorldToLocal(panelPos);

        // 3) local -> 0~1 정규화
        float w = Mathf.Max(1f, drawing.resolvedStyle.width);
        float h = Mathf.Max(1f, drawing.resolvedStyle.height);

        float u = local.x / w;

        // 4) Y 뒤집기: top(0)일 때 v=1, bottom(h)일 때 v=0이 되게 만든다.
        // 이 값은 "수학 좌표계처럼 아래가 0"인 느낌으로 정규화되는 셈.
        float v = 1f - (local.y / h);

        if (u < 0f || u > 1f || v < 0f || v > 1f)
            return false;

        normalized = new Vector2(u, v);
        return true;
    }

    /// <summary>
    /// 현재 drawing 영역의 픽셀 크기.
    /// - 포인트 샘플링(minDistance)을 정규화로 바꿀 때 사용됨.
    /// </summary>
    public Vector2 GetRenderSizePx()
    {
        if (drawing == null) return Vector2.one;

        return new Vector2(
            Mathf.Max(1f, drawing.resolvedStyle.width),
            Mathf.Max(1f, drawing.resolvedStyle.height)
        );
    }

    // ===== IOverlayAnnotator: Stroke API =====

    public void BeginStroke(OverlayStrokeKey key, Color color, float widthPx)
    {
        if (!IsReady) return;
        drawing.BeginStroke(key, color, widthPx);
    }

    public void AddStrokePoint(OverlayStrokeKey key, Vector2 normalized)
    {
        if (!IsReady) return;
        drawing.AddPoint(key, normalized);
    }

    public void EndStroke(OverlayStrokeKey key)
    {
        if (!IsReady) return;
        drawing.EndStroke(key);
    }

    // ===== Text / Clear =====

    public void AddText(Vector2 normalized, string text)
    {
        if (!IsReady) return;
        drawing.AddLabel(normalized, text);
    }

    public void Clear()
    {
        if (!IsReady) return;
        drawing.ClearAll();
    }
}
