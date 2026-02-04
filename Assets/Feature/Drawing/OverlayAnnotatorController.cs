using UnityEngine;

/// <summary>
/// 마우스 입력으로 드로잉/텍스트/클리어를 수행하는 컨트롤러.
/// 
/// 변경점:
/// - 로컬에서도 OverlayStrokeKey 기반으로 스트로크를 관리.
/// - net.NetBeginStroke()가 반환한 strokeId를 로컬 스트로크 키에 사용해서
///   로컬 렌더와 네트워크 스트로크 번호를 맞춘다.
/// </summary>
public class OverlayAnnotatorController : MonoBehaviour
{
    /// <summary>
    /// 사용하고 싶은 Annotator 종류 선택.
    /// </summary>
    public enum Mode
    {
        Texture2D,
        UIToolkit
    }

    [Header("Mode")]

    /// <summary>현재 모드</summary>
    [SerializeField] private Mode mode = Mode.Texture2D;

    [Header("Annotators")]

    /// <summary>Texture2DOverlayAnnotator를 할당</summary>
    [SerializeField] private MonoBehaviour texture2DAnnotator;

    /// <summary>UIToolkitOverlayAnnotator를 할당</summary>
    [SerializeField] private MonoBehaviour uiToolkitAnnotator;

    [Header("Brush")]

    /// <summary>선 색상</summary>
    [SerializeField] private Color strokeColor = Color.red;

    /// <summary>선 두께(px)</summary>
    [SerializeField] private float strokeWidthPx = 4f;

    /// <summary>포인트 샘플링 최소 간격(px)</summary>
    [SerializeField] private float minDistancePx = 3f;

    [Header("Text")]

    /// <summary>우클릭으로 찍을 텍스트 샘플</summary>
    [SerializeField] private string sampleText = "여기 확인";

    [Header("NetWork")]

    /// <summary>
    /// 네트워크 전송 브리지.
    /// - 없으면 로컬에서만 그린다.
    /// </summary>
    [SerializeField] private AnnotationNetBridge net;

    /// <summary>
    /// 현재 활성화된 annotator.
    /// </summary>
    private IOverlayAnnotator active;

    /// <summary>현재 드래그 중인지</summary>
    private bool isDrawing;

    /// <summary>마지막으로 찍은 정규화 좌표</summary>
    private Vector2 lastNorm;

    /// <summary>정규화 거리 기준의 minDistance 제곱값</summary>
    private float minDistanceNormSqr;

    /// <summary>
    /// 로컬 스트로크 작성자 ID.
    /// - 네트워크에서 받은 authorId는 PlayerRef.GetHashCode()로 생성되므로,
    ///   로컬은 충돌 가능성이 매우 낮은 값(-1)을 사용.
    /// </summary>
    private const int LocalAuthorId = -1;

    /// <summary>
    /// 로컬 스트로크 시퀀스.
    /// - 네트워크가 없거나 NetBeginStroke가 실패했을 때만 사용.
    /// </summary>
    private int localStrokeSeq = 1;

    /// <summary>
    /// 현재 그리고 있는 스트로크의 키.
    /// - Add/End 시 이 키를 사용해야 다른 스트로크와 절대 섞이지 않는다.
    /// </summary>
    private OverlayStrokeKey currentStrokeKey;

    private void Awake()
    {
        Switch(mode);
    }

    private void OnValidate()
    {
        // 에디터에서 모드 바꾸면 플레이 중에도 즉시 반영.
        if (Application.isPlaying)
            Switch(mode);
    }

    /// <summary>
    /// 모드 전환.
    /// - 기존 annotator 비활성화
    /// - 새로운 annotator 활성화
    /// - minDistanceNorm 재계산
    /// </summary>
    public void Switch(Mode newMode)
    {
        mode = newMode;

        var a = texture2DAnnotator as IOverlayAnnotator;
        var b = uiToolkitAnnotator as IOverlayAnnotator;

        if (a != null) a.SetActive(false);
        if (b != null) b.SetActive(false);

        active = (mode == Mode.Texture2D) ? a : b;

        if (active != null)
            active.SetActive(true);

        RecomputeMinDistanceNorm();
    }

    private void Update()
    {
        if (active == null || !active.IsReady)
            return;

        // 렌더 크기가 바뀔 수 있으니 주기적으로 minDistanceNorm 갱신
        if (Time.frameCount % 15 == 0)
            RecomputeMinDistanceNorm();

        // 좌클릭 드로잉
        if (Input.GetMouseButtonDown(0))
        {
            if (active.TryScreenToNormalized(Input.mousePosition, out var norm))
            {
                isDrawing = true;
                lastNorm = norm;

                BeginLocalStroke(norm);
            }
        }
        else if (Input.GetMouseButton(0) && isDrawing)
        {
            if (active.TryScreenToNormalized(Input.mousePosition, out var norm))
            {
                if ((norm - lastNorm).sqrMagnitude >= minDistanceNormSqr)
                {
                    lastNorm = norm;
                    AddLocalPoint(norm);
                }
            }
        }
        else if (Input.GetMouseButtonUp(0) && isDrawing)
        {
            isDrawing = false;
            EndLocalStroke();
        }

        // 우클릭 텍스트
        if (Input.GetMouseButtonDown(1))
        {
            if (active.TryScreenToNormalized(Input.mousePosition, out var norm))
            {
                AddLocalLabel(norm, sampleText);
            }
        }

        // C 키로 전체 클리어
        if (Input.GetKeyDown(KeyCode.C))
        {
            ClearAll();
        }
    }

    /// <summary>
    /// 픽셀 기준 minDistancePx를 정규화 거리로 변환.
    /// - 화면 크기에 따라 정규화 거리 임계값을 조정해야 동일한 느낌이 나온다.
    /// </summary>
    private void RecomputeMinDistanceNorm()
    {
        var size = active?.GetRenderSizePx() ?? Vector2.one;
        float w = Mathf.Max(1f, size.x);
        float h = Mathf.Max(1f, size.y);

        // 가로/세로 중 작은 축 기준으로 정규화 거리 계산
        float denom = Mathf.Min(w, h);
        float d = minDistancePx / denom;

        minDistanceNormSqr = d * d;
    }

    /// <summary>
    /// 로컬 스트로크 시작.
    /// - 네트워크가 준비되었다면 NetBeginStroke가 반환한 strokeId를 사용해서
    ///   로컬 스트로크 키와 네트워크 strokeId를 일치시킨다.
    /// </summary>
    private void BeginLocalStroke(Vector2 norm)
    {
        // 네트워크로 먼저 Begin을 시도해서 strokeId를 받아온다.
        // - 성공: 네트워크 strokeId 사용
        // - 실패: 로컬 strokeSeq 사용
        int netStrokeId = (net != null) ? net.NetBeginStroke(strokeColor, strokeWidthPx, norm) : -1;

        int strokeId = (netStrokeId > 0) ? netStrokeId : localStrokeSeq++;
        currentStrokeKey = new OverlayStrokeKey(LocalAuthorId, strokeId);

        // 로컬 렌더 시작
        active.BeginStroke(currentStrokeKey, strokeColor, strokeWidthPx);
        active.AddStrokePoint(currentStrokeKey, norm);
    }

    /// <summary>
    /// 로컬 포인트 추가.
    /// </summary>
    private void AddLocalPoint(Vector2 norm)
    {
        active.AddStrokePoint(currentStrokeKey, norm);
        net?.NetAddPoint(norm);
    }

    /// <summary>
    /// 로컬 스트로크 종료.
    /// </summary>
    private void EndLocalStroke()
    {
        active.EndStroke(currentStrokeKey);
        net?.NetEndStroke();
    }

    /// <summary>
    /// 로컬 텍스트 추가.
    /// </summary>
    private void AddLocalLabel(Vector2 norm, string text)
    {
        active.AddText(norm, text);
        net?.NetAddLabel(norm, text);
    }

    /// <summary>
    /// 로컬 + 네트워크 전체 클리어.
    /// </summary>
    private void ClearAll()
    {
        active.Clear();
        net?.NetClear();
    }
}
