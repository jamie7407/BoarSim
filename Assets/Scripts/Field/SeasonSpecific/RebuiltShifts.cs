using System;
using UnityEngine;
using Random = System.Random;

public class RebuiltShifts : ScoreOnlyOnce
{
    public static CurrentShift ActiveShift { get; private set; } = CurrentShift.Auto;
    public static bool BlueOwnsOddShifts { get; private set; } = true;
    public static float ShiftEndMatchTime { get; private set; }

    [SerializeField] private CurrentShift currentShift;

    [Header("End Disable Scoring")]
    [SerializeField] private float endDisableScoreTime = 3f;

    private bool blueWonAuto;
    private float shiftEndMatchTime;
    private float endDisableScoreTimer;
    private MatchState previousMatchState;
    
    [SerializeField] private GameObject shiftOnLight;

    private void Start()
    {
        blueWonAuto = false;
        shiftEndMatchTime = 0f;
        endDisableScoreTimer = 0f;
        currentShift = CurrentShift.Auto;
        previousMatchState = MatchState.auto;
        ActiveShift = currentShift;
        BlueOwnsOddShifts = true;
    }

    private new void FixedUpdate()
    {
        handleShiftState();

        bool shouldScore = isOnShift();

        // Keep scoring active during the match-end disabled period.
        if (FMS.MatchState == MatchState.finished && endDisableScoreTimer > 0f)
        {
            shouldScore = true;
            endDisableScoreTimer -= Time.fixedDeltaTime;
        }

        if (shiftOnLight != null)
        {
            shiftOnLight.SetActive(shouldScore);
        }

        poolOccupyObjects();

        compareObjects(shouldScore);

        ScorePoints(totalScore);
    }

    private void handleShiftState()
    {
        // Auto just ended.
        if (FMS.MatchState != MatchState.auto && previousMatchState == MatchState.auto)
        {
            if (ScoreHolder.BlueScore > ScoreHolder.RedScore)
            {
                blueWonAuto = true;
            }
            else if (ScoreHolder.BlueScore == ScoreHolder.RedScore)
            {
                var rng = new Random();
                blueWonAuto = rng.Next(0, 2) == 1;
            }

            BlueOwnsOddShifts = !blueWonAuto;

            currentShift = CurrentShift.Transition;
            shiftEndMatchTime = FMS.TeleopStartMatchTime - 10f;   // 140 - 10 = 130 (clean integer)
        }

        if (FMS.MatchState == MatchState.teleop)
        {
            if (FMS.MatchTimer <= shiftEndMatchTime && currentShift < CurrentShift.EndGame)
            {
                currentShift += 1;
                shiftEndMatchTime -= 25f;   // 130 -> 105 -> 80 -> 55 -> 30, all whole seconds
            }
        }

        // Endgame remains active through normal endgame.
        if (FMS.MatchState == MatchState.endgame)
        {
            currentShift = CurrentShift.EndGame;
        }

        // Finished is the final disabled period.
        // Keep the shift as EndGame instead of letting it advance past the enum.
        if (FMS.MatchState == MatchState.finished)
        {
            currentShift = CurrentShift.EndGame;

            if (previousMatchState != MatchState.finished)
            {
                endDisableScoreTimer = endDisableScoreTime;
            }
        }

        previousMatchState = FMS.MatchState;
        ActiveShift = currentShift;
        ShiftEndMatchTime = shiftEndMatchTime;
    }

    private bool isOnShift()
    {
        var isBlue = GetIsBlue();

        if (blueWonAuto)
        {
            if (isBlue)
            {
                return currentShift is CurrentShift.Auto
                    or CurrentShift.Transition
                    or CurrentShift.Shift2
                    or CurrentShift.Shift4
                    or CurrentShift.EndGame;
            }
            else
            {
                return currentShift is CurrentShift.Auto
                    or CurrentShift.Transition
                    or CurrentShift.Shift1
                    or CurrentShift.Shift3
                    or CurrentShift.EndGame;
            }
        }
        else
        {
            if (isBlue)
            {
                return currentShift is CurrentShift.Auto
                    or CurrentShift.Transition
                    or CurrentShift.Shift1
                    or CurrentShift.Shift3
                    or CurrentShift.EndGame;
            }
            else
            {
                return currentShift is CurrentShift.Auto
                    or CurrentShift.Transition
                    or CurrentShift.Shift2
                    or CurrentShift.Shift4
                    or CurrentShift.EndGame;
            }
        }
    }

    [Serializable]
    public enum CurrentShift
    {
        Auto,
        Transition,
        Shift1,
        Shift2,
        Shift3,
        Shift4,
        EndGame,
    }
}