using System;
using System.Collections;
using System.Collections.Generic;
using BuilderLib;
using UnityEngine;
using Util;

public class ScoreThenMove : FieldScorer
{
    [SerializeField] private FeildInteraction moveTo;

    private Queue<GamePiece> buffer;
    private readonly HashSet<GamePiece> bufferSet = new HashSet<GamePiece>();

    private int scoredCount;

    private GamePiece lastAnimatedGamePiece;

    private void Start()
    {
        buffer = new Queue<GamePiece>();
    }

    void FixedUpdate()
    {
        occupyObjects = occupyPieces();

        for (int i = occupyObjects.Count - 1; i >= 0; i--)
        {
            if (bufferSet.Contains(occupyObjects[i]))
            {
                occupyObjects.Remove(occupyObjects[i]);
            }
            else
            {
                buffer.Enqueue(occupyObjects[i]);
                bufferSet.Add(occupyObjects[i]);
            }
        }

        scoredCount += occupyObjects.Count;
        ScorePoints(scoredCount);

        if (!moveTo.GetGamePiece() && buffer.TryPeek(out GamePiece piece))
        {
            if (lastAnimatedGamePiece != piece)
            {
                // Reuse occupyObjects already populated above — avoids a second occupyPieces() call
                bool anyBuffered = false;
                for (int i = 0; i < occupyObjects.Count; i++)
                {
                    if (bufferSet.Contains(occupyObjects[i]))
                    {
                        anyBuffered = true;
                        break;
                    }
                }
                if (!anyBuffered)
                {
                    lastAnimatedGamePiece = piece;
                    return;
                }
            }

            bool finished = AnimateTo(piece, 60, 0);

            if (finished)
            {
                moveTo.SetGamePiece(piece);
                buffer.Dequeue();
                bufferSet.Remove(piece);
            }
        }
    }
    
    private bool AnimateTo(GamePiece piece, float Speed, float AngularSpeed)
        {
            var speed = Speed * 0.0254f;

            var transform = piece.rb.transform;
            var target = moveTo.gameObject.transform;
            if (piece.state != GamePieceState.Moving)
            {
                piece.state = GamePieceState.Moving;
                piece.startPosition = transform.localPosition;
                GamePieceManager.disableColliders(piece);
            }

            var distance = transform.parent.InverseTransformPoint(target.position) - piece.startPosition;
            var parentPosition = transform.parent.position;
            var step = distance.normalized * ((speed) * Time.deltaTime);
            var finalPosition = piece.startPosition + step;

            piece.startPosition = finalPosition;
            transform.position = parentPosition + transform.parent.TransformDirection(finalPosition);
            piece.rb.position = parentPosition + transform.parent.TransformDirection(finalPosition);
            piece.rb.velocity = Vector3.zero;

            var distanceMagnitude = distance.magnitude;
            
            Quaternion targetRotation = target.rotation;
          

            // Smoothly rotate towards target rotation
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                targetRotation,
                AngularSpeed * Time.fixedDeltaTime
            );

            if (AngularSpeed == 0)
            {
                transform.localRotation = Quaternion.identity;
            }

            if (distanceMagnitude <= 0.75f * 0.0254f)
            {
                return true; // Reached target
            }
            else
            {
                return false; // Moving towards target
            }
        }
    
    
}
