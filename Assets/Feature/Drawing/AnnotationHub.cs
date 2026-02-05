using System.Collections.Generic;
using Fusion;
using UnityEngine;

/// <summary>
/// 네트워크 상에서 드로잉 이벤트를 중계/저장/재생하는 허브(NetworkBehaviour).
///
/// ============================
/// 왜 "씬 오브젝트"여야 하나?
/// ============================
/// Shared Mode에서 마스터(SharedModeMasterClient)가 나가면,
/// "마스터가 Spawn한 NetworkObject"는 함께 Despawn되는 경우가 흔합니다.
/// (특히 마스터가 StateAuthority를 가진 오브젝트는 세션 내에서 유지가 불안정해질 수 있음)
///
/// 그래서 Hub는 "씬에 고정된 NetworkObject(Scene Object)"로 두는 게 실무적으로 안전합니다.
/// - 마스터가 바뀌어도 오브젝트는 살아 있고
/// - StateAuthority만 새로운 마스터로 넘어갑니다.
///
/// ============================
/// 히스토리 정책(가장 중요)
/// ============================
/// 이 Hub는 드로잉 히스토리를 "마스터만" 저장하지 않습니다.
/// 모든 클라이언트가 동일한 히스토리를 누적합니다.
///
/// 이유:
/// - 마스터가 바뀌는 순간, "마스터 로컬 메모리에만 있던 히스토리"는 사라집니다.
/// - Late Join 스냅샷은 마스터가 보내는데, 마스터가 바뀌어도 계속 동작하려면
///   새 마스터도 동일한 히스토리를 이미 가지고 있어야 합니다.
///
/// 구현 방식:
/// - 실시간 드로잉 RPC는 InvokeLocal=false로 유지합니다.
///   즉 "보낸 사람"은 RPC를 다시 받지 않습니다.
/// - 따라서 "보낸 사람"은 SendXXX()에서 로컬 히스토리를 직접 누적합니다.
/// - "받는 사람들"은 RPC 핸들러에서 로컬 히스토리를 누적합니다.
/// 결과적으로 모든 클라이언트의 storedStrokes/storedLabels는 동일하게 됩니다.
///
/// ============================
/// Late Join 정책
/// ============================
/// - Late Join 시, 참가자는 현재 StateAuthority(마스터)에게 스냅샷을 요청합니다.
/// - 마스터는 자신의 로컬 히스토리를 기반으로 스냅샷을 구성해 참가자에게만 전송합니다.
/// - 히스토리는 모든 클라이언트가 동일하므로, 마스터가 바뀌어도 스냅샷 기능은 계속됩니다.
/// </summary>
public class AnnotationHub : NetworkBehaviour
{
    /// <summary>
    /// 런타임 싱글턴(편의용).
    /// - Scene Object이므로 보통 항상 1개만 존재하게 구성합니다.
    /// </summary>
    public static AnnotationHub Instance { get; private set; }

    /// <summary>
    /// 실제 렌더링 구현체(UI Toolkit / Texture2D 등).
    /// </summary>
    private IOverlayAnnotator annotator;

    /// <summary>
    /// 네트워크 준비 여부.
    /// - Spawned 이후 Runner/Object가 유효해집니다.
    /// </summary>
    public bool IsNetworkReady => Runner != null && Object != null;

    // ============================
    // 저장소(히스토리) - 모든 클라이언트가 동일하게 누적
    // ============================

    /// <summary>
    /// 누적된 스트로크 히스토리.
    /// - 모든 클라이언트가 동일한 순서/내용으로 누적됩니다.
    /// - Late Join 스냅샷은 이 리스트로 재구성합니다.
    /// </summary>
    private readonly List<StoredStroke> storedStrokes = new();

    /// <summary>
    /// 누적된 텍스트(라벨) 히스토리.
    /// </summary>
    private readonly List<StoredLabel> storedLabels = new();

    /// <summary>
    /// 히스토리 저장용 스트로크 데이터.
    /// - author + strokeId 조합으로 스트로크를 유일하게 식별합니다.
    /// </summary>
    private struct StoredStroke
    {
        public PlayerRef author;
        public int strokeId;
        public Color32 color;
        public float widthPx;
        public List<Vector2> points;
    }

    /// <summary>
    /// 히스토리 저장용 라벨 데이터.
    /// </summary>
    private struct StoredLabel
    {
        public int labelId;
        public Vector2 pos;
        public string text;
    }

    // ============================
    // 수신(원격 재생) 상태
    // ============================

    /// <summary>
    /// 네트워크에서 들어오는 스트로크를 유일하게 구분하기 위한 키.
    /// - (author, strokeId)
    /// </summary>
    private readonly struct NetStrokeKey : System.IEquatable<NetStrokeKey>
    {
        public readonly PlayerRef author;
        public readonly int strokeId;

        public NetStrokeKey(PlayerRef author, int strokeId)
        {
            this.author = author;
            this.strokeId = strokeId;
        }

        public bool Equals(NetStrokeKey other)
            => author.Equals(other.author) && strokeId == other.strokeId;

        public override bool Equals(object obj)
            => obj is NetStrokeKey other && Equals(other);

        public override int GetHashCode()
            => System.HashCode.Combine(author.GetHashCode(), strokeId);
    }

    /// <summary>
    /// 원격 스트로크의 시작/스타일 정보를 보관.
    /// - BeginStroke는 "첫 포인트가 도착했을 때" 실제로 호출(지연 시작)
    ///   -> 네트워크 순서/타이밍 이슈로 AddPoints가 먼저 오는 경우를 방어하기 위함.
    /// </summary>
    private struct RemoteStrokeState
    {
        public Color32 color;
        public float widthPx;
        public bool begun;
    }

    /// <summary>
    /// 원격 스트로크 상태 맵.
    /// </summary>
    private readonly Dictionary<NetStrokeKey, RemoteStrokeState> remoteStates = new();

    /// <summary>
    /// packedPoints를 Unpack하여 재사용하는 버퍼(할당 최소화).
    /// </summary>
    private readonly List<Vector2> unpackBuffer = new();

    public override void Spawned()
    {
        Instance = this;

        ResolveAnnotator();

        Debug.Log(
            $"[AnnotationHub] Spawned. NetworkReady={IsNetworkReady}, annotator={(annotator != null)}, " +
            $"IsReady={(annotator != null && annotator.IsReady)}, HasStateAuthority={Object.HasStateAuthority}"
        );

        // 마스터는 자기 스냅샷 요청 금지 (자기 히스토리 지워질 수 있음)
        if (Runner.GameMode == GameMode.Shared)
        {
            if (!Runner.IsSharedModeMasterClient)
                RequestSnapshot();
        }
        else
        {
            // Shared 아닌 경우 정책이 필요하면 여기서 결정
            RequestSnapshot();
        }
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        if (Instance == this) Instance = null;
        base.Despawned(runner, hasState);
    }

    /// <summary>
    /// Annotator를 찾는 로직.
    /// - AnnotatorAnchor.Instance가 있으면 거기서 가져오는 것을 최우선(권장).
    /// - 없으면 Scene 전체에서 IOverlayAnnotator 구현체를 탐색(백업).
    /// </summary>
    private void ResolveAnnotator()
    {
        if (AnnotatorAnchor.Instance != null)
            annotator = AnnotatorAnchor.Instance.Annotator;

        if (annotator == null)
        {
            var candidates = FindObjectsOfType<MonoBehaviour>(true);
            foreach (var c in candidates)
            {
                if (c is IOverlayAnnotator a)
                {
                    annotator = a;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Annotator가 실제로 입력/렌더 가능한 상태인지 확인.
    /// </summary>
    private bool IsAnnotatorReady()
        => annotator != null && annotator.IsReady;

    // =============================
    // Bridge에서 호출하는 API (전송 + 로컬 히스토리 반영)
    // =============================

    /// <summary>
    /// 스트로크 시작 전송.
    ///
    /// 주의:
    /// - RPC는 InvokeLocal=false라서 "보낸 사람"은 Begin RPC를 다시 받지 않습니다.
    /// - 따라서 여기서 로컬 히스토리를 먼저 누적해야 모든 클라이언트의 히스토리가 동일해집니다.
    /// </summary>
    public void SendBeginStroke(int strokeId, Color32 color, float widthPx)
    {
        if (!IsNetworkReady) return;

        // 네트워크 전송량을 줄이기 위해 width를 0.1px 단위로 양자화(ushort)
        ushort widthQ = (ushort)Mathf.Clamp(Mathf.RoundToInt(widthPx * 10f), 1, 4000);

        // 1) 로컬 히스토리 반영(보낸 사람 전용)
        LocalRecordBegin(Runner.LocalPlayer, strokeId, color, widthQ);

        // 2) 네트워크 전송(다른 사람들에게만 도착)
        RPC_BeginStroke(strokeId, color, widthQ);
    }

    /// <summary>
    /// 포인트 청크 전송.
    /// - packedPoints는 (x ushort + y ushort) 반복으로 구성된 바이트 배열(StrokeNetEncoder 참고)
    /// </summary>
    public void SendAddPointsChunk(int strokeId, byte[] packedPoints)
    {
        if (!IsNetworkReady) return;

        // 1) 로컬 히스토리 반영(보낸 사람 전용)
        LocalRecordPoints(Runner.LocalPlayer, strokeId, packedPoints);

        // 2) 네트워크 전송
        RPC_AddPoints(strokeId, packedPoints);
    }

    /// <summary>
    /// 스트로크 종료 전송.
    /// </summary>
    public void SendEndStroke(int strokeId)
    {
        if (!IsNetworkReady) return;

        // 종료 자체는 히스토리 상 별도 데이터가 필요하지 않지만,
        // 수신측 렌더에서는 EndStroke가 필요합니다.
        RPC_EndStroke(strokeId);
    }

    /// <summary>
    /// 라벨 전송.
    /// </summary>
    public void SendAddLabel(int labelId, Vector2 posNorm, string text)
    {
        if (!IsNetworkReady) return;

        ushort x = StrokeNetEncoder.Float01ToU16(posNorm.x);
        ushort y = StrokeNetEncoder.Float01ToU16(posNorm.y);

        // 1) 로컬 히스토리 반영(보낸 사람 전용)
        LocalRecordLabel(labelId, x, y, text);

        // 2) 네트워크 전송
        RPC_AddLabel(labelId, x, y, text);
    }

    /// <summary>
    /// 전체 Clear 전송.
    ///
    /// 요구사항:
    /// - 세션이 유지되는 동안 드로잉은 유지
    /// - 명시적으로 Clear를 누르면 모두에게 Clear + 히스토리도 초기화
    ///
    /// RPC는 InvokeLocal=false이므로,
    /// 보낸 사람은 여기서 로컬 Clear를 먼저 수행해야 합니다.
    /// </summary>
    public void SendClear()
    {
        if (!IsNetworkReady) return;

        // 1) 로컬 히스토리 + 로컬 렌더 초기화(보낸 사람 전용)
        LocalApplyClear();

        // 2) 네트워크 전송
        RPC_Clear();
    }

    // =============================
    // Late Join: Snapshot
    // =============================

    /// <summary>
    /// 스냅샷 요청.
    /// - "현재 클라이언트"가 StateAuthority(마스터)에게 요청합니다.
    /// </summary>
    public void RequestSnapshot()
    {
        if (!IsNetworkReady) return;
        Debug.Log($"[Snapshot][Send] local={Runner.LocalPlayer} -> request snapshot");
        RPC_RequestSnapshot(Runner.LocalPlayer);
    }

    /// <summary>
    /// 스냅샷 요청 RPC (모든 클라이언트 -> StateAuthority).
    /// - StateAuthority만 처리합니다.
    /// - 핵심: 히스토리는 모든 클라이언트가 동일하게 누적되므로,
    ///         마스터가 바뀌어도 마스터는 스냅샷을 만들 수 있습니다.
    /// "요청"은 모든 클라가 받게 하고, "처리"는 SharedModeMasterClient만 하도록 가드
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_RequestSnapshot(PlayerRef requester, RpcInfo info = default)
    {
        Debug.Log(
        $"[Snapshot][Request] hub={Object.Id} " +
        $"src={info.Source} requester={requester} " +
        $"isMaster={Runner.IsSharedModeMasterClient} hasSA={Object.HasStateAuthority} " +
        $"mode={Runner.GameMode}");

        // Shared Mode에서는 마스터만 처리
        if (Runner.GameMode == GameMode.Shared && !Runner.IsSharedModeMasterClient)
            return;

        // 스냅샷 응답 RPC들은 RpcSources.StateAuthority 이므로,
        // 실제 전송 주체는 StateAuthority여야 안전합니다.
        // if (!Object.HasStateAuthority)
            // return;

        Debug.Log($"[Snapshot][Reply] to={requester} strokes={storedStrokes.Count} labels={storedLabels.Count}");
        // 1) requester 화면을 먼저 clear
        RPC_SnapshotClear(requester);

        // 2) 라벨 전송
        for (int i = 0; i < storedLabels.Count; i++)
        {
            var lbl = storedLabels[i];
            ushort x = StrokeNetEncoder.Float01ToU16(lbl.pos.x);
            ushort y = StrokeNetEncoder.Float01ToU16(lbl.pos.y);
            RPC_SnapshotAddLabel(requester, lbl.labelId, x, y, lbl.text);
        }

        // 3) 스트로크 전송
        for (int i = 0; i < storedStrokes.Count; i++)
        {
            var s = storedStrokes[i];
            ushort widthQ = (ushort)Mathf.Clamp(Mathf.RoundToInt(s.widthPx * 10f), 1, 4000);

            RPC_SnapshotBeginStroke(requester, s.author, s.strokeId, s.color, widthQ);

            const int MAX_POINTS_PER_RPC = 64;
            StrokeNetEncoder.ForEachChunk(s.points, MAX_POINTS_PER_RPC, (start, count) =>
            {
                var temp = new Vector2[count];
                for (int k = 0; k < count; k++) temp[k] = s.points[start + k];

                var packed = StrokeNetEncoder.PackPoints(temp);
                RPC_SnapshotAddPoints(requester, s.author, s.strokeId, packed);
            });

            RPC_SnapshotEndStroke(requester, s.author, s.strokeId);
        }
    }

    /// <summary>
    /// 스냅샷: 대상 클라이언트의 화면/히스토리를 '스냅샷 기준'으로 덮어쓰기 위해 Clear 수행.
    /// - RpcTargets.All로 브로드캐스트하지만 target(PlayerRef)으로 수신 클라만 적용합니다.
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_SnapshotClear(PlayerRef target, RpcInfo info = default)
    {
        if (Runner.LocalPlayer != target) return;
        if (!IsAnnotatorReady()) return;

        // 스냅샷을 적용할 때는 "기존 상태"를 지우고,
        // 이어서 들어오는 SnapshotBegin/AddPoints/AddLabel로 동일 히스토리를 재구성합니다.
        // (이렇게 해야 이 클라이언트가 나중에 마스터가 되어도 스냅샷 재전송이 가능)
        storedStrokes.Clear();
        storedLabels.Clear();

        remoteStates.Clear();
        annotator.Clear();
    }

    /// <summary>
    /// 스냅샷: 라벨 추가(대상 클라이언트만).
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_SnapshotAddLabel(PlayerRef target, int labelId, ushort x, ushort y, string text, RpcInfo info = default)
    {
        if (Runner.LocalPlayer != target) return;
        if (!IsAnnotatorReady()) return;

        // 스냅샷으로 받은 라벨도 히스토리에 누적
        LocalRecordLabel(labelId, x, y, text);

        var pos = new Vector2(StrokeNetEncoder.U16ToFloat01(x), StrokeNetEncoder.U16ToFloat01(y));
        annotator.AddText(pos, text);
    }

    /// <summary>
    /// 스냅샷: 스트로크 시작 정보 저장(대상 클라이언트만).
    /// - 실제 BeginStroke 호출은 첫 포인트 도착 시점에 수행(지연 시작)
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_SnapshotBeginStroke(PlayerRef target, PlayerRef author, int strokeId, Color32 color, ushort widthQ, RpcInfo info = default)
    {
        if (Runner.LocalPlayer != target) return;
        if (!IsAnnotatorReady()) return;

        // 스냅샷으로 받은 스트로크 시작 정보도 히스토리에 누적
        // (points는 AddPoints에서 누적됨)
        LocalRecordBegin(author, strokeId, color, widthQ);

        var key = new NetStrokeKey(author, strokeId);
        remoteStates[key] = new RemoteStrokeState
        {
            color = color,
            widthPx = widthQ / 10f,
            begun = false
        };
    }

    /// <summary>
    /// 스냅샷: 포인트 추가(대상 클라이언트만).
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_SnapshotAddPoints(PlayerRef target, PlayerRef author, int strokeId, byte[] packedPoints, RpcInfo info = default)
    {
        if (Runner.LocalPlayer != target) return;

        // 스냅샷으로 받은 포인트도 히스토리에 누적
        LocalRecordPoints(author, strokeId, packedPoints);

        ReplayPackedPoints(new NetStrokeKey(author, strokeId), packedPoints);
    }

    /// <summary>
    /// 스냅샷: 스트로크 종료(대상 클라이언트만).
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_SnapshotEndStroke(PlayerRef target, PlayerRef author, int strokeId, RpcInfo info = default)
    {
        if (Runner.LocalPlayer != target) return;
        if (!IsAnnotatorReady()) return;

        var key = new NetStrokeKey(author, strokeId);

        if (remoteStates.TryGetValue(key, out var st) && st.begun)
        {
            var overlayKey = ToOverlayKey(key);
            annotator.EndStroke(overlayKey);
        }

        remoteStates.Remove(key);
    }

    // =============================
    // 실시간 RPC (드로잉 동기화)
    // =============================

    /// <summary>
    /// 실시간: 스트로크 시작.
    /// - InvokeLocal=false 이므로 "보낸 사람"은 이 RPC를 받지 않습니다.
    /// - info.Source는 "이 RPC를 보낸 플레이어".
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.All, InvokeLocal = false)]
    private void RPC_BeginStroke(int strokeId, Color32 color, ushort widthQ, RpcInfo info = default)
    {
        var author = info.Source;

        // 1) 히스토리 누적(수신자들은 여기서 누적)
        LocalRecordBegin(author, strokeId, color, widthQ);

        // 2) 렌더 준비 (실제 BeginStroke는 첫 포인트 시점에 수행)
        if (!IsAnnotatorReady()) return;

        var key = new NetStrokeKey(author, strokeId);
        remoteStates[key] = new RemoteStrokeState
        {
            color = color,
            widthPx = widthQ / 10f,
            begun = false
        };
    }

    /// <summary>
    /// 실시간: 포인트 추가.
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.All, InvokeLocal = false)]
    private void RPC_AddPoints(int strokeId, byte[] packedPoints, RpcInfo info = default)
    {
        var author = info.Source;

        // 1) 히스토리 누적(수신자들은 여기서 누적)
        LocalRecordPoints(author, strokeId, packedPoints);

        // 2) 렌더 재생
        ReplayPackedPoints(new NetStrokeKey(author, strokeId), packedPoints);
    }

    /// <summary>
    /// 실시간: 스트로크 종료.
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.All, InvokeLocal = false)]
    private void RPC_EndStroke(int strokeId, RpcInfo info = default)
    {
        var author = info.Source;

        if (!IsAnnotatorReady()) return;

        var key = new NetStrokeKey(author, strokeId);

        if (remoteStates.TryGetValue(key, out var st) && st.begun)
        {
            var overlayKey = ToOverlayKey(key);
            annotator.EndStroke(overlayKey);
        }

        remoteStates.Remove(key);
    }

    /// <summary>
    /// 실시간: 라벨 추가.
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.All, InvokeLocal = false)]
    private void RPC_AddLabel(int labelId, ushort x, ushort y, string text, RpcInfo info = default)
    {
        // 1) 히스토리 누적(수신자)
        LocalRecordLabel(labelId, x, y, text);

        // 2) 렌더
        if (!IsAnnotatorReady()) return;
        var pos = new Vector2(StrokeNetEncoder.U16ToFloat01(x), StrokeNetEncoder.U16ToFloat01(y));
        annotator.AddText(pos, text);
    }

    /// <summary>
    /// 실시간: 전체 Clear.
    /// - InvokeLocal=false 이므로 "보낸 사람"은 이 RPC를 받지 않습니다.
    ///   -> 보낸 사람은 SendClear()에서 로컬 Clear를 수행해야 합니다.
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.All, InvokeLocal = false)]
    private void RPC_Clear(RpcInfo info = default)
    {
        // 수신자는 여기서 Clear 수행
        LocalApplyClear();
    }

    // =============================
    // 내부 유틸: 히스토리 누적/초기화
    // =============================

    /// <summary>
    /// 로컬 히스토리에 "스트로크 시작"을 누적합니다.
    /// - 모든 클라이언트가 동일한 데이터를 갖도록 하는 핵심 로직.
    /// </summary>
    private void LocalRecordBegin(PlayerRef author, int strokeId, Color32 color, ushort widthQ)
    {
        // 이미 존재하면 덮어쓰기(방어)
        int idx = storedStrokes.FindIndex(s => s.author.Equals(author) && s.strokeId == strokeId);
        if (idx >= 0)
        {
            var s = storedStrokes[idx];
            s.color = color;
            s.widthPx = widthQ / 10f;
            if (s.points == null) s.points = new List<Vector2>(256);
            storedStrokes[idx] = s;
            return;
        }

        storedStrokes.Add(new StoredStroke
        {
            author = author,
            strokeId = strokeId,
            color = color,
            widthPx = widthQ / 10f,
            points = new List<Vector2>(256)
        });
    }

    /// <summary>
    /// 로컬 히스토리에 "포인트"를 누적합니다.
    /// - packedPoints를 unpack해서 points 리스트에 추가합니다.
    /// </summary>
    private void LocalRecordPoints(PlayerRef author, int strokeId, byte[] packedPoints)
    {
        int idx = storedStrokes.FindIndex(s => s.author.Equals(author) && s.strokeId == strokeId);
        if (idx < 0)
        {
            // Begin이 먼저 와야 정상이나, 네트워크 상황에 대비해 방어적으로 Begin을 생성합니다.
            LocalRecordBegin(author, strokeId, new Color32(255, 0, 0, 255), (ushort)(3f * 10f));
            idx = storedStrokes.FindIndex(s => s.author.Equals(author) && s.strokeId == strokeId);
            if (idx < 0) return;
        }

        StrokeNetEncoder.UnpackPoints(packedPoints, unpackBuffer);
        storedStrokes[idx].points.AddRange(unpackBuffer);
    }

    /// <summary>
    /// 로컬 히스토리에 "라벨"을 누적합니다.
    /// </summary>
    private void LocalRecordLabel(int labelId, ushort x, ushort y, string text)
    {
        var pos = new Vector2(StrokeNetEncoder.U16ToFloat01(x), StrokeNetEncoder.U16ToFloat01(y));
        storedLabels.Add(new StoredLabel { labelId = labelId, pos = pos, text = text });
    }

    /// <summary>
    /// 로컬 히스토리 + 로컬 렌더를 모두 초기화합니다.
    /// - Clear 버튼 요구사항을 만족시키는 핵심 함수.
    /// </summary>
    private void LocalApplyClear()
    {
        storedStrokes.Clear();
        storedLabels.Clear();
        remoteStates.Clear();

        if (!IsAnnotatorReady()) return;
        annotator.Clear();
    }

    // =============================
    // 내부 유틸: 렌더 재생
    // =============================

    /// <summary>
    /// 네트워크 스트로크 키(NetStrokeKey)를 annotator용 키(OverlayStrokeKey)로 변환합니다.
    ///
    /// - AuthorId는 PlayerRef.GetHashCode()를 사용합니다.
    /// - 이 값은 "세션 동안 동일 PlayerRef에 대해 일관"되게 유지되므로
    ///   annotator 내부에서 author를 구분하기 위한 용도로 충분합니다.
    /// </summary>
    private static OverlayStrokeKey ToOverlayKey(NetStrokeKey key)
    {
        int authorId = key.author.GetHashCode();
        return new OverlayStrokeKey(authorId, key.strokeId);
    }

    /// <summary>
    /// packedPoints를 unpack하고, 해당 스트로크에 포인트를 재생합니다.
    ///
    /// - BeginStroke 지연 시작 정책:
    ///   아직 begun=false라면 "첫 포인트 도착 시점"에 BeginStroke를 호출합니다.
    ///   이런 방식은 네트워크 순서 문제(포인트가 먼저 오는 경우 등)에 강합니다.
    /// </summary>
    private void ReplayPackedPoints(NetStrokeKey key, byte[] packedPoints)
    {
        if (!IsAnnotatorReady()) return;
        if (packedPoints == null || packedPoints.Length < 4) return;

        if (!remoteStates.TryGetValue(key, out var st))
        {
            // BeginStroke가 먼저 와야 정상이지만, 예외 상황 방어
            st = new RemoteStrokeState
            {
                color = new Color32(255, 0, 0, 255),
                widthPx = 3f,
                begun = false
            };
            remoteStates[key] = st;
        }

        var overlayKey = ToOverlayKey(key);

        // 첫 포인트 시점에 BeginStroke
        if (!st.begun)
        {
            annotator.BeginStroke(overlayKey, st.color, st.widthPx);
            st.begun = true;
            remoteStates[key] = st;
        }

        StrokeNetEncoder.UnpackPoints(packedPoints, unpackBuffer);
        for (int i = 0; i < unpackBuffer.Count; i++)
            annotator.AddStrokePoint(overlayKey, unpackBuffer[i]);
    }
}
