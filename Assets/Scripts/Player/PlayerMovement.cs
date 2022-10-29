using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public enum PlayerState
{
    Idle,       // No input, speed 0
    Rolling,    // Move input pressed, speeding up or at max speed
    Slowing,    // No input, speed above 0, slowing down
    Midair     // Either jump was just pressed or player is in midair
}

[RequireComponent(typeof(PlayerInput))]
public class PlayerMovement : MonoBehaviour
{
    [Header("General")]
    [Tooltip("The point at the center of the capsule's lower hemisphere.")]
    public Transform footPoint;
    [Tooltip("The point at the center of the capsule's upper hemisphere.")]
    public Transform headPoint;
    [Tooltip("The transform of the player's model object.")]
    public Transform playerModel;
    [Tooltip("The layers that the player should collide with.")]
    public LayerMask collisionLayers;

    [Header("Rolling")]
    [Tooltip("The maximum speed of the player in rolls/second.")]
    public float moveSpeed = 5f;
    [Tooltip("The amount to increase speed per roll.")]
    public float moveAccel = 1f;
    [Tooltip("The speed of the player when they first start rolling.")]
    public float startMoveSpeed = 1f;
    [Tooltip("The maximum angle in degrees we can climb.")]
    public float slopeLimit = 45f;
    [Tooltip("The maximum change in movement direction in degrees per second (delta per roll calculated using speed).")]
    public float maxAngleDelta = 45f;
    [Tooltip("The percent of one roll that should be allocated to the pause between roll animations.")]
    public float rollAnimPausePercent = 0.3f;

    [Header("Vertical Movement")]
    [Tooltip("The distance below the player to check for ground.")]
    public float groundCheckDistance = 0.05f;
    [Tooltip("The layers that the player can stand on.")]
    public LayerMask groundLayers;
    [Tooltip("The distance the player hovers above the ground. Necessary for ground collision to function.")]
    public float hoverHeight = 0.01f;
    [Tooltip("The acceleration due to gravity in meters/sec^2.")]
    public float gravityAccel = 20f;
    [Tooltip("The jump height when the player is at maximum speed.")]
    public float maxJumpHeight = 5f;
    [Tooltip("The jump height when the player is not moving.")]
    public float minJumpHeight = 2f;
    [Tooltip("The amount to multiply the jump height when the player achieves a perfect jump.")]
    public float perfectJumpMultiplier = 1.5f;

    public PlayerInput input { get; private set; }

    // We calculate and store our transform on fixedUpdate, and visually update it on Update
    public TransformAndTime lastTransform { get; private set; }
    public TransformAndTime nextTransform { get; private set; }
    
    private new CapsuleCollider collider;

    // Movement properties
    private Vector3 rollDirection;          // The world space direction to roll towards
    private Transform pivotPoint;           // Whether to pivot around the feet point or the head point
    private float currentMoveSpeed;         // The current speed in rolls/second
    private float currentAngularAccel;      // The current angular acceleration in degrees/sec^2
    private float currentAngularVelocity;   // The current angular speed in degrees/second
    
    private Vector2 moveInput;
    private PlayerState currentState;
    private bool isOnFeet;
    private bool isAnimPause;

    /// <summary> Gets the position of the player accounting for weird movement stuff </summary>
    public Vector3 GetStablePosition()
    {
        // Get the interpolated position between the last physics update and the next one
        Vector3 position = Vector3.Lerp(lastTransform.position, nextTransform.position, Mathf.InverseLerp(lastTransform.time, nextTransform.time, Time.time));

        // Set the y position to the lower of the two positions of the head and foot point for stability
        position.y = Mathf.Min(footPoint.position.y, headPoint.position.y); 
        return position;
    }
    
    void Awake()
    {
        isOnFeet = true;
        pivotPoint = footPoint;
        currentState = PlayerState.Idle;
        currentMoveSpeed = 0;
        rollDirection = Vector3.zero;

        lastTransform = new TransformAndTime(Time.fixedTime, transform);
        nextTransform = new TransformAndTime(Time.fixedTime + Time.fixedDeltaTime, transform);

        input = GetComponent<PlayerInput>();
        collider = GetComponent<CapsuleCollider>();
    }

    void Update()
    {
        // Update model transform
        float frameProgress = Mathf.InverseLerp(lastTransform.time, nextTransform.time, Time.time);
        playerModel.position = Vector3.Lerp(lastTransform.position, nextTransform.position, frameProgress);
        playerModel.rotation = Quaternion.Lerp(lastTransform.rotation, nextTransform.rotation, frameProgress);
    }

    void FixedUpdate()
    {
        // We store our current transform as the transform for last frame, and calculate a new transform for this frame
        lastTransform = nextTransform;

        // Perform controlled movement
        if(currentState == PlayerState.Rolling || currentState == PlayerState.Slowing)
        {
            // If we just started rolling
            if(rollDirection == Vector3.zero)
            {
                rollDirection = CalculateMoveDirection();           // Get the direction to roll
                StartRoll();
            }

            // TODO anim pausing

            // The amount to rotate in the roll direction based on current speed
            Quaternion stepRotation = Quaternion.RotateTowards(Quaternion.identity, Quaternion.FromToRotation(Vector3.up, rollDirection), currentAngularVelocity * Time.fixedDeltaTime);

            currentAngularVelocity += currentAngularAccel * Time.fixedDeltaTime;  // Accelerate the angular velocity

            // Rotate the bean and correct the position with pivot offset
            Vector3 lastPosition = pivotPoint.position;
            transform.rotation = stepRotation * transform.rotation;
            transform.position += lastPosition - pivotPoint.position;
            // TODO account for forward roll using arc length

            nextTransform = new TransformAndTime(Time.fixedTime, transform);
            
            // If the player cannot move into the specified position
            CollisionHit collision = CalculateCollision(lastTransform, nextTransform);
            if(collision.collider != null)
            {
                Debug.Log("Collision");
                // Start by setting our position and rotation to the calculated position and rotation at the time of the collision
                transform.position = collision.transform.position;
                transform.rotation = collision.transform.rotation;

                // Calculate the vector from the opposite pivot to the point of contact

                // If the point we hit is:
                // 1. At the other pivot, AND below the pivot, AND within slope limit, THEN switch the pivots
                // 2. NOT at the other pivot, AND within the step limit, THEN create a new pivot and keep rolling
                // 3. NOT at the other pivot, AND NOT within the step limit, AND below the halfway point, THEN create a new pivot and keep rolling
                // 4. None of the above, THEN come to a stop

                Transform otherPivot = isOnFeet ? headPoint : footPoint;        // Get the pivot opposite the one we're using

                Vector3 localRightVector = Vector3.Cross(lastTransform.rotation * Vector3.up, nextTransform.rotation * Vector3.up).normalized;
                Vector3 pivotForwardVector = Vector3.Cross(localRightVector, otherPivot.position - pivotPoint.position).normalized;

                RaycastHit pivotHit;
                if(Physics.SphereCast(otherPivot.position, 
                                      collider.radius * 0.99f, 
                                      pivotForwardVector,
                                      out pivotHit,
                                      0.05f,
                                      collisionLayers))                         // Check if hit point is in range of opposite pivot 
                {
                    Vector3 pivotToHit = pivotHit.point - otherPivot.position;  // Calculate the vector from the opposite pivot toward the collider we hit
                
                    Debug.Log("Pivot");
                    Debug.Log(Vector3.Dot(pivotToHit, Vector3.Cross(rollDirection, Vector3.up)));
                    Debug.Log(Vector3.Angle(rollDirection, transform.up * (isOnFeet ? 1 : -1)));

                    // Check case 1
                    if(Vector3.Dot(pivotToHit, Vector3.Cross(rollDirection, Vector3.up)) < 0.01f &&     // If hit point is coplanar with the bean's forward motion & world up vector
                       Vector3.Angle(rollDirection, transform.up * (isOnFeet ? 1 : -1)) <= slopeLimit)  // AND the bean is within the slope limit
                    {
                        Debug.Log("Flip");

                        isOnFeet = !isOnFeet;

                        rollDirection = Vector3.RotateTowards(rollDirection, CalculateMoveDirection(), maxAngleDelta * Mathf.Deg2Rad / currentMoveSpeed, 0);

                        StartRoll();
                    }
                }
                else // Check case 2 & 3
                {

                }


                // TODO Perform post-collision partial frame movement
            }
        }

        // Ground check
        if(Physics.CheckSphere(pivotPoint.position - (Vector3.up * groundCheckDistance), 
                               collider.radius, 
                               groundLayers))
        {
            // Check if the player is steady on the ground or is only partially on the ground
            RaycastHit hit;
            if(Physics.Raycast(pivotPoint.position,
                               Vector3.down,
                               out hit,
                               collider.radius + groundCheckDistance,
                               groundLayers))
            {
                float groundDistance = hit.distance - collider.radius;              // Subtract the portion of the ray that is inside the capsule
                transform.position += Vector3.up * (hoverHeight - groundDistance);  // Hover the player slightly above the ground
            }
            else
            {
                // TODO deal with sliding
            }
        }
            
        nextTransform = new TransformAndTime(Time.fixedTime, transform);
    }

    /// <summary> Calculates the time of impact of this object moving between two positions. </summary>
    /// <returns> A CollisionHit containing the collision information. </returns>
    private CollisionHit CalculateCollision(TransformAndTime initial, TransformAndTime final, uint iterations = 50)
    {
        // Check the total volume the player capsule could intersect with
        if(!Physics.CheckCapsule(initial.position, final.position, (collider.height + collider.radius * 2)/2, collisionLayers))
            return new CollisionHit(null, new TransformAndTime(-1));

        // Midpoint trial and error to find the closest time to the actual collision
        Vector3 capsulePointOffset = Vector3.up * collider.height/2;    // The distance from the center of the capsule to either of its two points (as used by Unity's physics methods)
        Collider hitCollider = null;                                    // The collider that was hit, if any
        Vector3 testCenter = Vector3.zero;                              // The position to test. This will be the closest position before the collision occurs
        Quaternion testRotation = Quaternion.identity;                  // The rotation to test. This will be the closest rotation before the collision occurs
        float contactTime = 0f;                                         // The time (from 0 to 1) at which the collision occurs
        for(uint i = 0; i < iterations; i++)
        {
            float testContactTime = contactTime + Mathf.Pow(0.5f, 2 * i);

            testCenter = Vector3.Lerp(initial.position, final.position, testContactTime);
            testRotation = Quaternion.Lerp(initial.rotation, final.rotation, testContactTime);
            Collider[] hits = Physics.OverlapCapsule(testCenter + testRotation * capsulePointOffset,
                                                     testCenter + testRotation * -capsulePointOffset,
                                                     collider.radius,
                                                     collisionLayers);

            if(hits.Length > 0)
            {
                hitCollider = hits[0];
            }
            else
            {
                contactTime = testContactTime;
            }
        }

        // If we didn't actually hit anything, return that info
        if(hitCollider == null)
            return new CollisionHit(null, new TransformAndTime(-1));

        Vector3 hitPosition = Vector3.Lerp(initial.position, final.position, contactTime);
        Quaternion hitRotation = Quaternion.Lerp(initial.rotation, final.rotation, contactTime);

        // Try to gain a little more info by CapsuleCasting from the calculated position of impact to the final position
        Vector3? hitPoint = null;
        RaycastHit hit;
        Vector3 castVector = final.position - hitPosition;
        Physics.CapsuleCast(hitPosition + hitRotation * capsulePointOffset,
                            hitPosition + hitRotation * -capsulePointOffset,
                            collider.radius,
                            castVector,
                            out hit,
                            castVector.magnitude,
                            collisionLayers);
        
        hitPoint = hit.point;
        
        return new CollisionHit(hitCollider, new TransformAndTime(Mathf.Lerp(initial.time, final.time, contactTime), hitPosition, hitRotation), hitPoint);
    }

    public void Move(InputAction.CallbackContext context)
    {
        // Update moveInput
        moveInput = context.ReadValue<Vector2>();

        if(moveInput == Vector2.zero) // If no input is detected
        {
            if(currentState != PlayerState.Idle) // If we're not idle, set to idle
            {
                currentState = PlayerState.Idle;
            }
        }
        else // If input is detected
        {
            if(currentState == PlayerState.Idle) // If we're idle, start moving
            {
                currentState = PlayerState.Rolling;
            }
        }
    }

    /// <summary> Calculates a new movement vector based on movement input and camera direction </summary>
    /// <returns> A normalized Vector3 representing the movement input relative to the camera in world space </returns>
    private Vector3 CalculateMoveDirection()
    {
        // Convert Vector2 movement input into 3D world space
        Vector3 worldMove = new Vector3(moveInput.x, 0, moveInput.y);
        // Rotate based on camera rotation
        return Quaternion.AngleAxis(Camera.main.transform.eulerAngles.y, Vector3.up) * worldMove.normalized;
    }

    private void StartRoll()
    {
        // If we just started moving, use starting speed. Otherwise, accelerate
        if(currentMoveSpeed < moveAccel)
        {
            currentMoveSpeed = startMoveSpeed;
        }
        else if(currentMoveSpeed < moveSpeed)
        {
            currentMoveSpeed += moveAccel;
        }

        // Limit speed to max speed
        if(currentMoveSpeed > moveSpeed)
            currentMoveSpeed = moveSpeed;

        pivotPoint = isOnFeet ? footPoint : headPoint;  // Figure out if we're on our feet or our head

        // The angular acceleration determines the cadence of the animation, and will be constant for the duration of the roll
        // It is calculated using calc and physics and stuff
        currentAngularAccel = 360 / Mathf.Pow((1 - rollAnimPausePercent) / currentMoveSpeed, 2);
        
        currentAngularVelocity = currentAngularAccel;     // The bean has just begun rolling
    }
}
