using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// uGUI(RawImage) 위에 Texture2D를 생성하고 픽셀 버퍼에 선을 그리는 Annotator.
/// 
/// 목표:
/// - 네트워크에서 동시에 여러 스트로크가 들어와도 섞이지 않게 key(OverlayStrokeKey)별로 상태를 분리
/// - 큰 히스토리(스냅샷) 재생 시에도 성능이 무너지지 않도록 BeginBulk/EndBulk로 Apply를 묶어줌
/// 
/// 최신 IOverlayAnnotator 인터페이스에 맞춘 변경점:
/// - GetRenderSizePx() 삭제(인터페이스에서 제거됨)
/// - AddStrokePoint -> AddPoints(키, IReadOnlyList<Vector2>)
/// - AddText -> AddLabel(labelId, normPos, text)
/// - Clear -> ClearAll
/// - BeginStroke 색 타입 Color32로 통일
/// </summary>
public class Texture2DOverlayAnnotator : MonoBehaviour, IOverlayAnnotator
{
    [Header("uGUI References")]

    /// <summary>드로잉 결과를 표시할 RawImage</summary>
    [SerializeField] private RawImage targetRawImage;

    /// <summary>정규화 좌표 계산을 위한 RectTransform</summary>
    [SerializeField] private RectTransform targetRect;

    /// <summary>ScreenPointToLocalPointInRectangle 변환에 필요한 Canvas</summary>
    [SerializeField] private Canvas rootCanvas;

    [Header("Texture Settings")]

    /// <summary>
    /// 렌더링 해상도 스케일.
    /// - 1.0이면 UI 크기 그대로 텍스처 생성
    /// - 0.5면 가로/세로 절반 해상도로 생성하여 성능 향상
    /// </summary>
    [Range(0.25f, 1f)]
    [SerializeField] private float resolutionScale = 0.5f;

    /// <summary>Clear 시 채울 색(투명 포함)</summary>
    [SerializeField] private Color clearColor = new Color(0, 0, 0, 0);

    [Header("Performance")]

    /// <summary>
    /// Texture2D.Apply 호출 간격.
    /// - 매 프레임 Apply는 비싸므로 일정 간격으로만 적용
    /// </summary>
    [SerializeField] private float applyInterval = 0.033f;

    /// <summary>생성된 텍스처</summary>
    private Texture2D tex;

    /// <summary>텍스처 픽셀 버퍼(Color32)</summary>
    private Color32[] buffer;

    /// <summary>텍스처 크기</summary>
    private int texW, texH;

    /// <summary>활성 여부</summary>
    private bool active = true;

    /// <summary>UI 크기 변경 감지용</summary>
    private Vector2 lastRectSize;

    /// <summary>다음 Apply 가능한 시간</summary>
    private float nextApplyTime;

    /// <summary>버퍼 변경 여부</summary>
    private bool dirty;

    /// <summary>
    /// Bulk 모드 카운터.
    /// - BeginBulk 호출 시 증가
    /// - EndBulk 호출 시 감소
    /// - Bulk 중에는 Apply를 자동으로 하지 않고, EndBulk에서 한 번만 Apply하도록 한다.
    /// 
    /// 왜 필요한가?
    /// - Late Join 스냅샷에서 포인트가 수천~수만개 들어오면,
    ///   포인트마다 Apply가 발생하는 순간 프레임이 크게 끊긴다.
    /// - Bulk로 묶으면 "마지막에 한 번만 Apply"할 수 있어 비용이 급감한다.
    /// </summary>
    private int bulkDepth;

    /// <summary>
    /// 스트로크별 상태(동시 스트로크 지원).
    /// </summary>
    private struct StrokeRasterState
    {
        /// <summary>현재 스트로크 색</summary>
        public Color32 color;

        /// <summary>현재 스트로크 두께(px)</summary>
        public float widthPx;

        /// <summary>이전 픽셀(라인 연결용)</summary>
        public Vector2Int lastPixel;

        /// <summary>이전 픽셀이 유효한지</summary>
        public bool hasLastPixel;
    }

    /// <summary>
    /// 진행 중인 스트로크 상태 맵.
    /// - key별로 lastPixel을 분리해서 동시 입력이 섞이지 않게 한다.
    /// </summary>
    private readonly Dictionary<OverlayStrokeKey, StrokeRasterState> activeStrokes = new();

    /// <summary>준비 완료 여부</summary>
    public bool IsReady =>
        active &&
        tex != null &&
        buffer != null &&
        targetRawImage != null &&
        targetRect != null &&
        rootCanvas != null;

    private void Awake()
    {
        EnsureTexture();
    }

    private void Update()
    {
        if (!active) return;

        EnsureTexture();

        // Bulk 모드에서는 자동 Apply를 하지 않는다.
        // (스냅샷 재생 같은 대량 입력 중 성능 보호)
        if (bulkDepth > 0) return;

        // dirty 상태에서 일정 간격마다 Apply
        if (dirty && Time.unscaledTime >= nextApplyTime)
        {
            ApplyTextureNow();
        }
    }

    /// <summary>
    /// 활성/비활성 토글.
    /// </summary>
    public void SetActive(bool active)
    {
        this.active = active;

        if (targetRawImage != null)
            targetRawImage.enabled = active;

        if (active)
            EnsureTexture();
    }

    /// <summary>
    /// Screen 좌표 -> 정규화(0~1) 좌표 변환.
    /// </summary>
    public bool TryScreenToNormalized(Vector2 screenPos, out Vector2 normalized)
    {
        normalized = default;

        if (!active || targetRect == null || rootCanvas == null)
            return false;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                targetRect, screenPos, rootCanvas.worldCamera, out var local))
            return false;

        var rect = targetRect.rect;

        float u = (local.x - rect.xMin) / rect.width;
        float v = (local.y - rect.yMin) / rect.height;

        if (u < 0f || u > 1f || v < 0f || v > 1f)
            return false;

        normalized = new Vector2(u, v);
        return true;
    }

    // --------------------------------------------------------------------
    // IOverlayAnnotator (Bulk)
    // --------------------------------------------------------------------

    /// <summary>
    /// 대량 적용 모드 시작.
    /// - Bulk 중에는 Update에서 Apply를 하지 않는다.
    /// - EndBulk에서 한 번만 Apply하여 비용을 줄인다.
    /// </summary>
    public void BeginBulk()
    {
        bulkDepth++;
    }

    /// <summary>
    /// 대량 적용 모드 종료.
    /// - BulkDepth가 0이 되면, 누적된 dirty 내용을 한 번에 Apply한다.
    /// </summary>
    public void EndBulk()
    {
        bulkDepth = Mathf.Max(0, bulkDepth - 1);

        if (bulkDepth == 0 && dirty)
        {
            // EndBulk에서는 즉시 Apply(대량 재생이 끝난 타이밍이므로)
            ApplyTextureNow();
        }
    }

    // --------------------------------------------------------------------
    // IOverlayAnnotator (Stroke)
    // --------------------------------------------------------------------

    /// <summary>
    /// key 기반 스트로크 시작.
    /// - 기존의 전역 isStrokeOpen 대신, key마다 상태를 저장한다.
    /// </summary>
    public void BeginStroke(OverlayStrokeKey key, Color32 color, float widthPx)
    {
        if (!IsReady) return;

        activeStrokes[key] = new StrokeRasterState
        {
            color = color,
            widthPx = widthPx,
            lastPixel = default,
            hasLastPixel = false
        };
    }

    /// <summary>
    /// 여러 점을 한 번에 추가(배치 처리).
    /// 
    /// 왜 IReadOnlyList로 받나?
    /// - 네트워크 청킹/배치에서 한 번에 여러 포인트가 도착한다.
    /// - 포인트마다 함수 호출하는 것보다, 한 번에 처리하는 편이 오버헤드가 줄어든다.
    /// </summary>
    public void AddPoints(OverlayStrokeKey key, IReadOnlyList<Vector2> normPoints)
    {
        if (!IsReady) return;
        if (normPoints == null || normPoints.Count == 0) return;

        if (!activeStrokes.TryGetValue(key, out var st))
            return;

        int radius = Mathf.Max(1, Mathf.RoundToInt((st.widthPx * resolutionScale) * 0.5f));

        for (int i = 0; i < normPoints.Count; i++)
        {
            var p = NormalizedToPixel(normPoints[i]);

            if (!st.hasLastPixel)
            {
                // 첫 점은 원으로 찍기
                DrawCircle(p, radius, st.color);
                st.lastPixel = p;
                st.hasLastPixel = true;
            }
            else
            {
                // 이후 점은 라인으로 연결
                DrawLine(st.lastPixel, p, radius, st.color);
                st.lastPixel = p;
            }
        }

        activeStrokes[key] = st;
        MarkDirty();
    }

    /// <summary>
    /// key 기반 스트로크 종료.
    /// </summary>
    public void EndStroke(OverlayStrokeKey key)
    {
        activeStrokes.Remove(key);
    }

    // --------------------------------------------------------------------
    // IOverlayAnnotator (Label / Clear)
    // --------------------------------------------------------------------

    /// <summary>
    /// 라벨 추가.
    /// - 텍스처에 글자를 굽지 않고, UI(TextMeshProUGUI)로 올리는 방식.
    /// 
    /// labelId를 받는 이유:
    /// - 네트워크에서 라벨을 고유하게 식별하려면 ID가 필요하다.
    /// - 추후 수정/삭제 기능 확장 시 labelId가 키로 쓰인다.
    /// </summary>
    public void AddLabel(int labelId, Vector2 normPos, string text)
    {
        if (!active || targetRect == null) return;

        // 간단 구현: labelId로 이름 지정(추후 갱신/삭제에 활용 가능)
        var go = new GameObject($"OverlayLabel_{labelId}", typeof(RectTransform));
        go.transform.SetParent(targetRect, false);

        var t = go.AddComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = 32;
        t.color = Color.white;

        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0, 0);
        rt.pivot = new Vector2(0.5f, 0.5f);

        var rect = targetRect.rect;
        rt.anchoredPosition = new Vector2(
            Mathf.Lerp(rect.xMin, rect.xMax, normPos.x),
            Mathf.Lerp(rect.yMin, rect.yMax, normPos.y)
        );

        rt.sizeDelta = new Vector2(400, 80);
    }

    /// <summary>
    /// 전체 삭제.
    /// - 텍스처 버퍼를 clearColor로 채움.
    /// - 진행 중 스트로크 상태도 모두 비움.
    /// </summary>
    public void ClearAll()
    {
        EnsureTexture();
        if (buffer == null) return;

        activeStrokes.Clear();

        // 라벨도 함께 제거(원하면 라벨 유지 정책으로 바꿀 수 있음)
        ClearAllLabels();

        var c = (Color32)clearColor;
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = c;

        tex.SetPixels32(buffer);
        tex.Apply(false, false);

        dirty = false;
        nextApplyTime = Time.unscaledTime + applyInterval;
    }

    private void ClearAllLabels()
    {
        if (targetRect == null) return;

        // OverlayLabel_ 로 시작하는 것만 제거(다른 UI 요소 보호)
        for (int i = targetRect.childCount - 1; i >= 0; i--)
        {
            var child = targetRect.GetChild(i);
            if (child != null && child.name.StartsWith("OverlayLabel_"))
                Destroy(child.gameObject);
        }
    }

    // --------------------------------------------------------------------
    // Texture creation / apply
    // --------------------------------------------------------------------

    /// <summary>
    /// UI 크기에 맞춰 텍스처를 생성/재생성.
    /// </summary>
    private void EnsureTexture()
    {
        if (targetRect == null || targetRawImage == null) return;

        var size = GetTargetRectSizePx();
        if (tex != null && size == lastRectSize) return;

        lastRectSize = size;

        texW = Mathf.Max(8, Mathf.RoundToInt(size.x * resolutionScale));
        texH = Mathf.Max(8, Mathf.RoundToInt(size.y * resolutionScale));

        tex = new Texture2D(texW, texH, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        buffer = new Color32[texW * texH];
        targetRawImage.texture = tex;

        ClearAll();
    }

    /// <summary>
    /// targetRect의 실제 픽셀 크기(최소 1 보장).
    /// - 예전엔 GetRenderSizePx()를 인터페이스로 노출했지만,
    ///   지금은 내부에서만 쓰면 되므로 private로 둔다.
    /// </summary>
    private Vector2 GetTargetRectSizePx()
    {
        if (targetRect == null) return Vector2.one;
        var r = targetRect.rect;
        return new Vector2(Mathf.Max(1f, r.width), Mathf.Max(1f, r.height));
    }

    /// <summary>
    /// dirty 상태 표시.
    /// - Update에서 일정 간격마다 Apply하도록 함.
    /// - Bulk 모드라면 EndBulk에서 한 번만 Apply.
    /// </summary>
    private void MarkDirty()
    {
        dirty = true;

        // Bulk 중엔 Apply를 미루고, 평상시엔 Update에서 applyInterval마다 처리
        if (bulkDepth > 0) return;

        // 너무 오래 기다리면 UX가 답답할 수 있어,
        // sendInterval과 맞춰서 필요하면 여기서 "즉시 Apply" 정책으로 바꿀 수도 있음.
    }

    /// <summary>
    /// 즉시 텍스처에 적용.
    /// - Apply 비용이 크므로 호출 빈도를 제어해야 한다.
    /// </summary>
    private void ApplyTextureNow()
    {
        if (tex == null || buffer == null) return;

        tex.SetPixels32(buffer);
        tex.Apply(false, false);

        dirty = false;
        nextApplyTime = Time.unscaledTime + applyInterval;
    }

    // --------------------------------------------------------------------
    // Raster helpers
    // --------------------------------------------------------------------

    /// <summary>
    /// 정규화(0~1) 좌표 -> 텍스처 픽셀 좌표.
    /// </summary>
    private Vector2Int NormalizedToPixel(Vector2 norm)
    {
        int x = Mathf.Clamp(Mathf.RoundToInt(norm.x * (texW - 1)), 0, texW - 1);
        int y = Mathf.Clamp(Mathf.RoundToInt(norm.y * (texH - 1)), 0, texH - 1);
        return new Vector2Int(x, y);
    }

    /// <summary>
    /// Bresenham 기반 라인 + 원 브러시로 두께 적용.
    /// </summary>
    private void DrawLine(Vector2Int a, Vector2Int b, int radius, Color32 col)
    {
        int x0 = a.x, y0 = a.y;
        int x1 = b.x, y1 = b.y;

        int dx = Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            DrawCircle(new Vector2Int(x0, y0), radius, col);
            if (x0 == x1 && y0 == y1) break;

            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>
    /// 원 브러시로 픽셀 버퍼에 색을 찍는다.
    /// </summary>
    private void DrawCircle(Vector2Int center, int r, Color32 col)
    {
        int cx = center.x, cy = center.y;
        int r2 = r * r;

        for (int y = -r; y <= r; y++)
        {
            int py = cy + y;
            if (py < 0 || py >= texH) continue;

            for (int x = -r; x <= r; x++)
            {
                if (x * x + y * y > r2) continue;

                int px = cx + x;
                if (px < 0 || px >= texW) continue;

                int idx = py * texW + px;
                buffer[idx] = col;
            }
        }
    }
}
