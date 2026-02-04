using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// uGUI(RawImage) 위에 Texture2D를 생성하고 픽셀 버퍼에 선을 그리는 Annotator.
/// 
/// 기존 문제:
/// - isStrokeOpen/lastPixel을 "전역 1개"로만 관리 → 동시에 여러 스트로크가 들어오면 섞임.
/// 
/// 해결:
/// - key(OverlayStrokeKey)별로 lastPixel/hasLastPixel/색/두께를 Dictionary로 관리.
/// - AddStrokePoint도 key를 받아서 해당 스트로크 상태만 갱신.
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
    private bool active;

    /// <summary>UI 크기 변경 감지용</summary>
    private Vector2 lastRectSize;

    /// <summary>다음 Apply 가능한 시간</summary>
    private float nextApplyTime;

    /// <summary>버퍼 변경 여부</summary>
    private bool dirty;

    /// <summary>
    /// 스트로크별 상태(동시 스트로크 지원).
    /// </summary>
    private struct StrokeRasterState
    {
        /// <summary>현재 스트로크 색</summary>
        public Color color;

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
    public bool IsReady => active && tex != null && targetRawImage != null && targetRect != null && rootCanvas != null;

    private void Awake()
    {
        EnsureTexture();
    }

    private void Update()
    {
        if (!active) return;

        EnsureTexture();

        // dirty 상태에서 일정 간격마다 Apply
        if (dirty && Time.unscaledTime >= nextApplyTime)
        {
            tex.SetPixels32(buffer);
            tex.Apply(false, false);
            dirty = false;
            nextApplyTime = Time.unscaledTime + applyInterval;
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

    /// <summary>
    /// 렌더링 기준 픽셀 크기.
    /// </summary>
    public Vector2 GetRenderSizePx()
    {
        if (targetRect == null) return Vector2.one;
        var r = targetRect.rect;
        return new Vector2(Mathf.Max(1f, r.width), Mathf.Max(1f, r.height));
    }

    /// <summary>
    /// key 기반 스트로크 시작.
    /// - 기존의 전역 isStrokeOpen 대신, key마다 상태를 저장한다.
    /// </summary>
    public void BeginStroke(OverlayStrokeKey key, Color color, float widthPx)
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
    /// key 기반 점 추가.
    /// - 해당 key 스트로크의 lastPixel만 갱신하므로 동시 스트로크가 섞이지 않는다.
    /// </summary>
    public void AddStrokePoint(OverlayStrokeKey key, Vector2 normalized)
    {
        if (!IsReady) return;

        if (!activeStrokes.TryGetValue(key, out var st))
            return;

        var p = NormalizedToPixel(normalized);

        int radius = Mathf.Max(1, Mathf.RoundToInt((st.widthPx * resolutionScale) * 0.5f));

        if (!st.hasLastPixel)
        {
            DrawCircle(p, radius, st.color);
            st.lastPixel = p;
            st.hasLastPixel = true;
            activeStrokes[key] = st;

            MarkDirty();
            return;
        }

        DrawLine(st.lastPixel, p, radius, st.color);

        st.lastPixel = p;
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

    /// <summary>
    /// 텍스트 추가.
    /// - 텍스처에 직접 글자를 굽는 대신 UI 오브젝트로 띄우는 방식(샘플).
    /// </summary>
    public void AddText(Vector2 normalized, string text)
    {
        if (!active || targetRect == null) return;

        var go = new GameObject("OverlayText", typeof(RectTransform));
        go.transform.SetParent(targetRect, false);

        var t = go.AddComponent<TMPro.TextMeshProUGUI>();
        t.text = text;
        t.fontSize = 32;
        t.color = Color.white;

        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0, 0);
        rt.pivot = new Vector2(0.5f, 0.5f);

        var rect = targetRect.rect;
        rt.anchoredPosition = new Vector2(
            Mathf.Lerp(rect.xMin, rect.xMax, normalized.x),
            Mathf.Lerp(rect.yMin, rect.yMax, normalized.y)
        );

        rt.sizeDelta = new Vector2(400, 80);
    }

    /// <summary>
    /// 전체 삭제.
    /// - 텍스처 버퍼를 clearColor로 채움.
    /// - activeStrokes도 모두 비움.
    /// </summary>
    public void Clear()
    {
        EnsureTexture();
        if (buffer == null) return;

        activeStrokes.Clear();

        var c = (Color32)clearColor;
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = c;

        tex.SetPixels32(buffer);
        tex.Apply(false, false);

        dirty = false;
        nextApplyTime = Time.unscaledTime + applyInterval;
    }

    /// <summary>
    /// UI 크기에 맞춰 텍스처를 생성/재생성.
    /// </summary>
    private void EnsureTexture()
    {
        if (targetRect == null || targetRawImage == null) return;

        var size = GetRenderSizePx();
        if (tex != null && size == lastRectSize) return;

        lastRectSize = size;

        texW = Mathf.Max(8, Mathf.RoundToInt(size.x * resolutionScale));
        texH = Mathf.Max(8, Mathf.RoundToInt(size.y * resolutionScale));

        tex = new Texture2D(texW, texH, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        buffer = new Color32[texW * texH];
        targetRawImage.texture = tex;

        Clear();
    }

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
    /// 버퍼 변경 표시.
    /// - Update에서 일정 주기마다 Apply하도록 함.
    /// </summary>
    private void MarkDirty()
    {
        dirty = true;
    }

    /// <summary>
    /// Bresenham 기반 라인 + 원 브러시로 두께 적용.
    /// </summary>
    private void DrawLine(Vector2Int a, Vector2Int b, int radius, Color col)
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
    private void DrawCircle(Vector2Int center, int r, Color col)
    {
        int cx = center.x, cy = center.y;
        int r2 = r * r;
        var c = (Color32)col;

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
                buffer[idx] = c;
            }
        }
    }
}
