using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 로컬 입력(마우스/펜/터치)을 네트워크 드로잉 이벤트로 변환하여 AnnotationHub로 전달하는 브릿지.
///
/// ============================
/// 왜 Hub 참조를 인스펙터로 들고 있으면 안 되나?
/// ============================
/// Shared Mode에서 마스터가 나가고 오브젝트 권한/상태가 바뀌는 과정에서,
/// "SerializeField로 박아둔 Hub 참조"는 Missing으로 깨지는 케이스가 실제로 자주 나옵니다.
/// (특히 Hub를 Spawn 방식으로 운용하면 더 심각)
///
/// 그래서 브릿지는:
/// - hub를 SerializeField로 고정하지 않고
/// - AnnotationHub.Instance를 통해 항상 최신 Hub를 바라보게 합니다.
///
/// ============================
/// Batching / Chunking
/// ============================
/// 포인트는 매우 자주 발생하므로:
/// - 일정 시간(sendInterval)마다 묶어서 전송(Batching)
/// - RPC 1회당 최대 포인트 개수(maxPointsPerRpc)를 제한(Chunking)
///
/// 추가로, 너무 촘촘한 포인트는 네트워크 낭비가 되므로
/// minDistanceNorm로 거리 필터링을 합니다.
/// </summary>
public class AnnotationNetBridge : MonoBehaviour
{
    [Header("Batching")]

    /// <summary>포인트 전송 주기(초). 작을수록 더 실시간이지만 트래픽 증가.</summary>
    [SerializeField] private float sendInterval = 0.05f;

    /// <summary>RPC 1회당 최대 포인트 개수. Fusion RPC 페이로드 제한 방어용.</summary>
    [SerializeField] private int maxPointsPerRpc = 64;

    /// <summary>정규화 좌표 기준 최소 거리. 너무 촘촘한 포인트 전송 방지.</summary>
    [SerializeField] private float minDistanceNorm = 0.002f;

    /// <summary>현재 사용할 Hub(런타임에 자동으로 잡힘)</summary>
    private AnnotationHub hub;

    /// <summary>Hub 준비 여부</summary>
    private bool netReady;

    /// <summary>Hub를 계속 찾아 붙잡는 코루틴</summary>
    private Coroutine ensureRoutine;

    /// <summary>로컬에서 생성하는 strokeId 시퀀스 (각 로컬 플레이어 기준 1,2,3...)</summary>
    private int strokeSeq = 1;

    /// <summary>라벨 ID 시퀀스</summary>
    private int labelSeq = 1;

    /// <summary>현재 그리고 있는 스트로크 ID</summary>
    private int currentStrokeId;

    /// <summary>현재 스트로크가 열려 있는지(그리는 중인지)</summary>
    private bool strokeOpen;

    /// <summary>전송 대기 중인 포인트 버퍼(정규화 0~1)</summary>
    private readonly List<Vector2> pending = new();

    /// <summary>거리 필터링을 위한 마지막 채택 포인트</summary>
    private Vector2 lastAccepted;

    /// <summary>lastAccepted가 유효한지</summary>
    private bool hasLast;

    /// <summary>다음 전송 가능한 시각</summary>
    private float nextSendTime;

    private void OnEnable()
    {
        if (ensureRoutine == null)
            ensureRoutine = StartCoroutine(EnsureHubReadyLoop());
    }

    private void OnDisable()
    {
        if (ensureRoutine != null)
        {
            StopCoroutine(ensureRoutine);
            ensureRoutine = null;
        }

        netReady = false;
        hub = null;
    }

    /// <summary>
    /// Hub를 "계속" 찾고, 준비되면 netReady를 true로 만듭니다.
    ///
    /// 왜 루프인가?
    /// - 씬 로드 타이밍/네트워크 시작 타이밍에 따라 Spawned 순서가 달라질 수 있음
    /// - 마스터 변경/재접속 같은 이벤트로 Instance가 잠깐 null이 될 수 있음
    ///
    /// 따라서 "한 번 찾고 끝"이 아니라, 유효해질 때까지 반복하는 방식이 안전합니다.
    /// </summary>
    private IEnumerator EnsureHubReadyLoop()
    {
        while (true)
        {
            if (hub == null)
                hub = AnnotationHub.Instance;

            if (hub != null && hub.IsNetworkReady)
            {
                if (!netReady)
                {
                    netReady = true;
                    Debug.Log("[NetBridge] Hub is network-ready.");
                }
            }
            else
            {
                // 준비가 깨진 경우(Instance null 등)
                if (netReady)
                {
                    netReady = false;
                    Debug.LogWarning("[NetBridge] Hub is not ready (lost reference or not spawned).");
                }
            }

            yield return null;
        }
    }

    /// <summary>
    /// 지금 네트워크로 전송해도 되는지.
    /// </summary>
    private bool CanSend()
        => netReady && hub != null && hub.IsNetworkReady;

    /// <summary>
    /// 네트워크 스트로크 시작.
    ///
    /// 반환:
    /// - 성공: 할당된 strokeId (1,2,3...)
    /// - 실패: -1
    ///
    /// 주의:
    /// - strokeId는 "로컬 플레이어 기준" 증가값입니다.
    /// - 네트워크에서 유일성은 (author, strokeId) 조합으로 보장됩니다(AnnotationHub에서 처리).
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

        // 첫 포인트는 즉시 전송(사용자 체감 개선)
        AcceptPoint(firstNorm);
        Flush(force: true);

        return currentStrokeId;
    }

    /// <summary>
    /// 포인트 추가(그리는 중).
    /// </summary>
    public void NetAddPoint(Vector2 norm)
    {
        if (!strokeOpen) return;
        if (!CanSend()) return;

        AcceptPoint(norm);
        Flush(force: false);
    }

    /// <summary>
    /// 스트로크 종료.
    /// - 남은 포인트를 강제로 Flush하고 End를 보냅니다.
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
    /// 라벨 추가.
    /// </summary>
    public void NetAddLabel(Vector2 posNorm, string text)
    {
        if (!CanSend()) return;
        hub.SendAddLabel(labelSeq++, posNorm, text);
    }

    /// <summary>
    /// 전체 Clear.
    /// - 모든 클라이언트에 Clear + 히스토리 초기화
    /// </summary>
    public void NetClear()
    {
        if (!CanSend()) return;
        hub.SendClear();
    }

    /// <summary>
    /// 거리 필터링 후 pending에 포인트 추가.
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
    /// pending 포인트들을 전송합니다.
    ///
    /// - force=false면 sendInterval에 맞춰 주기적으로만 전송(Batching)
    /// - maxPointsPerRpc 단위로 나눠서 전송(Chunking)
    /// </summary>
    private void Flush(bool force)
    {
        if (pending.Count == 0) return;

        if (!force && Time.unscaledTime < nextSendTime)
            return;

        StrokeNetEncoder.ForEachChunk(pending, maxPointsPerRpc, (start, count) =>
        {
            var temp = new Vector2[count];
            for (int i = 0; i < count; i++)
                temp[i] = pending[start + i];

            var packed = StrokeNetEncoder.PackPoints(temp);
            hub.SendAddPointsChunk(currentStrokeId, packed);
        });

        pending.Clear();
        nextSendTime = Time.unscaledTime + sendInterval;
    }
}
