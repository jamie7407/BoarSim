using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;
using Util;

/// <summary>
/// Attached to the same GameObject as FMS.  The server/host runs the real FMS
/// state machine; this component mirrors its output to all clients via SyncVars.
/// </summary>
public class NetworkMatchSync : NetworkBehaviour
{
    // ── Synced match state (FishNet 4.x SyncVar<T> API) ───────────────────────
    private readonly SyncVar<float>      _matchTimer = new SyncVar<float>();
    private readonly SyncVar<MatchState> _matchState = new SyncVar<MatchState>();
    private readonly SyncVar<RobotState> _robotState = new SyncVar<RobotState>();
    private readonly SyncVar<int>        _blueScore  = new SyncVar<int>();
    private readonly SyncVar<int>        _redScore   = new SyncVar<int>();

    private FMS _fms;

    private void Awake()
    {
        _fms = GetComponent<FMS>();

        _matchTimer.OnChange += (prev, next, asServer) => { if (!asServer) FMS.MatchTimer         = next; };
        _matchState.OnChange += (prev, next, asServer) => { if (!asServer) FMS.MatchState         = next; };
        _robotState.OnChange += (prev, next, asServer) => { if (!asServer) FMS.RobotState         = next; };
        _blueScore.OnChange  += (prev, next, asServer) => { if (!asServer) ScoreHolder.BlueScore  = next; };
        _redScore.OnChange   += (prev, next, asServer) => { if (!asServer) ScoreHolder.RedScore   = next; };
    }

    // Server pushes current FMS state into the SyncVars every frame.
    // Clients receive the values automatically via the OnChange callbacks.
    private void Update()
    {
        if (!IsServerInitialized) return;

        _matchTimer.Value = FMS.MatchTimer;
        _matchState.Value = FMS.MatchState;
        _robotState.Value = FMS.RobotState;
        _blueScore.Value  = ScoreHolder.BlueScore;
        _redScore.Value   = ScoreHolder.RedScore;
    }

    // ── Server RPC: any client can request a match restart ────────────────────
    [ServerRpc(RequireOwnership = false)]
    public void RequestRestart()
    {
        _fms?.Restart();
    }
}
