using Fusion;
using Fusion.Sockets;
using UnityEngine;

/// <summary>
/// Fusion 세션을 시작하고, Shared Mode 마스터만 AnnotationHub를 Spawn하는 부트스트랩.
/// 
/// - GameMode.Shared:
///   서버가 별도로 있는 구조가 아니라, 참가자 중 "마스터"가 사실상 권한자 역할을 수행.
/// - runner.IsSharedModeMasterClient:
///   현재 클라이언트가 마스터이면 true.
/// </summary>
public class FusionBootstrap : MonoBehaviour
{
    /// <summary>세션(방) 이름</summary>
    [SerializeField] private string sessionName = "AnnotationRoom";

    /// <summary>네트워크로 Spawn할 AnnotationHub 프리팹</summary>
    [SerializeField] private NetworkObject annotationHubPrefab;

    /// <summary>Fusion 네트워크 러너</summary>
    private NetworkRunner runner;

    private async void Start()
    {
        runner = gameObject.AddComponent<NetworkRunner>();

        // 드로잉은 입력 예측이 필요 없으므로 ProvideInput=false로 둔다.
        runner.ProvideInput = false;

        // 기본 씬 매니저
        var sceneManager = gameObject.AddComponent<NetworkSceneManagerDefault>();

        // 세션 시작
        var result = await runner.StartGame(new StartGameArgs
        {
            GameMode = GameMode.Shared,
            SessionName = sessionName,
            SceneManager = sceneManager,
        });

        if (!result.Ok)
        {
            Debug.LogError($"Fusion StartGame failed: {result.ShutdownReason}");
            return;
        }

        // Shared Mode에서 마스터 1명만 Hub Spawn
        if (runner.IsSharedModeMasterClient)
        {
            runner.Spawn(annotationHubPrefab);
            Debug.Log("[FusionBootstrap] Spawned AnnotationHub (master client)");
        }
    }

    // INetworkRunnerCallbacks를 붙이면 다양한 콜백을 받을 수 있으나,
    // 현재 샘플에서는 구현을 비워둠.
    public void OnPlayerJoined(NetworkRunner runner, PlayerRef player) { }
    public void OnPlayerLeft(NetworkRunner runner, PlayerRef player) { }
    public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason) { }
    public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
    public void OnConnectedToServer(NetworkRunner runner) { }
    public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
    public void OnInput(NetworkRunner runner, NetworkInput input) { }
    public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
    public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
    public void OnSessionListUpdated(NetworkRunner runner, System.Collections.Generic.List<SessionInfo> sessionList) { }
    public void OnCustomAuthenticationResponse(NetworkRunner runner, System.Collections.Generic.Dictionary<string, object> data) { }
    public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
    public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, System.ArraySegment<byte> data) { }
    public void OnSceneLoadDone(NetworkRunner runner) { }
    public void OnSceneLoadStart(NetworkRunner runner) { }
}
