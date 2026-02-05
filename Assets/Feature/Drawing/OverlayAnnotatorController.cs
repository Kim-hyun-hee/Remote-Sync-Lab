using UnityEngine;

/// <summary>
/// "입력(마우스/키보드)"을 받아서:
/// - 로컬 렌더(화면에 즉시 그림)
/// - 네트워크 전송(다른 사람에게도 그림)
///
/// 을 동시에 수행하는 컨트롤러.
///
/// 이 컨트롤러는 "네트워크를 몰라도" 드로잉 자체는 가능해야 한다.
/// 그래서 net(AnnotationNetBridge)가 없거나 준비가 안 됐을 때는:
/// - 로컬에서만 그리도록(offline fallback) 설계되어 있다.
///
/// 핵심 개념 3가지:
/// 1) 정규화 좌표(normalized):
///    - 화면/패널의 픽셀 좌표를 0~1로 바꾼 값.
///    - UI 크기가 바뀌어도 동일한 상대 위치를 표현할 수 있다.
/// 2) 최소 거리 샘플링(minDistance):
///    - 마우스가 움직일 때 매 프레임 포인트를 찍으면 포인트 폭발.
///    - 일정 거리 이상 움직였을 때만 포인트를 추가해서 데이터량을 줄인다.
/// 3) StrokeKey 분리:
///    - 동시 드로잉에서 "전역 1개 스트로크"로 관리하면 선이 섞인다.
///    - (authorId, strokeId)로 스트로크를 분리해서 독립적으로 관리한다.
///    - 로컬 입력은 authorId=-1 같은 고정값을 써도 된다.
/// </summary>
public class OverlayAnnotatorController : MonoBehaviour
{
    /// <summary>
    /// 같은 컨트롤러로도 uGUI(Texture2D) / UI Toolkit(Vector) 둘 다 테스트할 수 있게 모드 제공.
    /// </summary>
    public enum Mode
    {
        Texture2D,
        UIToolkit
    }

    [Header("Mode")]
    [SerializeField] private Mode mode = Mode.Texture2D;

    [Header("Annotators")]
    [SerializeField] private MonoBehaviour texture2DAnnotator;
    [SerializeField] private MonoBehaviour uiToolkitAnnotator;

    [Header("Brush")]
    [SerializeField] private Color strokeColor = Color.red;

    /// <summary>
    /// 선 두께(px). 각 Annotator 구현체는 이를 "자기 방식대로" 해석한다.
    /// - Texture2D는 실제 픽셀 기반으로 래스터라이즈
    /// - Vector(UI Toolkit)는 painter2D lineWidth로 사용
    /// </summary>
    [SerializeField] private float strokeWidthPx = 4f;

    /// <summary>
    /// 포인트를 찍는 최소 거리(px).
    /// - 값이 작을수록 더 부드럽지만 데이터가 늘어난다.
    /// - 값이 클수록 데이터가 줄지만 각진 느낌이 날 수 있다.
    ///
    /// 내부적으로는 "정규화 거리"로 변환해서 비교한다.
    /// </summary>
    [SerializeField] private float minDistancePx = 3f;

    [Header("Text")]
    [SerializeField] private string sampleText = "여기 확인";

    [Header("NetWork")]
    [SerializeField] private AnnotationNetBridge net;

    /// <summary>
    /// 현재 활성화된 Annotator (mode에 따라 교체됨)
    /// </summary>
    private IOverlayAnnotator active;

    /// <summary>
    /// 현재 드래그 중인지(좌클릭 누르고 있는지)
    /// </summary>
    private bool isDrawing;

    /// <summary>
    /// 마지막으로 찍은 정규화 좌표(포인트 샘플링 기준)
    /// </summary>
    private Vector2 lastNorm;

    /// <summary>
    /// minDistancePx를 정규화 거리로 바꾼 뒤, 제곱 거리로 비교하기 위한 값.
    /// - sqrt를 피하고 성능을 위해 sqrMagnitude 사용.
    /// </summary>
    private float minDistanceNormSqr;

    /// <summary>
    /// 로컬 입력의 authorId.
    /// - 네트워크 입력(authorId=PlayerRef 해시 등)과 충돌하지 않게 음수로 둔다.
    /// </summary>
    private const int LocalAuthorId = -1;

    /// <summary>
    /// 네트워크 strokeId를 못 받는(offline) 경우를 위한 로컬 시퀀스.
    /// </summary>
    private int localStrokeSeq = 1;

    /// <summary>
    /// 현재 그리고 있는 스트로크의 키.
    /// - Add / End는 반드시 이 키로 호출해야 "다른 스트로크"와 섞이지 않는다.
    /// </summary>
    private OverlayStrokeKey currentStrokeKey;

    private void Awake()
    {
        // 시작 시 현재 mode에 맞는 annotator를 활성화
        Switch(mode);
    }

    private void OnValidate()
    {
        // 에디터에서 mode를 바꾸면, 플레이 중에도 즉시 반영
        if (Application.isPlaying)
            Switch(mode);
    }

    /// <summary>
    /// mode에 따라 실제 annotator 구현체를 교체한다.
    ///
    /// 동작:
    /// 1) 둘 다 비활성화(SetActive(false))
    /// 2) 선택된 쪽만 active로 설정 + SetActive(true)
    /// 3) minDistanceNorm 재계산
    /// </summary>
    public void Switch(Mode newMode)
    {
        mode = newMode;

        var a = texture2DAnnotator as IOverlayAnnotator;
        var b = uiToolkitAnnotator as IOverlayAnnotator;

        // 둘 다 꺼두고
        if (a != null) a.SetActive(false);
        if (b != null) b.SetActive(false);

        // 선택된 구현체를 active로
        active = (mode == Mode.Texture2D) ? a : b;

        if (active != null)
            active.SetActive(true);

        RecomputeMinDistanceNorm();
    }

    private void Update()
    {
        // Annotator가 준비되지 않았으면(패널 생성 전 등) 입력을 무시
        if (active == null || !active.IsReady)
            return;

        // UI 크기 변경 등에 대비해서 주기적으로 minDistanceNorm을 갱신
        if (Time.frameCount % 15 == 0)
            RecomputeMinDistanceNorm();

        // ===== 좌클릭 드로잉 =====
        if (Input.GetMouseButtonDown(0))
        {
            // 화면 좌표 → 정규화 좌표 변환이 성공했을 때만 시작
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
                // 일정 거리 이상 움직였을 때만 포인트 추가
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

        // ===== 우클릭 텍스트 =====
        if (Input.GetMouseButtonDown(1))
        {
            if (active.TryScreenToNormalized(Input.mousePosition, out var norm))
            {
                AddLocalLabel(norm, sampleText);
            }
        }

        // ===== 전체 클리어 =====
        // 학습/테스트 편의용 단축키
        if (Input.GetKeyDown(KeyCode.C))
        {
            ClearAll();
        }
    }

    /// <summary>
    /// 픽셀 단위 minDistancePx를 정규화 거리로 변환한다.
    ///
    /// 왜 변환하나?
    /// - 화면/패널 크기가 바뀌면, 같은 px 거리라도 "정규화 거리" 기준이 달라진다.
    /// - 정규화로 바꿔두면, 입력 처리(샘플링)가 UI 크기에 덜 민감해진다.
    ///
    /// 변환 기준:
    /// - (가로, 세로) 중 작은 축을 denom으로 사용(대략적인 스케일 일관성 목적)
    /// </summary>
    private void RecomputeMinDistanceNorm()
    {
        var size = active?.GetRenderSizePx() ?? Vector2.one;
        float w = Mathf.Max(1f, size.x);
        float h = Mathf.Max(1f, size.y);

        float denom = Mathf.Min(w, h);
        float d = minDistancePx / denom;

        minDistanceNormSqr = d * d;
    }

    /// <summary>
    /// 로컬 스트로크 시작.
    ///
    /// 중요한 흐름:
    /// - 네트워크가 준비되어 있으면 NetBeginStroke를 먼저 호출해서 strokeId를 받는다.
    ///   (이렇게 하면 "로컬 렌더 스트로크"와 "네트워크 스트로크"의 번호가 동일해져서 디버깅이 쉬움)
    /// - 네트워크가 안 되면 localStrokeSeq로 fallback.
    /// </summary>
    private void BeginLocalStroke(Vector2 norm)
    {
        // 네트워크로 먼저 Begin을 시도해서 strokeId를 받아온다.
        // - 성공: netStrokeId > 0
        // - 실패: -1 또는 0
        int netStrokeId = (net != null) ? net.NetBeginStroke(strokeColor, strokeWidthPx, norm) : -1;

        int strokeId = (netStrokeId > 0) ? netStrokeId : localStrokeSeq++;

        // 로컬 authorId는 -1, strokeId는 위에서 결정한 값
        currentStrokeKey = new OverlayStrokeKey(LocalAuthorId, strokeId);

        // 로컬 렌더 시작
        active.BeginStroke(currentStrokeKey, strokeColor, strokeWidthPx);
        active.AddStrokePoint(currentStrokeKey, norm);
    }

    /// <summary>
    /// 로컬 포인트 추가.
    /// - 로컬 렌더에 즉시 반영
    /// - 네트워크에도 포인트 전송(NetAddPoint)
    /// </summary>
    private void AddLocalPoint(Vector2 norm)
    {
        active.AddStrokePoint(currentStrokeKey, norm);
        net?.NetAddPoint(norm);
    }

    /// <summary>
    /// 로컬 스트로크 종료.
    /// - 로컬 렌더 종료
    /// - 네트워크에도 종료 전송(NetEndStroke)
    /// </summary>
    private void EndLocalStroke()
    {
        active.EndStroke(currentStrokeKey);
        net?.NetEndStroke();
    }

    /// <summary>
    /// 텍스트 라벨 추가.
    /// - 로컬에 표시
    /// - 네트워크에도 전송
    /// </summary>
    private void AddLocalLabel(Vector2 norm, string text)
    {
        active.AddText(norm, text);
        net?.NetAddLabel(norm, text);
    }

    /// <summary>
    /// 전체 클리어.
    /// - 로컬 클리어
    /// - 네트워크로도 클리어 요청(모든 사람에게 반영 + 히스토리 초기화)
    /// </summary>
    private void ClearAll()
    {
        active.Clear();
        net?.NetClear();
    }
}
