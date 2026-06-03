using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Util;

namespace BuilderLib
{
    public static class GamePieceManager
    {
        public static void disableColliders(GamePiece piece)
        {
            if (piece.colliderParent.activeSelf)
            {
                piece.colliderParent.SetActive(false);
            }
        }

        public static IEnumerator enableColliders(GamePiece piece)
        {
            if (piece.colliderParent.activeSelf)
            {
                yield return null;
            }
            yield return new WaitForSeconds(0.05f);
        
            piece.colliderParent.SetActive(true);
        }
        public static bool AnimateTo(GamePiece piece, NodeAction action, Transform t = null)
        {
            var speed = action.Speed * 0.0254f;
        
            var transform = piece.rb.transform;
            var target = t ? t : action.MoveTo.transform;
            
            if (piece.state != GamePieceState.Moving)
            {
                piece.state = GamePieceState.Moving;
                piece.startPosition = transform.localPosition;
                disableColliders(piece);
            }
        
            var distance = transform.parent.InverseTransformPoint(target.position) - piece.startPosition;
            var parentPosition = transform.parent.position;
            
            // Calculate the step, but clamp it to not overshoot
            var distanceMagnitude = distance.magnitude;
            var maxStep = speed * Time.deltaTime;
            var stepMagnitude = Mathf.Min(maxStep, distanceMagnitude);
            var step = distance.normalized * stepMagnitude;
            
            var finalPosition = piece.startPosition + step;
        
            piece.startPosition = finalPosition;
            transform.position = parentPosition + transform.parent.TransformDirection(finalPosition);
            piece.rb.position = parentPosition + transform.parent.TransformDirection(finalPosition);
            piece.rb.velocity = Vector3.zero;
        
            // Calculate target rotation based on movement direction
            Quaternion targetRotation = target.rotation;
            Quaternion shortestTargetRotation;
            if (piece.pieceType == PieceNames.Coral)
            {
                shortestTargetRotation = FindShortestSymmetricRotation(
                    transform.rotation,
                    targetRotation
                );
            }
            else
            {
                shortestTargetRotation = targetRotation;
            }
        
            // Smoothly rotate towards target rotation
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                shortestTargetRotation,
                action.AngularSpeed * Time.deltaTime  // Changed from Time.fixedDeltaTime to Time.deltaTime
            );
        
            if (action.AngularSpeed == 0)
            {
                transform.localRotation = Quaternion.identity;
            }
        
            if (distanceMagnitude <= 0.75f * 0.0254f)
            {
                changeParent(piece, action, t);
                return true; // Reached target
            }
            else
            {
                return false; // Moving towards target
            }
        }

        public static bool teleportTo(GamePiece piece, NodeAction action)
        {
            var target = action.MoveTo.transform;
            teleportTo(piece, target, true);
            changeParent(piece, action);
            return true;
        }
    
        public static bool teleportTo(GamePiece piece, Transform target, bool alreadyChanged = false)
        {
            disableColliders(piece);
            var transform = piece.rb.transform;
            transform.position = target.position;
            transform.rotation = target.rotation;
            if (alreadyChanged) return true;
            changeParent(piece, target);
            return true;
        }
    
        // Cached to avoid FindFirstObjectByType on every release
        private static LoadMatch _loadMatchCache;

        private static int ResolveRobotSlot(Transform ownerTransform)
        {
            if (ownerTransform == null) return -1;
            // owner is a node inside the robot; walk up to find the robot root
            var swerve = ownerTransform.GetComponentInParent<SwerveController>();
            if (swerve == null) return -1;
            if (_loadMatchCache == null)
                _loadMatchCache = Object.FindFirstObjectByType<LoadMatch>();
            if (_loadMatchCache == null) return -1;
            for (int i = 0; i < 4; i++)
                if (_loadMatchCache.GetRobotLoaded(i) == swerve.gameObject) return i;
            return -1;
        }

        public static bool ReleaseToWorld(GamePiece piece, NodeAction action)
        {
            if (!piece) return false;
            if (!piece.owner) return false;
            if (piece.pieceType != action.PieceType) return false;
            var speed = action.overideSpeed * 0.0254f ?? action.Speed * 0.0254f;
            action.overideSpeed = null;
            var rb = piece.rb;
            var transform = rb.transform;

            // Capture which robot fired this piece before we clear owner
            piece.lastScoredBySlot = ResolveRobotSlot(piece.owner);

            rb.velocity = Vector3.zero;
            piece.transform.localPosition = Vector3.zero;
            piece.transform.localEulerAngles = Vector3.zero;
            Vector3 velocity;
            switch (action.Direction)
            {
                case Direction.forward:
                    velocity = piece.owner.transform.forward.normalized * speed;
                    break;
                case Direction.up:
                    velocity = piece.owner.transform.up.normalized * speed;
                    break;
                case Direction.sideways:
                    velocity = piece.owner.transform.right * speed;
                    break;
                default:
                    velocity = piece.owner.transform.forward.normalized * speed;
                    break;
            }
            rb.velocity = velocity;
            rb.angularVelocity = transform.TransformDirection(action.Spin);

            piece.state = GamePieceState.World;
            piece.transform.parent = piece.originalParent;
            piece.owner = null;

            return true;
        }


        public static bool changeParent(GamePiece piece, NodeAction action, Transform t = null)
        {
            var value = t ? t : action.MoveTo.transform;
            if (action.MoveTo)
            {
                action.MoveTo.currentGamePiece = piece;
                action.MoveTo.currentState = NodeState.Stowing;
            }
            changeParent(piece, value);
            return true;
        }
    
        public static bool changeParent(GamePiece piece, Transform target)
        {
            piece.owner = target.transform;
            piece.transform.parent = target.transform;
            piece.state = GamePieceState.Stationary;
            return true;
        }
    
        // Pre-allocated buffer — avoids new List<Quaternion>() every call.
        private static readonly Quaternion[] _symBuffer = new Quaternion[8];

        private static Quaternion FindShortestSymmetricRotation(Quaternion current, Quaternion target)
        {
            _symBuffer[0] = target;
            _symBuffer[1] = target * Quaternion.Euler(180f, 0f,   0f);
            _symBuffer[2] = target * Quaternion.Euler(0f,   180f, 0f);
            _symBuffer[3] = target * Quaternion.Euler(0f,   0f,   180f);
            _symBuffer[4] = target * Quaternion.Euler(180f, 180f, 0f);
            _symBuffer[5] = target * Quaternion.Euler(180f, 0f,   180f);
            _symBuffer[6] = target * Quaternion.Euler(0f,   180f, 180f);
            _symBuffer[7] = target * Quaternion.Euler(180f, 180f, 180f);

            Quaternion best  = target;
            float smallest   = float.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                float angle = Quaternion.Angle(current, _symBuffer[i]);
                if (angle < smallest) { smallest = angle; best = _symBuffer[i]; }
            }
            return best;
        }
    }
}



