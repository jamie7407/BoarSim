using TMPro;
using UnityEngine;

// Displays whose shift is active and how much time is left in it.
//
// All countdowns are derived from FMS.MatchTimer so their second-ticks
// land on the SAME boundary as the main match clock (no "ticks in between").
//   Auto                  -> FMS.AutoTimeRemaining
//   Transition / Shift1-4 -> FloorToInt(MatchTimer) - FloorToInt(ShiftEndMatchTime)
//   EndGame               -> FloorToInt(MatchTimer)
//
// Setup: put this on a UI object, wire a label TMP_Text and a countdown TMP_Text.
public class ShiftTimerUI : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private TMP_Text label;       // "BLUE SHIFT" / "RED SHIFT" / "AUTO" / "TRANSITION" / "ENDGAME"
    [SerializeField] private TMP_Text countdown;    // "14"

    [Header("Colors")]
    [SerializeField] private Color blueColor = new Color(0f, 0.72f, 1f);
    [SerializeField] private Color redColor = new Color(1f, 0.19f, 0.19f);
    [SerializeField] private Color neutralColor = Color.white;

    [Header("Display Rules")]
    [SerializeField] private bool hideWhenFinished = true;

    private void Update()
    {
        if (hideWhenFinished && FMS.MatchState == MatchState.finished)
        {
            SetVisible(false);
            return;
        }

        SetVisible(true);

        var shift = RebuiltShifts.ActiveShift;

        string text;
        Color color;
        bool showCountdown = true;
        int displaySeconds = 0;

        // Match clock floored once, reused so every phase ticks on the same boundary.
        int matchFloor = Mathf.FloorToInt(Mathf.Max(0f, FMS.MatchTimer));

        switch (shift)
        {
            case RebuiltShifts.CurrentShift.Auto:
                text = "AUTO";
                color = neutralColor;
                displaySeconds = Mathf.FloorToInt(Mathf.Max(0f, FMS.AutoTimeRemaining));
                break;

            case RebuiltShifts.CurrentShift.Transition:
                text = "TRANSITION";
                color = neutralColor;
                displaySeconds = Mathf.Max(0, matchFloor - Mathf.FloorToInt(RebuiltShifts.ShiftEndMatchTime));
                break;

            case RebuiltShifts.CurrentShift.Shift1:
            case RebuiltShifts.CurrentShift.Shift3:
                {
                    bool blue = RebuiltShifts.BlueOwnsOddShifts;
                    text = blue ? "BLUE SHIFT" : "RED SHIFT";
                    color = blue ? blueColor : redColor;
                    displaySeconds = Mathf.Max(0, matchFloor - Mathf.FloorToInt(RebuiltShifts.ShiftEndMatchTime));
                    break;
                }

            case RebuiltShifts.CurrentShift.Shift2:
            case RebuiltShifts.CurrentShift.Shift4:
                {
                    bool blue = !RebuiltShifts.BlueOwnsOddShifts;
                    text = blue ? "BLUE SHIFT" : "RED SHIFT";
                    color = blue ? blueColor : redColor;
                    displaySeconds = Mathf.Max(0, matchFloor - Mathf.FloorToInt(RebuiltShifts.ShiftEndMatchTime));
                    break;
                }

            case RebuiltShifts.CurrentShift.EndGame:
                text = "ENDGAME";
                color = neutralColor;
                displaySeconds = matchFloor; // time left in the match
                break;

            default:
                text = "";
                color = neutralColor;
                showCountdown = false;
                break;
        }

        if (label != null)
        {
            label.text = text;
            label.color = color;
        }

        if (countdown != null)
        {
            if (showCountdown)
            {
                countdown.text = displaySeconds.ToString();
                countdown.color = color;
                countdown.enabled = true;
            }
            else
            {
                countdown.enabled = false;
            }
        }
    }

    private void SetVisible(bool visible)
    {
        if (label != null) label.enabled = visible;
        if (countdown != null) countdown.enabled = visible;
    }
}