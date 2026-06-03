using FishNet;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

namespace Network
{
    /// <summary>
    /// Scene-level singleton that wraps FishNet's NetworkManager and exposes a
    /// simple host / client / disconnect API used by NetworkLobbyUI and the rest
    /// of the networking layer.
    ///
    /// Setup in Unity Editor:
    ///   1. Add a GameObject "NetworkGameManager" to FieldScene.
    ///   2. Attach this component AND FishNet's NetworkManager component to it.
    ///   3. Configure NetworkManager: add Tugboat transport, set default port 7770.
    ///   4. The NetworkManager's ServerManager and ClientManager are auto-found.
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public class NetworkGameManager : MonoBehaviour
    {
        public static NetworkGameManager Instance { get; private set; }

        private NetworkManager _nm;

        // ── Events broadcast to the rest of the game ──────────────────────────
        public static event System.Action OnHostStarted;
        public static event System.Action OnClientConnected;
        public static event System.Action OnDisconnected;

        // ── State ──────────────────────────────────────────────────────────────
        public bool IsOnline  => _nm != null && (_nm.IsServer || _nm.IsClient);
        public bool IsHost    => _nm != null && _nm.IsHost;
        public bool IsServer  => _nm != null && _nm.IsServer;
        public bool IsClient  => _nm != null && _nm.IsClient;

        // ── Lifecycle ──────────────────────────────────────────────────────────
        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            _nm = GetComponent<NetworkManager>();
        }

        private void OnEnable()
        {
            if (_nm == null) return;
            _nm.ServerManager.OnServerConnectionState += OnServerState;
            _nm.ClientManager.OnClientConnectionState += OnClientState;
        }

        private void OnDisable()
        {
            if (_nm == null) return;
            _nm.ServerManager.OnServerConnectionState -= OnServerState;
            _nm.ClientManager.OnClientConnectionState -= OnClientState;
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>Start a host session (server + local client on this machine).</summary>
        public void StartHost()
        {
            _nm.ServerManager.StartConnection();
            _nm.ClientManager.StartConnection();
        }

        /// <summary>Join a remote host at the given IP address.</summary>
        public void StartClient(string address)
        {
            // Tugboat (default FishNet UDP transport) exposes SetClientAddress.
            // If you switch transports, update this cast.
            if (_nm.TransportManager.Transport is Tugboat tugboat)
                tugboat.SetClientAddress(address);

            _nm.ClientManager.StartConnection();
        }

        /// <summary>Stop all connections gracefully.</summary>
        public void Disconnect()
        {
            if (_nm.IsServer) _nm.ServerManager.StopConnection(true);
            if (_nm.IsClient) _nm.ClientManager.StopConnection(false);
        }

        // ── Callbacks ──────────────────────────────────────────────────────────
        private void OnServerState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started)
                OnHostStarted?.Invoke();
            else if (args.ConnectionState == LocalConnectionState.Stopped)
                OnDisconnected?.Invoke();
        }

        private void OnClientState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started)
                OnClientConnected?.Invoke();
            else if (args.ConnectionState == LocalConnectionState.Stopped)
                OnDisconnected?.Invoke();
        }
    }
}
