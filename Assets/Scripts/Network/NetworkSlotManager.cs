using System.Collections.Generic;
using FishNet;
using FishNet.Connection;
using FishNet.Managing.Server;
using FishNet.Transporting;
using UnityEngine;

/// <summary>
/// Tracks the connection -> player-slot mapping for the current session.
/// Plain MonoBehaviour — no NetworkObject required.
/// </summary>
public class NetworkSlotManager : MonoBehaviour
{
    public static NetworkSlotManager Instance { get; private set; }

    private readonly Dictionary<NetworkConnection, int> _slots = new();
    private int _nextSlot;

    private void Awake() => Instance = this;

    private void OnEnable()
    {
        if (InstanceFinder.ServerManager != null)
            InstanceFinder.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
    }

    private void OnDisable()
    {
        if (InstanceFinder.ServerManager != null)
            InstanceFinder.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
    }

    /// <summary>Call this when the server starts to register the host as slot 0.</summary>
    public void OnServerStarted()
    {
        _slots.Clear();
        _nextSlot = 0;

        var localConn = InstanceFinder.ClientManager?.Connection;
        if (localConn != null && localConn.IsValid)
        {
            _slots[localConn] = _nextSlot++;
            Debug.Log($"[SlotManager] Host assigned slot 0");
        }
    }

    private void OnRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
    {
        if (args.ConnectionState == RemoteConnectionState.Started)
        {
            int slot = _nextSlot++;
            _slots[conn] = slot;
            Debug.Log($"[SlotManager] Remote client assigned slot {slot}");
        }
        else if (args.ConnectionState == RemoteConnectionState.Stopped)
        {
            _slots.Remove(conn);
        }
    }

    public NetworkConnection GetConnectionForSlot(int slot)
    {
        foreach (var kv in _slots)
            if (kv.Value == slot) return kv.Key;
        return null;
    }

    public int GetSlotForConnection(NetworkConnection conn)
        => _slots.TryGetValue(conn, out int s) ? s : -1;
}
