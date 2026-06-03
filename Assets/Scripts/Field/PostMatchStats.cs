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

    // ── Per-robot score tracking [slot 0-3][phase: 0=auto 1=teleop 2=endgame] ──
    private static readonly int[,] _slotScores = new int[4, 3];

    public static int GetSlotScore(int slot, int phase) =>
        (slot >= 0 && slot < 4) ? _slotScores[slot, phase] : 0;

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
        System.Array.Clear(_slotScores, 0, _slotScores.Length);
    }

    private void OnEnable()
    {
        ResetForNewMatch();
        FieldScorer.OnPieceScored += OnPieceScoredEvent;
    }

    private void OnDisable() => FieldScorer.OnPieceScored -= OnPieceScoredEvent;

    private static void OnPieceScoredEvent(int slot, int points)
    {
        if (slot < 0 || slot >= 4) return;
        int phase = FMS.MatchState switch
        {
            MatchState.auto     => 0,
            MatchState.endgame  => 2,
            _                   => 1,
        };
        _slotScores[slot, phase] += points;
    }

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

        // Use per-robot tracked scores when available (pieces fired through ReleaseToWorld).
        // Fall back to equal alliance split for slots with no tracked data (e.g. non-piece scoring).
        for (int slot = 0; slot < 4; slot++)
        {
            bool isBlue = slot < 2;
            int  count  = isBlue ? blueCount : redCount;
            if (count == 0) continue;
            if (!isBlue && slot - 2 >= redCount) continue;

            int trackedTotal = _slotScores[slot, 0] + _slotScores[slot, 1] + _slotScores[slot, 2];
            int slotAuto, slotTeleop, slotEndgame;

            if (trackedTotal > 0)
            {
                // Actual per-robot data available
                slotAuto    = _slotScores[slot, 0];
                slotTeleop  = _slotScores[slot, 1];
                slotEndgame = _slotScores[slot, 2];
            }
            else
            {
                // No piece events fired for this slot — equal split of alliance total
                int a = isBlue ? BlueAuto    : RedAuto;
                int t = isBlue ? BlueTeleop  : RedTeleop;
                int e = isBlue ? BlueEndgame : RedEndgame;
                slotAuto    = a / count;
                slotTeleop  = t / count;
                slotEndgame = e / count;
            }

            UpdateSlotEPA(slot, slotAuto, slotTeleop, slotEndgame, 1);
        }

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
