using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Util;

public class ScoreOnlyOnce : FieldScorer
{
    private HashSet<GameObject> scoredPieces = new HashSet<GameObject>();
    private readonly HashSet<GameObject> _currentObjects = new HashSet<GameObject>();
    protected int totalScore = 0;

    protected void FixedUpdate()
    {
        poolOccupyObjects();
        
        // Create a set of current objects for comparison
        compareObjects();
        
        ScorePoints(totalScore); // Pass the total accumulated score
    }

    protected void poolOccupyObjects()
    {
        occupyObjects = occupyPieces();
    }
    
    

    protected void compareObjects(bool shouldScore = true)
    {
        _currentObjects.Clear();
        foreach (var piece in occupyObjects)
        {
            _currentObjects.Add(piece.gameObject);

            if (!scoredPieces.Contains(piece.gameObject))
            {
                scoredPieces.Add(piece.gameObject);
                if (shouldScore)
                {
                    totalScore++;
                    bool auto = FMS.MatchState == MatchState.auto;
                    FireOnPieceScored(piece.lastScoredBySlot, auto ? autoScoreToAdd : scoreToAdd);
                }
            }
        }

        // Remove pieces that left the zone — no lambda allocation
        scoredPieces.IntersectWith(_currentObjects);
    }
}