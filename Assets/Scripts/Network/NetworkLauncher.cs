using Fusion;
using Fusion.Sockets;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Boots Fusion, collects input from IInputService, spawns players.
/// Attach to a "NetworkManager" GameObject in scene.
/// </summary>
public class NetworkLauncher : MonoBehaviour, INetworkRunnerCallbacks
{
    [Header("Player Prefab")]
    public GameObject playerPrefab;

    [Header("Input")]
    [SerializeField] private MonoBehaviour inputServiceObject;

    private NetworkRunner runner;
    private IInputService inputService;

    /// <summary>The same service when it is the router, which also carries menu picks.</summary>
    private InputRouter router;

    [Header("Startup")]
    [Tooltip("Off when a menu decides when to start. On to jump straight in.")]
    public bool startOnPlay;

    public bool IsRunning => runner != null && runner.IsRunning;

    private void Start()
    {
        ResolveInputService();
        if (startOnPlay) StartSession(GameMode.AutoHostOrClient, "room1");
    }

    private void ResolveInputService()
    {
        inputService = inputServiceObject as IInputService;

        if (inputService == null)
        {
            // Auto-find any IInputService in scene
            foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (mb is IInputService svc)
                {
                    inputService = svc;
                    break;
                }
            }
        }

        if (inputService == null)
            Debug.LogWarning("[NetworkLauncher] No IInputService found!");

        router = inputService as InputRouter;
        // The router carries the menu picks (weapon, item, emote, car); wired straight to a device service — as a
        // scene rebuild without the gameplay step leaves it — those never reached the network. Use it if it is there.
        if (router == null)
        {
            router = FindFirstObjectByType<InputRouter>();
            if (router != null) inputService = router;
        }
    }

    /// <summary>
    /// Starts a session. Single player uses a private room name so two people
    /// playing solo never land in the same match.
    /// </summary>
    public async void StartSession(GameMode mode, string sessionName)
    {
        if (IsRunning) return;
        ResolveInputService();

        runner = gameObject.GetComponent<NetworkRunner>();
        if (runner == null) runner = gameObject.AddComponent<NetworkRunner>();
        runner.AddCallbacks(this);
        runner.ProvideInput = true;

        var result = await runner.StartGame(new StartGameArgs
        {
            GameMode = mode,
            SessionName = sessionName,
            Scene = SceneRef.FromIndex(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex
            )
        });

        if (!result.Ok)
            Debug.LogError($"[NetworkLauncher] {result.ShutdownReason}");
    }

    public void StopSession()
    {
        if (runner != null) runner.Shutdown();
    }

    public void OnInput(NetworkRunner runner, NetworkInput input)
    {
        if (inputService == null) return;

        var data = new NetworkInputData
        {
            moveInput = inputService.MoveInput,
            lookInput = inputService.LookInput,
            fire = inputService.FireHeld,
            aim = inputService.AimHeld,
            jump = inputService.JumpPressed,
            reload = inputService.ReloadPressed,
            weapon = (byte)(router != null ? router.WeaponPick : 0),
            emote = (byte)(router != null ? router.EmotePick : 0),
            item = (byte)(router != null ? router.ItemPick : Consumable.None),
            car = router != null && router.CarPick,
            pedals = SimpleCar.LocalPedals
        };

        inputService.ResetOneShots();
        input.Set(data);
    }

    public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
    {
        if (runner.IsServer)
            runner.Spawn(playerPrefab, GetSpawnPosition(), Quaternion.identity, player);
    }

    /// <summary>
    /// Drops the spawn point onto the map floor instead of a fixed height.
    /// Takes the highest surface under the origin — the underside of the level
    /// is also a hit, and aiming for the lowest one drops the player through
    /// the floor. The safety net is skipped so we land on real geometry.
    /// </summary>
    public Vector3 GetSpawnPosition()
    {
        const float castHeight = 200f;

        var hits = Physics.RaycastAll(
            new Vector3(0f, castHeight, 0f), Vector3.down, castHeight * 2f);

        float floorY = float.MinValue;
        foreach (var hit in hits)
        {
            if (hit.collider.gameObject.name == "SafetyFloor") continue;
            if (hit.point.y > floorY) floorY = hit.point.y;
        }

        if (floorY == float.MinValue)
        {
            Debug.LogWarning("[NetworkLauncher] No floor found under origin — spawning at y=2.");
            return new Vector3(0f, 2f, 0f);
        }

        Debug.Log($"[NetworkLauncher] Floor found at y={floorY:F2}, spawning just above it.");
        return new Vector3(0f, floorY + 0.1f, 0f);
    }

    // --- Required empty callbacks ---
    public void OnPlayerLeft(NetworkRunner r, PlayerRef p) { }
    public void OnShutdown(NetworkRunner r, ShutdownReason s) { }
    public void OnConnectedToServer(NetworkRunner r) { }
    public void OnDisconnectedFromServer(NetworkRunner r, NetDisconnectReason reason) { }
    public void OnConnectFailed(NetworkRunner r, NetAddress a, NetConnectFailedReason reason) { }
    public void OnSessionListUpdated(NetworkRunner r, List<SessionInfo> l) { }
    public void OnCustomAuthenticationResponse(NetworkRunner r, Dictionary<string, object> d) { }
    public void OnReliableDataReceived(NetworkRunner r, PlayerRef p, ReliableKey k, ArraySegment<byte> d) { }
    public void OnReliableDataProgress(NetworkRunner r, PlayerRef p, ReliableKey k, float prog) { }
    public void OnUserSimulationMessage(NetworkRunner r, SimulationMessagePtr m) { }
    public void OnObjectEnterAOI(NetworkRunner r, NetworkObject o, PlayerRef p) { }
    public void OnObjectExitAOI(NetworkRunner r, NetworkObject o, PlayerRef p) { }
    public void OnConnectRequest(NetworkRunner r, NetworkRunnerCallbackArgs.ConnectRequest req, byte[] t) { req.Accept(); }
    public void OnInputMissing(NetworkRunner r, PlayerRef p, NetworkInput i) { }
    public void OnHostMigration(NetworkRunner r, HostMigrationToken t) { }
    public void OnSceneLoadStart(NetworkRunner r) { }
    public void OnSceneLoadDone(NetworkRunner r) { }
}
