using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(PlayerInput))]
public class PlayerMovement : MonoBehaviour
{
    [Tooltip("The point at the center of the capsule's lower hemisphere.")]
    public Transform footPoint;
    [Tooltip("The point at the center of the capsule's upper hemisphere.")]
    public Transform headPoint;

    public PlayerInput input { get; private set; }

    private Coroutine movementRoutine;
    private Vector2 moveInput;
    private bool isOnFeet;
    private bool isRolling;
    
    void Awake()
    {
        isOnFeet = true;
        isRolling = false;
    }

    public void Move(InputAction.CallbackContext context)
    {
        // Update moveInput
        moveInput = context.ReadValue<Vector2>();

        // If we're not currently moving, start moving
        if(!isRolling)
        {
            movementRoutine = StartCoroutine(MoveRoutine());
        }
    }

    private IEnumerator MoveRoutine()
    {
        // Figure out if we're on our feet or our head
        Transform pivotPoint = isOnFeet ? footPoint : headPoint;

        // Get the direction to roll
        Vector2 direction = CalculateMoveDirection();
        yield return null;
    }

    public Vector3 GetStablePosition()
    {
        Vector3 position = transform.position;
        position.y = Mathf.Min(footPoint.position.y, headPoint.position.y);
        return position;
    }

    private Vector2 CalculateMoveDirection()
    {
        // TODO calculate direction based on camera direction and movement input
        return moveInput;
    }
}
