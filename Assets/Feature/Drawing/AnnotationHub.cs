using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

/// <summary>
/// 네트워크 드로잉 허브(최적화 + seq 재정렬 + Begin 이전 Points 보관).
///
/// 목표:
/// 1) 중복/역순/유실에 강하게(최대한 "그려지게") 만들기
/// 2) Addon 없이 Fusion 무료버전 RPC로 가능한 범위에서 안정성/성능 균형 잡기
///
/// 핵심 전략:
/// - per-author seq 재정렬 버퍼(SortedDictionary)로 out-of-order를 "순서대로 처리"
/// - stroke별 PendingStroke를 둬서 Begin보다 Points/End가 먼저 와도 "보관 후 재생"
/// - seq 갭이 오래 지속되면 타임아웃으로 스킵(버퍼가 영원히 멈추는 것 방지)
///
/// 주의:
/// - 완전한 신뢰성(ACK/재전송)까지는 구현하지 않는다(무료/간단 운영 목적).
/// - 대신 "유실되더라도 최대한 그려지고, Late Join 스냅샷으로 복구" 방향.
/// </summary>
public class AnnotationHubOptimized : NetworkBehaviour
{
    public static AnnotationHubOptimized Instance { get; private set; }

    private IOverlayAnnotator annotator;
    public bool IsNetworkReady => Runner != null && Object != null;

    // ---------------------------------------------------------------------
    // 설정값(튜닝 포인트)
    // ---------------------------------------------------------------------

    [Header("Out-of-order Reorder")]
    [Tooltip("seq gap이 이 시간 이상 유지되면(예: 10이 안 오는데 11,12만 쌓임), 버퍼가 멈추지 않도록 강제로 스킵합니다.")]
    [SerializeField] private float gapTimeoutSec = 0.35f;

    [Tooltip("author별 재정렬 버퍼에 쌓일 수 있는 최대 이벤트 수. 너무 커지면 메모리/지연 증가. 보통 128~512 사이.")]
    [SerializeField] private int maxBufferedEventsPerAuthor = 256;

    // ---------------------------------------------------------------------
    // seq 재정렬 버퍼 구조
    // ---------------------------------------------------------------------

    private enum LiveEventType : byte
    {
        BeginStroke = 1,
        AddPoints = 2,
        EndStroke = 3,
        AddLabel = 4,
        ClearAll = 5,
    }

    /// <summary>
    /// 네트워크로 들어오는 라이브 이벤트를 "재정렬/보관"하기 위한 구조체.
    ///
    /// 왜 struct?
    /// - 자잘한 이벤트가 많이 오므로 힙 할당을 줄이기 위해 값 타입 사용.
    /// - 단, byte[]/string은 참조 타입이라 내부적으로는 참조를 들고 있음.
    /// </summary>
    private struct LiveEvent
    {
        public LiveEventType type;
        public uint seq;
        public int strokeId;

        // style (BeginStroke)
        public Color32 color;
        public float widthPx;

        // points (AddPoints)
        public byte[] packedPoints;

        // label (AddLabel)
        public int labelId;
        public Vector2 posNorm;
        public string text;

        // 타임아웃/디버깅 용도
        public float receivedAt;
    }

    /// <summary>
    /// author별로 재정렬 버퍼를 유지하기 위한 상태.
    /// </summary>
    private class AuthorReorderState
    {
        // 다음으로 처리해야 할 seq.
        // "연속 처리"가 가능할 때만 전진한다.
        public uint expectedSeq;

        // seq -> 이벤트
        // SortedDictionary를 쓰면 가장 작은 seq부터 순회/검색이 쉬움.
        public readonly SortedDictionary<uint, LiveEvent> buffer = new();

        // expectedSeq가 막혀서(갭) 처리 못 하고 있는 시작 시각
        public float gapBeganAt = -1f;
    }

    // authorId -> reorder state
    private readonly Dictionary<int, AuthorReorderState> reorderByAuthor = new(64);

    // 스냅샷 적용 중에는 seq/reorder를 끈다.
    private bool isApplyingSnapshot;

    // ---------------------------------------------------------------------
    // 히스토리 저장 (Late Join Snapshot용)
    // ---------------------------------------------------------------------

    private struct StrokeStyle
    {
        public Color32 color;
        public float widthPx;
    }

    private struct StoredStroke
    {
        public int authorId;
        public int strokeId;
        public StrokeStyle style;
        public List<Vector2> points; // norm points
    }

    private struct StoredLabel
    {
        public int labelId;
        public Vector2 pos;
        public string text;
    }

    private readonly List<StoredStroke> storedStrokes = new(256);
    private readonly List<StoredLabel> storedLabels = new(64);

    // (authorId, strokeId) -> storedStrokes index
    private readonly Dictionary<OverlayStrokeKey, int> strokeIndex = new(256);

    // points 언팩 재사용 버퍼
    private readonly List<Vector2> unpackBuffer = new(256);

    // ---------------------------------------------------------------------
    // PendingStroke : Begin 이전 Points/End 보관용
    // ---------------------------------------------------------------------

    /// <summary>
    /// 스트로크가 정상 흐름(Begin -> Points -> End)으로 오지 않을 때,
    /// "나중에 Begin이 도착하면 최대한 복구해서 그리기" 위한 상태.
    ///
    /// 현업에서 흔한 패턴:
    /// - Begin이 늦게 오거나 유실되어도, Points를 일단 보관한다.
    /// - End가 먼저 와도 보관해두고, Begin이 오면 End까지 마무리한다.
    /// </summary>
    private class PendingStroke
    {
        public bool hasBegin;
        public StrokeStyle style;
        public readonly List<Vector2> pendingPoints = new(256);
        public bool pendingEnd;
    }

    // (authorId, strokeId) -> pending state
    private readonly Dictionary<OverlayStrokeKey, PendingStroke> pendingByStroke = new(256);

    // ---------------------------------------------------------------------
    // Fusion lifecycle
    // ---------------------------------------------------------------------

    public override void Spawned()
    {
        Instance = this;

        ResolveAnnotator();
        Debug.Log($"[Hub] Spawned. IsMaster={Runner.IsSharedModeMasterClient}");

        // Shared Mode Late Join: 마스터가 아닌 클라는 스냅샷 요청
        if (Runner.GameMode == GameMode.Shared)
        {
            if (!Runner.IsSharedModeMasterClient)
                RequestSnapshot();
        }
        else
        {
            RequestSnapshot();
        }
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        if (Instance == this) Instance = null;
        base.Despawned(runner, hasState);
    }

    private void ResolveAnnotator()
    {
        annotator = AnnotatorAnchor.Instance != null ? AnnotatorAnchor.Instance.Annotator : null;

        // 백업(가급적 앵커 권장)
        if (annotator == null)
        {
            foreach (var mb in FindObjectsOfType<MonoBehaviour>(true))
            {
                if (mb is IOverlayAnnotator a)
                {
                    annotator = a;
                    break;
                }
            }
        }
    }

    private bool IsAnnotatorReady() => annotator != null && annotator.IsReady;

    private int AuthorIdFrom(PlayerRef author)
        => author.GetHashCode();

    // ---------------------------------------------------------------------
    // 송신 API (NetBridge에서 호출) - 기존과 동일
    // ---------------------------------------------------------------------

    public void SendBeginStroke(uint seq, int strokeId, Color32 color, float widthPx)
    {
        if (!IsNetworkReady) return;

        RPC_BeginStroke(seq, strokeId, color, widthPx);

        // 송신자 로컬 즉시 반영(InvokeLocal=false 대비)
        //LocalRecordBegin(Runner.LocalPlayer, strokeId, color, widthPx);
        //LocalRenderBegin(Runner.LocalPlayer, strokeId, color, widthPx);
    }

    public void SendAddPoints(uint seq, int strokeId, byte[] packedPoints)
    {
        if (!IsNetworkReady) return;

        RPC_AddPoints(seq, strokeId, packedPoints);

        StrokeNetEncoderOptimized.UnpackPoints(packedPoints, unpackBuffer);
        //LocalRecordPoints(Runner.LocalPlayer, strokeId, unpackBuffer);
        //LocalRenderPoints(Runner.LocalPlayer, strokeId, unpackBuffer);
    }

    public void SendEndStroke(uint seq, int strokeId)
    {
        if (!IsNetworkReady) return;

        RPC_EndStroke(seq, strokeId);

        //LocalRecordEnd(Runner.LocalPlayer, strokeId);
        //LocalRenderEnd(Runner.LocalPlayer, strokeId);
    }

    public void SendAddLabel(uint seq, int labelId, Vector2 posNorm, string text)
    {
        if (!IsNetworkReady) return;

        RPC_AddLabel(seq, labelId, posNorm, text);

        //LocalRecordLabel(labelId, posNorm, text);
        //LocalRenderLabel(labelId, posNorm, text);
    }

    public void SendClearAll(uint seq)
    {
        if (!IsNetworkReady) return;

        RPC_ClearAll(seq);

        // Clear는 강한 상태 변경이므로 송신자도 즉시 반영
        //LocalClearAll();
    }

    // ---------------------------------------------------------------------
    // 라이브 RPC 수신 -> "즉시 처리" 대신 "재정렬 버퍼에 enqueue" 후 flush
    // ---------------------------------------------------------------------

    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_BeginStroke(uint seq, int strokeId, Color32 color, float widthPx, RpcInfo info = default)
    {
        EnqueueLiveEvent(info.Source, new LiveEvent
        {
            type = LiveEventType.BeginStroke,
            seq = seq,
            strokeId = strokeId,
            color = color,
            widthPx = widthPx,
            receivedAt = Time.unscaledTime
        });
    }

    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_AddPoints(uint seq, int strokeId, byte[] packedPoints, RpcInfo info = default)
    {
        if (packedPoints == null || packedPoints.Length == 0) return;

        EnqueueLiveEvent(info.Source, new LiveEvent
        {
            type = LiveEventType.AddPoints,
            seq = seq,
            strokeId = strokeId,
            packedPoints = packedPoints,
            receivedAt = Time.unscaledTime
        });
    }

    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_EndStroke(uint seq, int strokeId, RpcInfo info = default)
    {
        EnqueueLiveEvent(info.Source, new LiveEvent
        {
            type = LiveEventType.EndStroke,
            seq = seq,
            strokeId = strokeId,
            receivedAt = Time.unscaledTime
        });
    }

    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_AddLabel(uint seq, int labelId, Vector2 posNorm, string text, RpcInfo info = default)
    {
        EnqueueLiveEvent(info.Source, new LiveEvent
        {
            type = LiveEventType.AddLabel,
            seq = seq,
            labelId = labelId,
            posNorm = posNorm,
            text = text,
            receivedAt = Time.unscaledTime
        });
    }

    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_ClearAll(uint seq, RpcInfo info = default)
    {
        EnqueueLiveEvent(info.Source, new LiveEvent
        {
            type = LiveEventType.ClearAll,
            seq = seq,
            receivedAt = Time.unscaledTime
        });
    }

    /// <summary>
    /// 라이브 이벤트를 재정렬 버퍼에 넣고, 처리 가능한 만큼 순서대로 처리한다.
    ///
    /// 왜 즉시 처리하지 않나?
    /// - out-of-order를 정상 순서로 바꾸려면 "일단 모아야" 한다.
    /// - seq 체크로 무조건 드롭하면 역순에서 스트로크가 통째로 사라질 수 있음.
    ///
    /// 스냅샷 적용 중이면:
    /// - 라이브 이벤트를 처리하면 충돌 가능
    /// - 이 예제에서는 스냅샷 중 라이브 처리를 막는 쪽(안정 우선)
    /// </summary>
    private void EnqueueLiveEvent(PlayerRef author, LiveEvent evt)
    {
        if (isApplyingSnapshot) return; // 스냅샷 중에는 라이브를 막아 안정성 우선

        int authorId = AuthorIdFrom(author);

        if (!reorderByAuthor.TryGetValue(authorId, out var st))
        {
            st = new AuthorReorderState
            {
                expectedSeq = 0,
                gapBeganAt = -1f
            };
            reorderByAuthor[authorId] = st;
        }

        // buffer overflow 방지:
        // 너무 많이 쌓이면 지연이 커지고 메모리를 잡아먹음.
        // 현업에서도 "윈도우 제한"을 둔다.
        if (st.buffer.Count >= maxBufferedEventsPerAuthor)
        {
            // 가장 오래된 것(가장 작은 seq)을 제거하고 진행성을 확보.
            // 제거 정책은 프로젝트에 따라 다르지만,
            // 드로잉은 "최신이 중요"하므로 이런 선택이 합리적일 때가 많다.
            var firstKey = GetFirstKey(st.buffer);
            st.buffer.Remove(firstKey);

            // expectedSeq가 제거된 쪽에 걸려 있으면, expectedSeq를 앞으로 당긴다.
            if (st.expectedSeq <= firstKey)
                st.expectedSeq = firstKey;
        }

        // 중복 seq는 그냥 무시(동일 seq는 동일 이벤트라고 가정)
        if (st.buffer.ContainsKey(evt.seq))
            return;

        st.buffer.Add(evt.seq, evt);

        FlushAuthorBuffer(author, authorId, st);
    }

    /// <summary>
    /// author별 버퍼에서 expectedSeq부터 연속된 이벤트를 처리한다.
    /// seq 갭이 오래 지속되면 타임아웃으로 스킵한다.
    /// </summary>
    private void FlushAuthorBuffer(PlayerRef author, int authorId, AuthorReorderState st)
    {
        // expectedSeq가 0이라면, 최초로 들어온 이벤트를 기준으로 잡는다.
        // 이유:
        // - 첫 이벤트가 seq=100부터 올 수도 있음(중간 합류/리셋 등)
        // - 무조건 1부터 기다리면 영원히 처리 못함
        if (st.expectedSeq == 0 && st.buffer.Count > 0)
        {
            uint first = GetFirstKey(st.buffer);
            st.expectedSeq = first;
        }

        bool progressed = false;

        while (st.buffer.TryGetValue(st.expectedSeq, out var evt))
        {
            st.buffer.Remove(st.expectedSeq);

            ApplyLiveEvent(author, authorId, evt);

            st.expectedSeq++; // 다음 seq로 전진
            progressed = true;

            // 갭 대기 시작 시각 리셋
            st.gapBeganAt = -1f;
        }

        if (progressed)
            return;

        // 여기까지 왔다는 건:
        // - buffer에 뭔가 있지만 expectedSeq가 없음(갭)
        // - 즉, out-of-order로 더 큰 seq들이 들어온 상태

        if (st.buffer.Count <= 0)
        {
            st.gapBeganAt = -1f;
            return;
        }

        // 갭 대기 시작 기록
        if (st.gapBeganAt < 0f)
            st.gapBeganAt = Time.unscaledTime;

        // 타임아웃이면 "가장 작은 seq로 expectedSeq를 점프"해서 진행성을 확보
        if (Time.unscaledTime - st.gapBeganAt >= gapTimeoutSec)
        {
            uint jump = GetFirstKey(st.buffer);

            // expectedSeq가 너무 뒤에 고정돼서 멈춘 경우를 풀어준다.
            st.expectedSeq = jump;

            st.gapBeganAt = -1f;

            // 점프 후 다시 flush
            FlushAuthorBuffer(author, authorId, st);
        }
    }

    private static uint GetFirstKey(SortedDictionary<uint, LiveEvent> dict)
    {
        // SortedDictionary는 첫 요소가 최솟값
        foreach (var kv in dict)
            return kv.Key;
        return 0;
    }

    // ---------------------------------------------------------------------
    // 이벤트 적용: PendingStroke를 사용해 Begin 이전 Points/End도 최대한 복구
    // ---------------------------------------------------------------------

    /// <summary>
    /// 재정렬이 완료된 "정상 순서 이벤트"를 실제로 적용한다.
    ///
    /// 여기서도 Begin 이전 Points/End에 대비해 PendingStroke를 사용한다.
    /// (재정렬을 해도, gap 스킵이 일어나거나 Begin 이벤트 자체가 늦을 수 있기 때문)
    /// </summary>
    private void ApplyLiveEvent(PlayerRef author, int authorId, LiveEvent evt)
    {
        // Clear는 전체 상태를 바꾸는 이벤트라 우선순위가 매우 높다.
        // Clear 이후에 들어온 예전 스트로크 이벤트가 적용되면 화면이 다시 더러워짐.
        // 그래서 Clear는 들어오면 "pending까지" 함께 리셋한다.
        if (evt.type == LiveEventType.ClearAll)
        {
            LocalClearAll();
            pendingByStroke.Clear(); // Begin 이전 Points까지 포함해 싹 제거
            return;
        }

        switch (evt.type)
        {
            case LiveEventType.BeginStroke:
                ApplyBegin(authorId, evt.strokeId, evt.color, evt.widthPx);
                break;

            case LiveEventType.AddPoints:
                ApplyPoints(authorId, evt.strokeId, evt.packedPoints);
                break;

            case LiveEventType.EndStroke:
                ApplyEnd(authorId, evt.strokeId);
                break;

            case LiveEventType.AddLabel:
                LocalRecordLabel(evt.labelId, evt.posNorm, evt.text);
                LocalRenderLabel(evt.labelId, evt.posNorm, evt.text);
                break;
        }
    }

    private PendingStroke GetOrCreatePending(OverlayStrokeKey key)
    {
        if (!pendingByStroke.TryGetValue(key, out var ps))
        {
            ps = new PendingStroke();
            pendingByStroke[key] = ps;
        }
        return ps;
    }

    private void ApplyBegin(int authorId, int strokeId, Color32 color, float widthPx)
    {
        var key = new OverlayStrokeKey(authorId, strokeId);
        var ps = GetOrCreatePending(key);

        // Begin이 이미 적용된 스트로크에 다시 Begin이 오면?
        // - seq가 정상이라면 거의 없지만, gap 스킵/버퍼 오버플로 등으로 생길 수 있음.
        // - 정책: 새 Begin이 오면 스타일 갱신만 하고 계속 그린다(최대한 표시 유지).
        ps.hasBegin = true;
        ps.style = new StrokeStyle { color = color, widthPx = widthPx };

        // 히스토리/렌더에 Begin 반영
        LocalRecordBegin_ByAuthorId(authorId, strokeId, color, widthPx);
        if (IsAnnotatorReady())
            annotator.BeginStroke(key, color, widthPx);

        // Begin 전에 쌓여있던 Points가 있으면 즉시 반영
        if (ps.pendingPoints.Count > 0)
        {
            LocalRecordPoints_ByAuthorId(authorId, strokeId, ps.pendingPoints);
            if (IsAnnotatorReady())
                annotator.AddPoints(key, ps.pendingPoints);

            ps.pendingPoints.Clear();
        }

        // Begin 전에 End가 먼저 와 있었다면 바로 End 처리
        if (ps.pendingEnd)
        {
            ps.pendingEnd = false;
            LocalRecordEnd_ByAuthorId(authorId, strokeId);
            if (IsAnnotatorReady())
                annotator.EndStroke(key);

            // 스트로크는 끝났으니 pending 제거(메모리 회수)
            pendingByStroke.Remove(key);
        }
    }

    private void ApplyPoints(int authorId, int strokeId, byte[] packedPoints)
    {
        if (packedPoints == null || packedPoints.Length == 0)
            return;

        StrokeNetEncoderOptimized.UnpackPoints(packedPoints, unpackBuffer);

        var key = new OverlayStrokeKey(authorId, strokeId);
        var ps = GetOrCreatePending(key);

        if (!ps.hasBegin)
        {
            // Begin이 아직 없으면, 포인트를 보관한다.
            // 이것이 "역순에서도 최대한 그려지게" 하는 핵심.
            ps.pendingPoints.AddRange(unpackBuffer);
            return;
        }

        // Begin이 이미 적용된 상태면 즉시 반영
        LocalRecordPoints_ByAuthorId(authorId, strokeId, unpackBuffer);
        if (IsAnnotatorReady())
            annotator.AddPoints(key, unpackBuffer);
    }

    private void ApplyEnd(int authorId, int strokeId)
    {
        var key = new OverlayStrokeKey(authorId, strokeId);
        var ps = GetOrCreatePending(key);

        if (!ps.hasBegin)
        {
            // Begin이 아직 없으면 End를 보류한다.
            // 나중에 Begin이 오면 Begin->(Points flush)->End로 마무리.
            ps.pendingEnd = true;
            return;
        }

        LocalRecordEnd_ByAuthorId(authorId, strokeId);
        if (IsAnnotatorReady())
            annotator.EndStroke(key);

        pendingByStroke.Remove(key);
    }

    // ---------------------------------------------------------------------
    // Late Join Snapshot (기존 구조 유지)
    // ---------------------------------------------------------------------

    private void RequestSnapshot()
    {
        if (!IsNetworkReady) return;
        RPC_RequestSnapshot(Runner.LocalPlayer);
    }

    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_RequestSnapshot(PlayerRef target, RpcInfo info = default)
    {
        if (!Runner.IsSharedModeMasterClient) return;
        if (!IsAnnotatorReady()) return;
        if (target == Runner.LocalPlayer) return;

        RPC_Snapshot_Begin(target);

        for (int i = 0; i < storedStrokes.Count; i++)
        {
            var s = storedStrokes[i];

            RPC_Snapshot_BeginStroke(target, s.authorId, s.strokeId, s.style.color, s.style.widthPx);

            int total = s.points.Count;
            StrokeNetEncoderOptimized.ForEachChunkByBytes(
                totalPoints: total,
                maxBytesPerChunk: 900,
                onChunk: (start, count) =>
                {
                    var packed = StrokeNetEncoderOptimized.PackPoints(s.points, start, count);
                    RPC_Snapshot_AddPoints(target, s.authorId, s.strokeId, packed);
                });

            RPC_Snapshot_EndStroke(target, s.authorId, s.strokeId);
        }

        for (int i = 0; i < storedLabels.Count; i++)
        {
            var l = storedLabels[i];
            RPC_Snapshot_AddLabel(target, l.labelId, l.pos, l.text);
        }

        RPC_Snapshot_End(target);
    }

    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_Snapshot_Begin(PlayerRef target, RpcInfo info = default)
    {
        if (Runner.LocalPlayer != target) return;
        if (!IsAnnotatorReady()) return;

        // 스냅샷 중에는 라이브 reorder/pendings를 정리하고, 안정적으로 상태 재구축
        isApplyingSnapshot = true;

        annotator.BeginBulk();

        storedStrokes.Clear();
        storedLabels.Clear();
        strokeIndex.Clear();

        reorderByAuthor.Clear();
        pendingByStroke.Clear();

        annotator.ClearAll();
    }

    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_Snapshot_BeginStroke(PlayerRef target, int authorId, int strokeId, Color32 color, float widthPx, RpcInfo info = default)
    {
        if (Runner.LocalPlayer != target) return;
        if (!IsAnnotatorReady()) return;

        LocalRecordBegin_ByAuthorId(authorId, strokeId, color, widthPx);
        annotator.BeginStroke(new OverlayStrokeKey(authorId, strokeId), color, widthPx);
    }

    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_Snapshot_AddPoints(PlayerRef target, int authorId, int strokeId, byte[] packedPoints, RpcInfo info = default)
    {
        if (Runner.LocalPlayer != target) return;
        if (!IsAnnotatorReady()) return;
        if (packedPoints == null || packedPoints.Length == 0) return;

        StrokeNetEncoderOptimized.UnpackPoints(packedPoints, unpackBuffer);

        LocalRecordPoints_ByAuthorId(authorId, strokeId, unpackBuffer);
        annotator.AddPoints(new OverlayStrokeKey(authorId, strokeId), unpackBuffer);
    }

    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_Snapshot_EndStroke(PlayerRef target, int authorId, int strokeId, RpcInfo info = default)
    {
        if (Runner.LocalPlayer != target) return;
        if (!IsAnnotatorReady()) return;

        LocalRecordEnd_ByAuthorId(authorId, strokeId);
        annotator.EndStroke(new OverlayStrokeKey(authorId, strokeId));
    }

    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_Snapshot_AddLabel(PlayerRef target, int labelId, Vector2 posNorm, string text, RpcInfo info = default)
    {
        if (Runner.LocalPlayer != target) return;
        if (!IsAnnotatorReady()) return;

        LocalRecordLabel(labelId, posNorm, text);
        annotator.AddLabel(labelId, posNorm, text);
    }

    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_Snapshot_End(PlayerRef target, RpcInfo info = default)
    {
        if (Runner.LocalPlayer != target) return;
        if (!IsAnnotatorReady()) return;

        annotator.EndBulk();
        isApplyingSnapshot = false;

        Debug.Log("[Hub] Snapshot applied.");
    }

    // ---------------------------------------------------------------------
    // 로컬 히스토리 기록 (스냅샷 소스가 되므로 매우 중요)
    // ---------------------------------------------------------------------

    private void LocalRecordBegin(PlayerRef author, int strokeId, Color32 color, float widthPx)
        => LocalRecordBegin_ByAuthorId(AuthorIdFrom(author), strokeId, color, widthPx);

    private void LocalRecordBegin_ByAuthorId(int authorId, int strokeId, Color32 color, float widthPx)
    {
        var key = new OverlayStrokeKey(authorId, strokeId);

        if (strokeIndex.TryGetValue(key, out int idx))
        {
            var s = storedStrokes[idx];
            s.points.Clear();
            s.style = new StrokeStyle { color = color, widthPx = widthPx };
            storedStrokes[idx] = s;
            return;
        }

        var ns = new StoredStroke
        {
            authorId = authorId,
            strokeId = strokeId,
            style = new StrokeStyle { color = color, widthPx = widthPx },
            points = new List<Vector2>(256)
        };

        storedStrokes.Add(ns);
        strokeIndex[key] = storedStrokes.Count - 1;
    }

    private void LocalRecordPoints(PlayerRef author, int strokeId, List<Vector2> points)
        => LocalRecordPoints_ByAuthorId(AuthorIdFrom(author), strokeId, points);

    private void LocalRecordPoints_ByAuthorId(int authorId, int strokeId, List<Vector2> points)
    {
        var key = new OverlayStrokeKey(authorId, strokeId);
        if (!strokeIndex.TryGetValue(key, out int idx)) return;

        storedStrokes[idx].points.AddRange(points);
    }

    private void LocalRecordEnd(PlayerRef author, int strokeId)
        => LocalRecordEnd_ByAuthorId(AuthorIdFrom(author), strokeId);

    private void LocalRecordEnd_ByAuthorId(int authorId, int strokeId)
    {
        // 필요하면 종료 플래그 저장 가능(현재는 생략)
    }

    private void LocalRecordLabel(int labelId, Vector2 posNorm, string text)
    {
        storedLabels.Add(new StoredLabel { labelId = labelId, pos = posNorm, text = text });
    }

    private void LocalClearAll()
    {
        storedStrokes.Clear();
        storedLabels.Clear();
        strokeIndex.Clear();

        reorderByAuthor.Clear();
        pendingByStroke.Clear();

        if (IsAnnotatorReady())
            annotator.ClearAll();
    }

    // ---------------------------------------------------------------------
    // 로컬 렌더 호출 (InvokeLocal=false 대비)
    // ---------------------------------------------------------------------

    private void LocalRenderBegin(PlayerRef author, int strokeId, Color32 color, float widthPx)
    {
        if (!IsAnnotatorReady()) return;
        int authorId = AuthorIdFrom(author);
        annotator.BeginStroke(new OverlayStrokeKey(authorId, strokeId), color, widthPx);
    }

    private void LocalRenderPoints(PlayerRef author, int strokeId, List<Vector2> points)
    {
        if (!IsAnnotatorReady()) return;
        int authorId = AuthorIdFrom(author);
        annotator.AddPoints(new OverlayStrokeKey(authorId, strokeId), points);
    }

    private void LocalRenderEnd(PlayerRef author, int strokeId)
    {
        if (!IsAnnotatorReady()) return;
        int authorId = AuthorIdFrom(author);
        annotator.EndStroke(new OverlayStrokeKey(authorId, strokeId));
    }

    private void LocalRenderLabel(int labelId, Vector2 posNorm, string text)
    {
        if (!IsAnnotatorReady()) return;
        annotator.AddLabel(labelId, posNorm, text);
    }
}
