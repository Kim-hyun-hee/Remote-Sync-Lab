using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;
using UnityEngine;

/// <summary>
/// "씬에 배치된 Hub(Scene NetworkObject)"를 사용하는 Shared Mode 부트스트랩.
///
/// 포인트:
/// 1) Hub는 프리팹 Spawn하지 않는다. (씬에 이미 존재)
/// 2) Scene Object가 네트워크에서 활성화되려면 SceneManager가 필요할 수 있다.
/// 3) Shared Mode에서 마스터가 바뀌어도 Hub가 유지되려면
///    Hub NetworkObject의 "Destroy When State Authority Leaves"를 꺼야 한다.
/// 4) AppId(Photon Fusion) 설정이 비어있으면 StartGame 자체가 실패한다.
/// </summary>
public class FusionBootstrapSceneHub : MonoBehaviour, INetworkRunnerCallbacks
{
    [Header("Fusion")]
    [SerializeField] private NetworkRunner runner;

    [Tooltip("Shared 룸 이름")]
    [SerializeField] private string roomName = "OverlayRoom";

    private async void Start()
    {
        if (runner == null)
            runner = gameObject.AddComponent<NetworkRunner>();

        // Scene Object 파이프라인을 위해 기본 SceneManager를 붙여준다.
        // (이미 붙어 있으면 중복 추가 안 함)
        if (runner.GetComponent<NetworkSceneManagerDefault>() == null)
            runner.gameObject.AddComponent<NetworkSceneManagerDefault>();

        runner.ProvideInput = true;
        runner.AddCallbacks(this);

        var args = new StartGameArgs
        {
            GameMode = GameMode.Shared,
            SessionName = roomName,

            // 중요: Scene Object(씬에 있는 NetworkObject) 사용 시 보통 필요
            SceneManager = runner.GetComponent<NetworkSceneManagerDefault>(),

            // 씬 buildIndex를 넣어 Scene 로딩/동기 파이프라인을 확실히 탄다
            Scene = SceneRef.FromIndex(0),
        };

        var result = await runner.StartGame(args);
        Debug.Log($"[FusionBootstrapSceneHub] StartGame ok={result.Ok}");

        if (!result.Ok)
        {
            Debug.LogError("[FusionBootstrapSceneHub] StartGame failed. (AppId 비었는지 / 네트워크 설정 확인)");
            return;
        }

        // 씬에 Hub가 실제로 존재하는지 확인(논리 체크용)
        var hub = FindFirstObjectByType<AnnotationHubOptimized>();
        if (hub == null)
            Debug.LogError("[FusionBootstrapSceneHub] Scene에 AnnotationHubOptimized가 없습니다. Hub 오브젝트가 씬에 있어야 합니다.");
        else
            Debug.Log("[FusionBootstrapSceneHub] Scene Hub found. 네트워크 스폰은 Hub의 Spawned()에서 확인하세요.");
    }

    // --------------------------------------------------------------------
    // INetworkRunnerCallbacks (필수 구현: 버전별로 멤버가 늘어남)
    // --------------------------------------------------------------------

    public void OnPlayerJoined(NetworkRunner runner, PlayerRef player) { }
    public void OnPlayerLeft(NetworkRunner runner, PlayerRef player) { }
    public void OnInput(NetworkRunner runner, NetworkInput input) { }
    public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
    public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason) { }
    public void OnConnectedToServer(NetworkRunner runner) { }
    public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
    public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
    public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
    public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
    public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
    public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
    public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
    public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
    public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
    public void OnSceneLoadDone(NetworkRunner runner) { }
    public void OnSceneLoadStart(NetworkRunner runner) { }
    public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
}
