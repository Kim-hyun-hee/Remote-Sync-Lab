using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 로컬 입력을 네트워크 이벤트로 변환하는 브리지.
/// 
/// 여기서 하는 일:
/// - 스트로크 시작/포인트/종료/라벨/클리어를 "Hub.SendXXX"로 전달
/// - 포인트를 배치로 묶어서 전송 (sendInterval)
/// - 포인트 다운샘플링(minDistanceNorm)
/// - payload 크기 제한을 고려해 청킹(maxBytesPerRpc)
/// - 그리고 이번 요청의 핵심: "seq 시스템" 생성/전달
/// 
/// seq 시스템:
/// - 브리지는 "내가 보내는 이벤트"마다 seq를 1씩 증가시킨다.
/// - Hub는 RPC에 seq를 포함해 뿌린다.
/// - 수신 측은 authorId별 lastSeq를 저장하고 seq가 작거나 같으면 무시한다.
/// </summary>
public class AnnotationNetBridgeOptimized : MonoBehaviour
{
    [Header("Batching / Chunking")]
    [SerializeField] private float sendInterval = 0.05f;

    [Tooltip("RPC 1회 payload 안전 예산(바이트). 700~1100 정도에서 프로젝트에 맞게 튜닝.")]
    [SerializeField] private int maxBytesPerRpc = 900;

    [Tooltip("너무 촘촘한 포인트 제거(정규화 좌표 기준 거리).")]
    [SerializeField] private float minDistanceNorm = 0.002f;

    private AnnotationHubOptimized hub;
    private bool netReady;
    private Coroutine ensureRoutine;

    private int strokeSeq = 1;
    private int labelSeq = 1;

    private int currentStrokeId;
    private bool strokeOpen;

    // pending points (norm)
    private readonly List<Vector2> pending = new(256);
    private Vector2 lastAccepted;
    private bool hasLast;
    private float nextSendTime;

    // -------------------- seq 시스템(송신자 측) --------------------

    /// <summary>
    /// 내가 보내는 라이브 이벤트의 전역 seq 카운터.
    /// 
    /// 왜 전역 1개가 좋은가?
    /// - Begin/Points/End/Label/Clear의 순서를 한 줄로 세울 수 있다.
    /// - "End가 먼저 도착" 같은 역순이 오면 seq로 필터링 가능.
    /// - 구현이 단순하고 효과가 크다.
    /// </summary>
    private uint localEventSeq = 0;

    /// <summary>
    /// 이벤트 하나를 보낼 때마다 증가시키고, 그 값을 이벤트의 seq로 사용한다.
    /// </summary>
    private uint NextSeq() => ++localEventSeq;

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

        hub = null;
        netReady = false;
    }

    private IEnumerator EnsureHubReadyLoop()
    {
        while (true)
        {
            if (hub == null)
                hub = AnnotationHubOptimized.Instance;

            bool readyNow = hub != null && hub.IsNetworkReady;
            if (readyNow && !netReady)
            {
                netReady = true;
                Debug.Log("[NetBridge] Hub ready.");
            }
            else if (!readyNow && netReady)
            {
                netReady = false;
                Debug.LogWarning("[NetBridge] Hub not ready.");
            }

            yield return null;
        }
    }

    private bool CanSend() => netReady && hub != null && hub.IsNetworkReady;

    /// <summary>
    /// 네트워크 스트로크 시작.
    /// - seq를 생성해 Begin 이벤트에 포함한다.
    /// </summary>
    public int NetBeginStroke(Color32 color, float widthPx, Vector2 firstNorm)
    {
        if (!CanSend()) return -1;

        currentStrokeId = strokeSeq++;
        strokeOpen = true;

        pending.Clear();
        hasLast = false;

        // Begin 이벤트 송신 (seq 포함)
        hub.SendBeginStroke(NextSeq(), currentStrokeId, color, widthPx);

        // 첫 포인트도 pending에 넣고 즉시 Flush(UX/정확도)
        AcceptPoint(firstNorm);
        Flush(force: true);

        return currentStrokeId;
    }

    /// <summary>
    /// 포인트 추가. (배치 전송)
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
    /// - 마지막 pending 포인트를 먼저 Flush하고 End 이벤트 송신.
    /// </summary>
    public void NetEndStroke()
    {
        if (!strokeOpen) return;
        if (!CanSend()) return;

        Flush(force: true);

        // End 이벤트 송신 (seq 포함)
        hub.SendEndStroke(NextSeq(), currentStrokeId);

        strokeOpen = false;
        pending.Clear();
        hasLast = false;
    }

    /// <summary>
    /// 라벨 추가(테스트/기능 확장용).
    /// </summary>
    public void NetAddLabel(Vector2 posNorm, string text)
    {
        if (!CanSend()) return;

        int labelId = labelSeq++;

        hub.SendAddLabel(NextSeq(), labelId, posNorm, text);
    }

    /// <summary>
    /// 전체 클리어.
    /// - Clear는 룸 전체 상태를 바꾸므로 seq 포함이 특히 중요하다.
    /// </summary>
    public void NetClearAll()
    {
        if (!CanSend()) return;

        strokeOpen = false;
        pending.Clear();
        hasLast = false;

        hub.SendClearAll(NextSeq());
    }

    // -------------------- 포인트 다운샘플링 --------------------

    /// <summary>
    /// 포인트를 너무 촘촘히 넣으면:
    /// - 네트워크 트래픽 증가
    /// - 수신 렌더 비용 증가
    /// - 히스토리 메모리 증가
    /// 
    /// 그래서 일정 거리 이하면 포인트를 버린다.
    /// 이 값(minDistanceNorm)은 해상도/감도에 맞춰 조절한다.
    /// </summary>
    private void AcceptPoint(Vector2 norm)
    {
        if (!hasLast)
        {
            pending.Add(norm);
            lastAccepted = norm;
            hasLast = true;
            return;
        }

        if ((norm - lastAccepted).sqrMagnitude < (minDistanceNorm * minDistanceNorm))
            return;

        pending.Add(norm);
        lastAccepted = norm;
    }

    // -------------------- 배치/청킹 전송 --------------------

    /// <summary>
    /// pending에 모인 포인트를 네트워크로 전송.
    /// 
    /// force=false:
    /// - sendInterval 주기로만 전송(배치)
    /// 
    /// force=true:
    /// - 즉시 전송(End 직전, Begin 직후 등)
    /// </summary>
    private void Flush(bool force)
    {
        if (!force)
        {
            if (Time.unscaledTime < nextSendTime) return;
            nextSendTime = Time.unscaledTime + sendInterval;
        }

        if (pending.Count <= 0) return;

        int total = pending.Count;

        // 포인트 묶음을 payload 바이트 예산 기준으로 청킹해서 여러 번 보낸다.
        StrokeNetEncoderOptimized.ForEachChunkByBytes(
            totalPoints: total,
            maxBytesPerChunk: maxBytesPerRpc,
            onChunk: (start, count) =>
            {
                var packed = StrokeNetEncoderOptimized.PackPoints(pending, start, count);

                // Points 이벤트도 seq 포함!
                // 주의: 청크가 여러 개면 청크마다 seq를 "각각" 부여해야 한다.
                // 왜냐면 청크 A,B가 뒤집혀서 도착할 수 있고,
                // 그걸 seq로 정렬/필터링하기 위해서다.
                hub.SendAddPoints(NextSeq(), currentStrokeId, packed);
            });

        pending.Clear();
        hasLast = false;
    }
}
