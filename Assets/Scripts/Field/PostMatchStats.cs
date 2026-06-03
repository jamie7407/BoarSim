using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Util;

/// <summary>
/// Tracks per-phase (auto / teleop / endgame) alliance scores during a match,
/// persists running EPA averages in PlayerPrefs, and exposes the data to the UI.
/// Auto-creates itself when an FMS is present in the scene.
/// </summary>
public class PostMatchStats : MonoBehaviour
{
    public static PostMatchStats Instance { get; private set; }

    // ── Phase snapshots (populated at each phase transition) ─────────────────
    public static int BlueScoreAtAutoEnd    { get; private set; }
    public static int RedScoreAtAutoEnd     { get; private set; }
    public static int BlueScoreAtEndgame    { get; private set; }
    public static int RedScoreAtEndgame     { get; private set; }

    // ── Derived per-phase totals ──────────────────────────────────────────────
    public static int BlueAuto    => BlueScoreAtAutoEnd;
    public static int RedAuto     => RedScoreAtAutoEnd;
    public static int BlueTeleop  => BlueScoreAtEndgame - BlueScoreAtAutoEnd;
    public static int RedTeleop   => RedScoreAtEndgame  - RedScoreAtAutoEnd;
    public static int BlueEndgame => ScoreHolder.BlueScore - BlueScoreAtEndgame;
    public static int RedEndgame  => ScoreHolder.RedScore  - RedScoreAtEndgame;
    public static int BlueTotal   => ScoreHolder.BlueScore;
    public static int RedTotal    => ScoreHolder.RedScore;

    // ── EPA slot IDs (indexed 0-3: B1, B2, R1, R2) ───────────────────────────
    public static readonly string[] SlotKeys  = { "B1", "B2", "R1", "R2" };
    public static readonly string[] SlotLabels = { "Blue P1", "Blue P2", "Red P1", "Red P2" };

    private MatchState _lastState;
    private bool _statsRecorded;

    // ── Auto-create ───────────────────────────────────────────────────────────
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    // [Preserve] prevents IL2CPP from stripping this method in a stripped build.
    [UnityEngine.Scripting.Preserve]
    private static void AutoCreate()
    {
        if (FindFirstObjectByType<FMS>() == null) return;
        if (FindFirstObjectByType<PostMatchStats>() != null) return;
        new GameObject("PostMatchStats").AddComponent<PostMatchStats>();
    }

    // Called by FMS.Restart() — guaranteed to run in all build configurations.
    public static void EnsureExists()
    {
        if (Instance != null) return;
        new GameObject("PostMatchStats").AddComponent<PostMatchStats>();
    }

    private void Awake()
    {
        Instance = this;
    }

    public void ResetForNewMatch()
    {
        BlueScoreAtAutoEnd = 0;
        RedScoreAtAutoEnd  = 0;
        BlueScoreAtEndgame = 0;
        RedScoreAtEndgame  = 0;
        _statsRecorded     = false;
        _lastState         = MatchState.auto;
    }

    private void OnEnable() => ResetForNewMatch();

    private void Update()
    {
        // D-pad up on any gamepad toggles the stats screen at any time
        foreach (var gp in Gamepad.all)
        {
            if (gp.dpad.up.wasPressedThisFrame)
            {
                if (PostMatchStatsUI.IsOpen) PostMatchStatsUI.Close();
                else                         PostMatchStatsUI.Show();
                break;
            }
        }

        var state = FMS.MatchState;
        if (state == _lastState) return;

        // auto → teleop: snapshot auto-phase score
        if (_lastState == MatchState.auto &&
            (state == MatchState.teleop || state == MatchState.endgame))
        {
            BlueScoreAtAutoEnd = ScoreHolder.BlueScore;
            RedScoreAtAutoEnd  = ScoreHolder.RedScore;
        }

        // teleop → endgame: snapshot teleop-phase score
        if (_lastState == MatchState.teleop && state == MatchState.endgame)
        {
            BlueScoreAtEndgame = ScoreHolder.BlueScore;
            RedScoreAtEndgame  = ScoreHolder.RedScore;
        }

        // match finished: record EPA, trigger UI
        if (state == MatchState.finished && !_statsRecorded)
        {
            _statsRecorded = true;

            // Guard: if endgame never triggered (very short test match), pin snapshot
            if (BlueScoreAtEndgame == 0 && BlueTotal > 0) BlueScoreAtEndgame = BlueTotal;
            if (RedScoreAtEndgame  == 0 && RedTotal  > 0) RedScoreAtEndgame  = RedTotal;

            RecordEPA();
            PostMatchStatsUI.Show();
        }

        _lastState = state;
    }

    // ── EPA persistence ───────────────────────────────────────────────────────

    private void RecordEPA()
    {
        var loadMatch = FindFirstObjectByType<LoadMatch>();
        if (loadMatch == null) return;

        var settings  = loadMatch.GetSettingsCopy();
        int blueCount = BlueRobotCount(settings.playMode);
        int redCount  = RedRobotCount(settings.playMode);

        // Slot 0 = Blue P1, Slot 1 = Blue P2, Slot 2 = Red P1, Slot 3 = Red P2
        UpdateSlotEPA(0, BlueAuto, BlueTeleop, BlueEndgame, blueCount);
        if (blueCount >= 2) UpdateSlotEPA(1, BlueAuto, BlueTeleop, BlueEndgame, blueCount);
        if (redCount  >= 1) UpdateSlotEPA(2, RedAuto,  RedTeleop,  RedEndgame,  redCount);
        if (redCount  >= 2) UpdateSlotEPA(3, RedAuto,  RedTeleop,  RedEndgame,  redCount);

        PlayerPrefs.Save();
    }

    private static void UpdateSlotEPA(int slot, int allianceAuto, int allianceTeleop,
                                       int allianceEndgame, int count)
    {
        if (count <= 0) return;
        string k = SlotKeys[slot];

        float newA = (float)allianceAuto    / count;
        float newT = (float)allianceTeleop  / count;
        float newE = (float)allianceEndgame / count;

        int   n  = PlayerPrefs.GetInt  ($"EPA_{k}_N", 0);
        float pA = PlayerPrefs.GetFloat($"EPA_{k}_Auto",    0f);
        float pT = PlayerPrefs.GetFloat($"EPA_{k}_Teleop",  0f);
        float pE = PlayerPrefs.GetFloat($"EPA_{k}_Endgame", 0f);

        int newN = n + 1;
        PlayerPrefs.SetFloat($"EPA_{k}_Auto",    (pA * n + newA) / newN);
        PlayerPrefs.SetFloat($"EPA_{k}_Teleop",  (pT * n + newT) / newN);
        PlayerPrefs.SetFloat($"EPA_{k}_Endgame", (pE * n + newE) / newN);
        PlayerPrefs.SetInt  ($"EPA_{k}_N",       newN);
    }

    // ── EPA reads ─────────────────────────────────────────────────────────────

    public static (float auto, float teleop, float endgame, float total, int matches)
        GetSlotEPA(int slot)
    {
        string k = SlotKeys[slot];
        float a = PlayerPrefs.GetFloat($"EPA_{k}_Auto",    0f);
        float t = PlayerPrefs.GetFloat($"EPA_{k}_Teleop",  0f);
        float e = PlayerPrefs.GetFloat($"EPA_{k}_Endgame", 0f);
        int   n = PlayerPrefs.GetInt  ($"EPA_{k}_N",       0);
        return (a, t, e, a + t + e, n);
    }

    public static void ClearAllEPA()
    {
        foreach (var k in SlotKeys)
        {
            PlayerPrefs.DeleteKey($"EPA_{k}_Auto");
            PlayerPrefs.DeleteKey($"EPA_{k}_Teleop");
            PlayerPrefs.DeleteKey($"EPA_{k}_Endgame");
            PlayerPrefs.DeleteKey($"EPA_{k}_N");
        }
        PlayerPrefs.Save();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public static int BlueRobotCount(Util.PlayMode mode) => mode switch
    {
        Util.PlayMode.TwoVsZero => 2,
        Util.PlayMode.TwoVsTwo  => 2,
        _                  => 1,
    };

    public static int RedRobotCount(Util.PlayMode mode) => mode switch
    {
        Util.PlayMode.OneVsOne => 1,
        Util.PlayMode.TwoVsTwo => 2,
        _                 => 0,
    };
}
