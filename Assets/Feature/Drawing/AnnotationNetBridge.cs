using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 로컬 입력으로 생성된 드로잉 데이터를 네트워크(AnnotationHub RPC)로 전송하는 브리지.
/// 
/// 변경점:
/// - NetBeginStroke가 "할당된 strokeId"를 반환하도록 수정.
///   - OverlayAnnotatorController가 로컬 스트로크 키를 만들 때 이 값을 사용.
/// 
/// 참고:
/// - 이 브리지는 "내가 그리고 있는 스트로크는 1개"라는 가정이 있다.
///   - 로컬 입력은 동시에 2개 스트로크를 그릴 일이 없으므로 문제 없다.
/// - 동시 사용자 처리는 AnnotationHub에서 (Source, strokeId)로 분리해서 해결한다.
/// </summary>
public class AnnotationNetBridge : MonoBehaviour
{
    /// <summary>
    /// 네트워크 허브(NetworkBehaviour).
    /// - 런타임에 AnnotationHub.Instance로도 찾아올 수 있음.
    /// </summary>
    [SerializeField] private AnnotationHub hub;

    [Header("Batching")]

    /// <summary>포인트 전송 주기(초)</summary>
    [SerializeField] private float sendInterval = 0.05f;

    /// <summary>RPC 1회에 담을 최대 포인트 수</summary>
    [SerializeField] private int maxPointsPerRpc = 64;

    /// <summary>정규화 좌표 기준 최소 거리(너무 촘촘한 포인트 제거)</summary>
    [SerializeField] private float minDistanceNorm = 0.002f;

    /// <summary>허브 준비 여부</summary>
    private bool netReady;

    /// <summary>허브 준비를 기다리는 코루틴</summary>
    private Coroutine ensureRoutine;

    /// <summary>로컬 스트로크 시퀀스(전송용 strokeId)</summary>
    private int strokeSeq = 1;

    /// <summary>라벨 시퀀스</summary>
    private int labelSeq = 1;

    /// <summary>현재 전송 중인 스트로크ID</summary>
    private int currentStrokeId;

    /// <summary>현재 스트로크가 열려 있는지</summary>
    private bool strokeOpen;

    /// <summary>전송 대기 중인 포인트 목록</summary>
    private readonly List<Vector2> pending = new();

    /// <summary>마지막으로 수용한 포인트(거리 필터링용)</summary>
    private Vector2 lastAccepted;

    /// <summary>lastAccepted 유효 여부</summary>
    private bool hasLast;

    /// <summary>다음 전송 가능한 시간</summary>
    private float nextSendTime;

    private void Awake()
    {
        if (ensureRoutine == null)
            ensureRoutine = StartCoroutine(EnsureHubReady());
    }

    private void OnEnable()
    {
        if (ensureRoutine == null)
            ensureRoutine = StartCoroutine(EnsureHubReady());
    }

    private void OnDisable()
    {
        if (ensureRoutine != null)
        {
            StopCoroutine(ensureRoutine);
            ensureRoutine = null;
        }

        netReady = false;
    }

    /// <summary>
    /// AnnotationHub가 생성(Spawned)되어 네트워크 준비 상태가 될 때까지 대기.
    /// </summary>
    private IEnumerator EnsureHubReady()
    {
        // 1) hub 참조 확보
        while (hub == null)
        {
            hub = AnnotationHub.Instance;
            yield return null;
        }

        // 2) 네트워크 초기화 완료까지 대기
        while (!hub.IsNetworkReady)
            yield return null;

        netReady = true;
        Debug.Log("[NetBridge] Hub is network-ready.");

        ensureRoutine = null;
    }

    /// <summary>
    /// 전송 가능한 상태인지 검사.
    /// </summary>
    private bool CanSend()
        => netReady && hub != null && hub.IsNetworkReady;

    /// <summary>
    /// 네트워크 스트로크 시작.
    /// 
    /// 반환값:
    /// - 전송 성공: 할당된 strokeId(1,2,3...)
    /// - 전송 실패(허브 준비 전): -1
    /// 
    /// Controller는 이 반환값을 로컬 스트로크 키에 사용한다.
    /// </summary>
    public int NetBeginStroke(Color color, float widthPx, Vector2 firstNorm)
    {
        if (!CanSend())
        {
            Debug.LogWarning("[NetBridge] Hub not ready yet. Skip sending BeginStroke.");
            return -1;
        }

        currentStrokeId = strokeSeq++;
        strokeOpen = true;

        // Begin 전송
        hub.SendBeginStroke(currentStrokeId, (Color32)color, widthPx);

        // 포인트 버퍼 초기화
        pending.Clear();
        hasLast = false;

        // 첫 포인트 수용 및 즉시 플러시(지연 없이 시작점을 공유)
        AcceptPoint(firstNorm);
        Flush(force: true);

        return currentStrokeId;
    }

    /// <summary>
    /// 포인트 추가 전송(배치).
    /// </summary>
    public void NetAddPoint(Vector2 norm)
    {
        if (!strokeOpen) return;
        if (!CanSend()) return;

        AcceptPoint(norm);
        Flush(force: false);
    }

    /// <summary>
    /// 스트로크 종료 전송.
    /// - 남은 포인트를 강제 플러시 후 End 전송.
    /// </summary>
    public void NetEndStroke()
    {
        if (!strokeOpen) return;
        if (!CanSend()) return;

        Flush(force: true);
        hub.SendEndStroke(currentStrokeId);

        strokeOpen = false;
        pending.Clear();
        hasLast = false;
    }

    /// <summary>
    /// 텍스트 추가 전송.
    /// </summary>
    public void NetAddLabel(Vector2 posNorm, string text)
    {
        if (!CanSend()) return;
        hub.SendAddLabel(labelSeq++, posNorm, text);
    }

    /// <summary>
    /// 전체 클리어 전송.
    /// </summary>
    public void NetClear()
    {
        if (!CanSend()) return;
        hub.SendClear();
    }

    /// <summary>
    /// 거리 필터링 적용 후 pending에 포인트 추가.
    /// </summary>
    private void AcceptPoint(Vector2 norm)
    {
        if (hasLast && (norm - lastAccepted).sqrMagnitude < (minDistanceNorm * minDistanceNorm))
            return;

        pending.Add(norm);
        lastAccepted = norm;
        hasLast = true;
    }

    /// <summary>
    /// pending 포인트를 maxPointsPerRpc 단위로 쪼개 RPC 전송.
    /// </summary>
    private void Flush(bool force)
    {
        if (pending.Count == 0) return;

        if (!force && Time.unscaledTime < nextSendTime)
            return;

        StrokeNetEncoder.ForEachChunk(pending, maxPointsPerRpc, (start, count) =>
        {
            var temp = new Vector2[count];
            for (int i = 0; i < count; i++) temp[i] = pending[start + i];

            var packed = StrokeNetEncoder.PackPoints(temp);
            hub.SendAddPointsChunk(currentStrokeId, packed);
        });

        pending.Clear();
        nextSendTime = Time.unscaledTime + sendInterval;
    }
}
