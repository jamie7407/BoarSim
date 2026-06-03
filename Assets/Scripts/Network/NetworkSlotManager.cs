using System.Collections.Generic;
using FishNet;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;

/// <summary>
/// Tracks the connection → player-slot mapping for the current session.
///
/// Assignment rules:
///   Slot 0 = host (server's own local connection)
///   Slot 1 = first remote client to connect
///   Slot 2 = second remote client
///   Slot 3 = third remote client
///
/// Setup in Unity Editor:
///   Add this component (+ NetworkObject) to the same GameObject as NetworkGameManager.
///   It must be spawned before robots are spawned so LoadMatch can query it.
/// </summary>
public class NetworkSlotManager : NetworkBehaviour
{
    public static NetworkSlotManager Instance { get; private set; }

    private readonly Dictionary<NetworkConnection, int> _slots = new();
    private int _nextSlot;

    private void Awake() => Instance = this;

    // ── Server lifecycle ───────────────────────────────────────────────────────

    public override void OnStartServer()
    {
        base.OnStartServer();
        _slots.Clear();
        _nextSlot = 0;

        // Host's own connection takes slot 0
        var localConn = InstanceFinder.NetworkManager.ClientManager.Connection;
        if (localConn != null && localConn.IsValid)
        {
            _slots[localConn] = _nextSlot++;
            Debug.Log($"[SlotManager] Host assigned slot 0 (conn {localConn.ClientId})");
        }

        ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
    }

    public override void OnStopServer()
    {
        base.OnStopServer();
        ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
        _slots.Clear();
        _nextSlot = 0;
    }

    private void OnRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
    {
        if (args.ConnectionState == RemoteConnectionState.Started)
        {
            int slot = _nextSlot++;
            _slots[conn] = slot;
            Debug.Log($"[SlotManager] Remote client assigned slot {slot} (conn {conn.ClientId})");
        }
        else if (args.ConnectionState == RemoteConnectionState.Stopped)
        {
            if (_slots.TryGetValue(conn, out int slot))
            {
                Debug.Log($"[SlotManager] Slot {slot} freed (conn {conn.ClientId} disconnected)");
                _slots.Remove(conn);
            }
        }
    }

    // ── Queries (called by LoadMatch on the server) ────────────────────────────

    /// <summary>Returns the NetworkConnection that owns a given player slot, or null.</summary>
    public NetworkConnection GetConnectionForSlot(int slot)
    {
        foreach (var kv in _slots)
            if (kv.Value == slot) return kv.Key;
        return null;
    }

    /// <summary>Returns the slot index for a connection, or -1 if not found.</summary>
    public int GetSlotForConnection(NetworkConnection conn)
        => _slots.TryGetValue(conn, out int s) ? s : -1;

    public int TotalConnected => _slots.Count;
}
