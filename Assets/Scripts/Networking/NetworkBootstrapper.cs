using System;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using UnityEngine;

// Auto-creates itself on scene load — no manual scene setup required.
//
// STILL REQUIRED in the scene (add once, not auto-created):
//   1. A GameObject with NetworkManager + UnityTransport components.
//      In NetworkManager inspector set Network Transport = UnityTransport.
//   2. A GameObject with NetworkObject + PieceSyncManager.
//   3. A GameObject with NetworkObject + GameNetworkManager.
//   4. In the Unity dashboard (dashboard.unity.com) enable Relay for this project.

public class NetworkBootstrapper : MonoBehaviour
{
    public static NetworkBootstrapper Instance { get; private set; }
    public bool IsInitialized { get; private set; }

    // Spawns exactly one NetworkBootstrapper after every scene load so the UI
    // can always call Instance.StartHostAsync / StartClientAsync without needing
    // the component to be placed in the scene manually.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        var go = new GameObject("[NetworkBootstrapper]");
        DontDestroyOnLoad(go);
        go.AddComponent<NetworkBootstrapper>();
    }

    private async void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            await UnityServices.InitializeAsync();
            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            IsInitialized = true;
            Debug.Log($"[Net] Signed in as {AuthenticationService.Instance.PlayerId}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Net] Init failed: {e}");
        }
    }

    // Allocates a Relay slot, starts hosting, and returns the 6-character join code.
    public async Task<string> StartHostAsync(int maxConnections = 3)
    {
        if (!IsInitialized)
            throw new InvalidOperationException("Unity Services not yet initialised — wait a moment and try again.");

        var allocation = await RelayService.Instance.CreateAllocationAsync(maxConnections);
        var joinCode   = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.MaxPacketQueueSize = 512;
        transport.SetRelayServerData(new RelayServerData(allocation, "dtls"));

        NetworkManager.Singleton.StartHost();
        Debug.Log($"[Net] Hosting — join code: {joinCode}");
        return joinCode;
    }

    // Joins an existing Relay session using the 6-character code the host shared.
    public async Task StartClientAsync(string joinCode)
    {
        if (!IsInitialized)
            throw new InvalidOperationException("Unity Services not yet initialised — wait a moment and try again.");

        var allocation = await RelayService.Instance.JoinAllocationAsync(joinCode);

        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.MaxPacketQueueSize = 512;
        transport.SetRelayServerData(new RelayServerData(allocation, "dtls"));

        NetworkManager.Singleton.StartClient();
        Debug.Log("[Net] Client connected via Relay.");
    }
}
