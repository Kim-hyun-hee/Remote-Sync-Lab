using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;
using UnityEngine;

/// <summary>
/// Fusion 네트워크 러너를 시작하고, 콜백 기반으로 세션 상태를 관찰하는 부트스트랩.
///
/// ============================
/// Shared Mode 기초 개념
/// ============================
/// - GameMode.Shared:
///   별도의 전용 서버 프로세스 없이, 참가자 중 1명이 "StateAuthority(마스터)" 역할을 맡습니다.
///   이 마스터는 Photon Cloud가 아니라 "참가자 중 한 클라이언트"입니다.
///
/// - SharedModeMasterClient:
///   현재 StateAuthority를 가진 클라이언트.
///   보통 "먼저 들어온 사람"이 마스터가 되지만,
///   마스터가 나가면 남아 있는 사람 중 한 명이 새 마스터가 됩니다.
///
/// ============================
/// 본 프로젝트의 핵심 정책
/// ============================
/// - AnnotationHub는 씬 오브젝트(Scene Network Object)로 유지한다.
/// - 마스터가 바뀌어도 Hub는 사라지지 않는다(권한만 바뀜).
///
/// 따라서 Bootstrap은 Hub를 Spawn하지 않습니다.
/// (Spawn 방식은 마스터 이탈 시 Despawn 이슈를 만들 가능성이 커서 실무적으로 비추천)
/// </summary>
public class FusionBootstrap : MonoBehaviour, INetworkRunnerCallbacks
{
    [Header("Session")]
    [SerializeField] private string sessionName = "AnnotationRoom";

    private NetworkRunner runner;

    private async void Start()
    {
        runner = gameObject.AddComponent<NetworkRunner>();

        // 드로잉은 "입력 예측" 같은 게 필요 없으므로 ProvideInput=false로 둡니다.
        runner.ProvideInput = false;

        // 콜백 등록(업데이트 폴링 대신 콜백 기반으로 관찰)
        runner.AddCallbacks(this);

        // 씬 네트워크 오브젝트를 관리하는 기본 SceneManager
        var sceneManager = gameObject.AddComponent<NetworkSceneManagerDefault>();

        var result = await runner.StartGame(new StartGameArgs
        {
            GameMode = GameMode.Shared,
            SessionName = sessionName,
            SceneManager = sceneManager,
            Scene = SceneRef.FromIndex(0)
        });

        if (!result.Ok)
        {
            Debug.LogError($"Fusion StartGame failed: {result.ShutdownReason}");
            return;
        }

        Debug.Log($"[FusionBootstrap] StartGame OK. Session={sessionName}, LocalPlayer={runner.LocalPlayer}");
    }

    // ============================
    // INetworkRunnerCallbacks
    // ============================

    public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
    {
        Debug.Log($"[Fusion] adding player [{player}] (Local={runner.LocalPlayer}, IsMaster={runner.IsSharedModeMasterClient})");
        // Hub는 Scene Object이므로 여기서 Spawn/Respawn 로직이 필요 없습니다.
        // Late Join 스냅샷은 AnnotationHub.Spawned()에서 RequestSnapshot()이 자동 호출됩니다.
    }

    public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
    {
        Debug.Log($"[Fusion] player left [{player}] (Local={runner.LocalPlayer}, IsMaster={runner.IsSharedModeMasterClient})");
        // 마스터가 나가면 IsSharedModeMasterClient가 다른 클라에서 true가 될 수 있습니다.
        // 하지만 히스토리는 모든 클라이언트에 누적되어 있으므로,
        // 새 마스터도 스냅샷을 정상적으로 제공할 수 있습니다.
    }

    public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
    {
        Debug.LogWarning($"[Fusion] shutdown: {shutdownReason}");
    }

    public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
    {
        Debug.LogWarning($"[Fusion] disconnected: {reason}");
    }

    public void OnConnectedToServer(NetworkRunner runner)
    {
        Debug.Log($"[Fusion] connected to server.");
    }

    public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason)
    {
        Debug.LogError($"[Fusion] connect failed: {remoteAddress} reason={reason}");
    }

    public void OnInput(NetworkRunner runner, NetworkInput input) { }
    public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
    public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
    public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
    public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
    public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
    public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, System.ArraySegment<byte> data) { }
    public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
    public void OnSceneLoadDone(NetworkRunner runner) { }
    public void OnSceneLoadStart(NetworkRunner runner) { }
    public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }

    public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
}
