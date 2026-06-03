using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;
using Util;

/// <summary>
/// Attached to the same GameObject as FMS.  The server/host runs the real FMS
/// state machine; this component mirrors its output to all clients via SyncVars.
///
/// Clients read the synced values from here instead of the static FMS fields so
/// their local match timer, state, and scores stay in step with the host.
///
/// Setup in Unity Editor:
///   Add this component to the FMS GameObject and also add a NetworkObject
///   component to that same GameObject.  Only one instance should exist in the
///   scene (FMS is a singleton-style object already).
/// </summary>
public class NetworkMatchSync : NetworkBehaviour
{
    // ── Synced match state ────────────────────────────────────────────────────
    [SyncVar(OnChange = nameof(OnMatchTimerChanged))]
    private float _matchTimer;

    [SyncVar(OnChange = nameof(OnMatchStateChanged))]
    private MatchState _matchState;

    [SyncVar(OnChange = nameof(OnRobotStateChanged))]
    private RobotState _robotState;

    // ── Synced scores ─────────────────────────────────────────────────────────
    [SyncVar(OnChange = nameof(OnBlueScoreChanged))]
    private int _blueScore;

    [SyncVar(OnChange = nameof(OnRedScoreChanged))]
    private int _redScore;

    // ── References ────────────────────────────────────────────────────────────
    private FMS _fms;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    private void Awake()
    {
        _fms = GetComponent<FMS>();
    }

    // Server pushes its state to clients every FixedUpdate.
    // Clients skip the push and just receive via SyncVar callbacks.
    private void Update()
    {
        if (!IsServer) return;

        _matchTimer = FMS.MatchTimer;
        _matchState = FMS.MatchState;
        _robotState = FMS.RobotState;
        _blueScore  = ScoreHolder.BlueScore;
        _redScore   = ScoreHolder.RedScore;
    }

    // ── SyncVar callbacks (run on clients only) ────────────────────────────────

    private void OnMatchTimerChanged(float prev, float next, bool asServer)
    {
        if (asServer) return;
        FMS.MatchTimer = next;
    }

    private void OnMatchStateChanged(MatchState prev, MatchState next, bool asServer)
    {
        if (asServer) return;
        FMS.MatchState = next;
    }

    private void OnRobotStateChanged(RobotState prev, RobotState next, bool asServer)
    {
        if (asServer) return;
        FMS.RobotState = next;
    }

    private void OnBlueScoreChanged(int prev, int next, bool asServer)
    {
        if (asServer) return;
        ScoreHolder.BlueScore = next;
    }

    private void OnRedScoreChanged(int prev, int next, bool asServer)
    {
        if (asServer) return;
        ScoreHolder.RedScore = next;
    }

    // ── Server RPC: client requests match restart ──────────────────────────────
    [ServerRpc(RequireOwnership = false)]
    public void RequestRestart()
    {
        _fms?.Restart();
    }
}
