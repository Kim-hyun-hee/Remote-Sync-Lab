using System.Collections.Generic;
using Fusion;
using UnityEngine;

/// <summary>
/// 네트워크상에서 드로잉 이벤트를 중계/저장/재생하는 허브(NetworkBehaviour).
/// 
/// 기존 문제:
/// - remoteStates가 strokeId만 키로 사용 → 서로 다른 사용자가 strokeId=1을 동시에 쓰면 충돌.
/// - annotator도 "현재 스트로크 1개" 구조라서, 포인트가 섞이면 선이 이어짐.
/// 
/// 해결:
/// - 네트워크 키를 (RpcInfo.Source, strokeId)로 잡는다.
///   - Source는 "이 RPC를 보낸 플레이어".
///   - strokeId는 해당 플레이어가 로컬에서 증가시키는 번호.
/// - 이렇게 하면 서로 다른 플레이어가 같은 strokeId(1,2,3...)를 써도 충돌 없음.
/// - annotator 호출도 OverlayStrokeKey(authorId, strokeId)로 분리하여 동시 드로잉이 섞이지 않게 한다.
/// </summary>
public class AnnotationHub : NetworkBehaviour
{
    /// <summary>
    /// 런타임 싱글턴(간단 테스트용).
    /// </summary>
    public static AnnotationHub Instance { get; private set; }

    /// <summary>
    /// 실제 렌더링 구현체(UI Toolkit/Texture2D 등).
    /// </summary>
    private IOverlayAnnotator annotator;

    /// <summary>
    /// 네트워크 준비 여부.
    /// - Runner/Object가 Spawned된 뒤에만 true.
    /// </summary>
    public bool IsNetworkReady => Runner != null && Object != null;

    // ============================
    // 저장소(스냅샷용)
    // ============================

    /// <summary>
    /// 서버(Shared에서는 마스터)가 저장하는 스트로크 목록.
    /// - Late Join 시 이 목록을 다시 전송(리플레이)한다.
    /// </summary>
    private readonly List<StoredStroke> storedStrokes = new();

    /// <summary>
    /// 서버가 저장하는 라벨 목록.
    /// </summary>
    private readonly List<StoredLabel> storedLabels = new();

    /// <summary>
    /// 스냅샷 저장용 스트로크 데이터.
    /// - author(보낸 플레이어)까지 저장해야 strokeId 충돌을 피할 수 있다.
    /// </summary>
    private struct StoredStroke
    {
        /// <summary>스트로크 작성자</summary>
        public PlayerRef author;

        /// <summary>작성자가 부여한 스트로크 ID</summary>
        public int strokeId;

        /// <summary>색</summary>
        public Color32 color;

        /// <summary>두께(px)</summary>
        public float widthPx;

        /// <summary>포인트들(정규화 0~1)</summary>
        public List<Vector2> points;
    }

    /// <summary>
    /// 스냅샷 저장용 라벨 데이터.
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
    /// - author + strokeId 조합
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
        {
            return System.HashCode.Combine(author.GetHashCode(), strokeId);
        }
    }

    /// <summary>
    /// 원격 스트로크의 시작/스타일 정보를 보관.
    /// - 실제 BeginStroke는 첫 포인트가 도착했을 때 호출(지연 시작).
    /// </summary>
    private struct RemoteStrokeState
    {
        public Color32 color;
        public float widthPx;
        public bool begun;
    }

    /// <summary>
    /// 원격 스트로크 상태 맵.
    /// - key가 (author, strokeId)이므로 동시 드로잉이 절대 섞이지 않는다.
    /// </summary>
    private readonly Dictionary<NetStrokeKey, RemoteStrokeState> remoteStates = new();

    /// <summary>
    /// packedPoints 언팩 결과를 재사용하기 위한 버퍼(할당 최소화).
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

        // Late Join 대비: 접속 직후 스냅샷 요청
        RequestSnapshot();
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        if (Instance == this) Instance = null;
        base.Despawned(runner, hasState);
    }

    /// <summary>
    /// Annotator를 찾는 로직.
    /// - Anchor 우선(권장)
    /// - 없으면 Scene에서 IOverlayAnnotator를 구현한 MonoBehaviour를 탐색
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
    /// Annotator가 실제로 사용 가능한 상태인지 확인.
    /// </summary>
    private bool IsAnnotatorReady()
        => annotator != null && annotator.IsReady;

    // =============================
    // Bridge에서 호출하는 API (전송)
    // =============================

    /// <summary>
    /// 스트로크 시작 전송.
    /// </summary>
    public void SendBeginStroke(int strokeId, Color32 color, float widthPx)
    {
        if (!IsNetworkReady) return;

        // 네트워크 전송량을 줄이기 위해 width를 0.1px 단위로 양자화(ushort)
        ushort widthQ = (ushort)Mathf.Clamp(Mathf.RoundToInt(widthPx * 10f), 1, 4000);
        RPC_BeginStroke(strokeId, color, widthQ);
    }

    /// <summary>
    /// 포인트 청크 전송.
    /// </summary>
    public void SendAddPointsChunk(int strokeId, byte[] packedPoints)
    {
        if (!IsNetworkReady) return;
        RPC_AddPoints(strokeId, packedPoints);
    }

    /// <summary>
    /// 스트로크 종료 전송.
    /// </summary>
    public void SendEndStroke(int strokeId)
    {
        if (!IsNetworkReady) return;
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

        RPC_AddLabel(labelId, x, y, text);
    }

    /// <summary>
    /// 전체 클리어 전송.
    /// </summary>
    public void SendClear()
    {
        if (!IsNetworkReady) return;
        RPC_Clear();
    }

    // =============================
    // Late Join: Snapshot
    // =============================

    /// <summary>
    /// 스냅샷 요청.
    /// - 내(LocalPlayer)에게 저장된 스트로크/라벨을 다시 보내달라고 StateAuthority에 요청.
    /// </summary>
    public void RequestSnapshot()
    {
        if (!IsNetworkReady) return;
        RPC_RequestSnapshot(Runner.LocalPlayer);
    }

    /// <summary>
    /// 스냅샷 요청 RPC(모든 클라이언트 -> StateAuthority).
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestSnapshot(PlayerRef requester, RpcInfo info = default)
    {
        if (!Object.HasStateAuthority) return;

        // 1) requester 화면을 먼저 clear
        RPC_SnapshotClear(requester);

        // 2) 라벨 전송
        foreach (var lbl in storedLabels)
        {
            ushort x = StrokeNetEncoder.Float01ToU16(lbl.pos.x);
            ushort y = StrokeNetEncoder.Float01ToU16(lbl.pos.y);
            RPC_SnapshotAddLabel(requester, lbl.labelId, x, y, lbl.text);
        }

        // 3) 스트로크 전송(작성자+strokeId 포함)
        foreach (var s in storedStrokes)
        {
            ushort widthQ = (ushort)Mathf.Clamp(Mathf.RoundToInt(s.widthPx * 10f), 1, 4000);

            RPC_SnapshotBeginStroke(requester, s.author, s.strokeId, s.color, widthQ);

            const int MAX_POINTS_PER_RPC = 64;
            StrokeNetEncoder.ForEachChunk(s.points, MAX_POINTS_PER_RPC, (start, count) =>
            {
                var temp = new Vector2[count];
                for (int i = 0; i < count; i++) temp[i] = s.points[start + i];

                var packed = StrokeNetEncoder.PackPoints(temp);
                RPC_SnapshotAddPoints(requester, s.author, s.strokeId, packed);
            });

            RPC_SnapshotEndStroke(requester, s.author, s.strokeId);
        }
    }

    /// <summary>
    /// 스냅샷: 대상 클라이언트만 Clear 수행.
    /// </summary>
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_SnapshotClear(PlayerRef target, RpcInfo info = default)
    {
        if (Runner.LocalPlayer != target) return;
        if (!IsAnnotatorReady()) return;

        remoteStates.Clear();
        annotator.Clear();
    }

    /// <summary>
    /// 스냅샷: 라벨 추가(대상 클라이언트만).
    /// </summary>
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_SnapshotAddLabel(PlayerRef target, int labelId, ushort x, ushort y, string text, RpcInfo info = default)
    {
        if (Runner.LocalPlayer != target) return;
        if (!IsAnnotatorReady()) return;

        var pos = new Vector2(StrokeNetEncoder.U16ToFloat01(x), StrokeNetEncoder.U16ToFloat01(y));
        annotator.AddText(pos, text);
    }

    /// <summary>
    /// 스냅샷: 스트로크 시작 정보 저장(대상 클라이언트만).
    /// - 실제 BeginStroke는 포인트가 올 때 수행(지연 시작).
    /// </summary>
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_SnapshotBeginStroke(PlayerRef target, PlayerRef author, int strokeId, Color32 color, ushort widthQ, RpcInfo info = default)
    {
        if (Runner.LocalPlayer != target) return;
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
    /// 스냅샷: 포인트 추가(대상 클라이언트만).
    /// </summary>
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_SnapshotAddPoints(PlayerRef target, PlayerRef author, int strokeId, byte[] packedPoints, RpcInfo info = default)
    {
        if (Runner.LocalPlayer != target) return;
        ReplayPackedPoints(new NetStrokeKey(author, strokeId), packedPoints);
    }

    /// <summary>
    /// 스냅샷: 스트로크 종료(대상 클라이언트만).
    /// </summary>
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
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
    /// - InvokeLocal=false 이므로 "보낸 사람(로컬)"은 이 RPC를 다시 받지 않는다.
    /// - info.Source는 "이 RPC를 보낸 플레이어".
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.All, InvokeLocal = false)]
    private void RPC_BeginStroke(int strokeId, Color32 color, ushort widthQ, RpcInfo info = default)
    {
        var author = info.Source;
        var key = new NetStrokeKey(author, strokeId);

        // 스냅샷 저장은 StateAuthority(Shared에서는 마스터)만 수행
        if (Object.HasStateAuthority)
        {
            storedStrokes.Add(new StoredStroke
            {
                author = author,
                strokeId = strokeId,
                color = color,
                widthPx = widthQ / 10f,
                points = new List<Vector2>(256)
            });
        }

        if (!IsAnnotatorReady()) return;

        // 아직 포인트가 오기 전이므로 스타일만 저장
        remoteStates[key] = new RemoteStrokeState
        {
            color = color,
            widthPx = widthQ / 10f,
            begun = false
        };
    }

    /// <summary>
    /// 실시간: 포인트 추가.
    /// - info.Source + strokeId로 정확히 어떤 스트로크인지 결정.
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.All, InvokeLocal = false)]
    private void RPC_AddPoints(int strokeId, byte[] packedPoints, RpcInfo info = default)
    {
        var author = info.Source;
        var key = new NetStrokeKey(author, strokeId);

        // 스냅샷 저장(권한자만)
        if (Object.HasStateAuthority)
        {
            int idx = storedStrokes.FindIndex(s => s.author.Equals(author) && s.strokeId == strokeId);
            if (idx >= 0)
            {
                StrokeNetEncoder.UnpackPoints(packedPoints, unpackBuffer);
                storedStrokes[idx].points.AddRange(unpackBuffer);
            }
        }

        // 실제 렌더 재생
        ReplayPackedPoints(key, packedPoints);
    }

    /// <summary>
    /// 실시간: 스트로크 종료.
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.All, InvokeLocal = false)]
    private void RPC_EndStroke(int strokeId, RpcInfo info = default)
    {
        var author = info.Source;
        var key = new NetStrokeKey(author, strokeId);

        if (!IsAnnotatorReady()) return;

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
        var pos = new Vector2(StrokeNetEncoder.U16ToFloat01(x), StrokeNetEncoder.U16ToFloat01(y));

        if (Object.HasStateAuthority)
            storedLabels.Add(new StoredLabel { labelId = labelId, pos = pos, text = text });

        if (!IsAnnotatorReady()) return;
        annotator.AddText(pos, text);
    }

    /// <summary>
    /// 실시간: 전체 클리어.
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.All, InvokeLocal = false)]
    private void RPC_Clear(RpcInfo info = default)
    {
        if (Object.HasStateAuthority)
        {
            storedStrokes.Clear();
            storedLabels.Clear();
        }

        remoteStates.Clear();

        if (!IsAnnotatorReady()) return;
        annotator.Clear();
    }

    // =============================
    // 내부 유틸
    // =============================

    /// <summary>
    /// 네트워크 스트로크 키(NetStrokeKey) -> annotator용 키(OverlayStrokeKey)로 변환.
    /// - AuthorId는 PlayerRef.GetHashCode() 값을 사용.
    /// - 이 값은 런타임 동안 동일 PlayerRef에 대해 일관되게 유지되므로,
    ///   annotator에서 스트로크를 구분하는 용도로 충분하다.
    /// </summary>
    private static OverlayStrokeKey ToOverlayKey(NetStrokeKey key)
    {
        int authorId = key.author.GetHashCode();
        return new OverlayStrokeKey(authorId, key.strokeId);
    }

    /// <summary>
    /// packedPoints를 언팩하고, 해당 스트로크에 점들을 재생한다.
    /// - 이 함수가 동시 드로잉 분리의 핵심:
    ///   key가 (author, strokeId)이므로 섞일 수 없다.
    /// </summary>
    private void ReplayPackedPoints(NetStrokeKey key, byte[] packedPoints)
    {
        if (!IsAnnotatorReady()) return;
        if (packedPoints == null || packedPoints.Length < 4) return;

        if (!remoteStates.TryGetValue(key, out var st))
        {
            // BeginStroke가 먼저 와야 정상이지만,
            // 네트워크 상황에 따라 예외 케이스 방어용 기본값.
            st = new RemoteStrokeState
            {
                color = new Color32(255, 0, 0, 255),
                widthPx = 3f,
                begun = false
            };
            remoteStates[key] = st;
        }

        var overlayKey = ToOverlayKey(key);

        // 아직 BeginStroke를 실제로 호출하지 않았다면, 첫 포인트 시점에 Begin 호출
        if (!st.begun)
        {
            annotator.BeginStroke(overlayKey, st.color, st.widthPx);
            st.begun = true;
            remoteStates[key] = st;
        }

        // 포인트 언팩 후 스트로크에 추가
        StrokeNetEncoder.UnpackPoints(packedPoints, unpackBuffer);
        for (int i = 0; i < unpackBuffer.Count; i++)
            annotator.AddStrokePoint(overlayKey, unpackBuffer[i]);
    }
}
