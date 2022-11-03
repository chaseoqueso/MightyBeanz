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
    private const int maxPhysicsAttempts = 10;

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
    [Tooltip("The maximum height we can climb when colliding at the tip of the capsule.")]
    public float stepLimit = 0.5f;
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
    public TransformAndTime lastTransform { get; private set; }         // The player's transform during the last physics update
    public TransformAndTime nextTransform { get; private set; }         // The player's transform calculated for the next physics update
    public TransformAndTime startRollTransform { get; private set; }    // The player's transform at the start of the current roll
    public TransformAndTime predictionTransform { get; private set; }   // Where we expect the player to be at the end of this roll
    
    private new CapsuleCollider collider;

    // Movement properties
    private Vector3 rollDirection;          // The world space direction to roll towards
    private Transform pivotPoint;           // The current point we are pivoting around
    private Transform otherPivot;           // The pivot point we are not currently pivoting around
    private Transform tempPivot;            // A reference to a temporary pivot point for rolling over obstacles
    private float currentMoveSpeed;         // The current speed in rolls/second
    private float currentAngularAccel;      // The current angular acceleration in degrees/sec^2
    private float currentAngularVelocity;   // The current angular speed in degrees/second
    
    private Vector2 moveInput;
    private PlayerState currentState;
    private bool isOnFeet;
    private bool isAnimPause;
    private bool animRising;

    /// <summary> Gets the position of the player accounting for weird movement stuff </summary>
    public Vector3 GetStablePosition()
    {
        if(currentState == PlayerState.Rolling || currentState == PlayerState.Slowing)
        {
            // Get the interpolated position between the start roll transform and the prediction transform
            return Vector3.Lerp(startRollTransform.position, predictionTransform.position, Mathf.InverseLerp(startRollTransform.time, predictionTransform.time, Time.time));
        }
        else
        {
            // Get the interpolated position between the last physics update and the next one
            return Vector3.Lerp(lastTransform.position, nextTransform.position, Mathf.InverseLerp(lastTransform.time, nextTransform.time, Time.time));
        }
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
        startRollTransform = new TransformAndTime(Time.fixedTime, transform);
        predictionTransform = new TransformAndTime(Time.fixedTime, transform);

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
                StartRoll();
            }

            TransformAndTime mostRecentTransform = lastTransform;

            // Progress through the frame, potentially calculating multiple movements if multiple collisions occur
            float timeRemainingThisFrame = Time.fixedDeltaTime;
            int attempts = 0;
            while(timeRemainingThisFrame > 0 && attempts < maxPhysicsAttempts)
            {
                attempts++;

                // Get the local up vector for use throughout the method
                Vector3 localUp = otherPivot.position - pivotPoint.position;
                if(localUp == Vector3.up)
                {
                    animRising = false;
                }

                // The amount to rotate in the roll direction based on current speed
                Quaternion destRotation = animRising ? Quaternion.FromToRotation(localUp, Vector3.up) : Quaternion.FromToRotation(Vector3.up, rollDirection);
                Quaternion stepRotation = Quaternion.RotateTowards(Quaternion.identity, destRotation, currentAngularVelocity * timeRemainingThisFrame);

                // If there's a tempPivot, use that. Otherwise, use the normal pivot point
                Transform pivotToUse = tempPivot;
                if(pivotToUse == null)
                    pivotToUse = pivotPoint;

                // Rotate the bean and correct the position with pivot offset
                Vector3 lastPosition = pivotToUse.position;
                transform.rotation = stepRotation * transform.rotation;
                transform.position += lastPosition - pivotToUse.position;
                // TODO account for forward roll using arc length
                // TODO anim pausing

                // Store the calculated position and clear remaining time
                nextTransform = new TransformAndTime(Time.fixedTime + Time.fixedDeltaTime, transform);
                timeRemainingThisFrame = 0;
                
                // If the player cannot move into the specified position
                CollisionHit collision = CalculateCollision(mostRecentTransform, nextTransform);
                if(collision.collider != null)
                {
                    timeRemainingThisFrame = collision.transform.time - Time.fixedTime;
                    // Debug.Log("Collision");
                    // Debug.Log(timeRemainingThisFrame);
                    
                    // Start by setting our position and rotation to the calculated position and rotation at the time of the collision
                    transform.position = collision.transform.position;
                    transform.rotation = collision.transform.rotation;

                    // If the point we hit is:
                    // 1. At the other pivot, AND below the pivot, AND within slope limit, THEN switch the pivots
                    // 2. NOT at the other pivot, AND within the step limit, THEN create a new pivot and keep rolling
                    // 3. NOT at the other pivot, AND NOT within the step limit, AND below the halfway point, THEN create a new pivot and keep rolling
                    // 4. None of the above, THEN come to a stop

                    Vector3 localRightVector = Vector3.Cross(localUp, stepRotation * localUp).normalized;
                    Vector3 pivotForwardVector = Vector3.Cross(localRightVector, localUp).normalized;

                    RaycastHit pivotHit;
                    if(Physics.SphereCast(otherPivot.position, 
                                        collider.radius * 0.99f, 
                                        pivotForwardVector,
                                        out pivotHit,
                                        groundCheckDistance,
                                        groundLayers))                            // Check if hit point is in range of opposite pivot 
                    {
                        Vector3 pivotToHit = pivotHit.point - otherPivot.position;  // Calculate the vector from the opposite pivot toward the collider we hit
                        
                        // Debug.Log("Pivot");
                        // Debug.Log(Vector3.Angle(pivotToHit, Vector3.down));
                        // Debug.Log(Vector3.Angle(rollDirection, localUp));

                        // Check case 1
                        if(Vector3.Angle(pivotToHit, Vector3.down) < slopeLimit &&  // If hit point is below the bean
                           Vector3.Angle(rollDirection, localUp) <= slopeLimit)     // AND the bean is within the slope limit
                        {
                            // Debug.Log("Flip");
                            StartRoll();
                        }
                    }
                    else if(Physics.CapsuleCast(pivotPoint.position,
                                                otherPivot.position, 
                                                collider.radius * 0.99f, 
                                                pivotForwardVector,
                                                out pivotHit,
                                                groundCheckDistance,
                                                groundLayers))
                    {
                        Vector3 centerToCollision = pivotHit.point - transform.position;
                        Vector3 pivotToCollision = pivotHit.point - pivotPoint.position;

                        // Debug.Log("TempPivot");
                        // Debug.Log(Vector3.Distance(pivotHit.point, pivotPoint.position));
                        // Debug.Log(pivotHit.point.y - pivotPoint.position.y);
                        // Debug.Log(Vector3.Dot(centerToCollision, localUp));

                        // Check case 2 & 3
                        if(Vector3.Dot(pivotToCollision, localUp) > 0 &&                // If the point we hit is NOT near our current pivot
                           (pivotHit.point.y - pivotPoint.position.y <= stepLimit ||    // and the point we hit is either a small enough step
                           Vector3.Dot(centerToCollision, localUp) <= 0))               // or at the lower half of the player
                        {
                            // If there is a temporary pivot point, clean it up
                            if(tempPivot != null)
                            {
                                Destroy(tempPivot.gameObject);
                                tempPivot = null;
                            }

                            // Create a pivot at the collision point as a child of the player
                            tempPivot = new GameObject("TempPivot").transform;
                            tempPivot.position = pivotHit.point;
                            tempPivot.parent = transform;

                            // Recalculate the camera prediction
                            CalculateRollPrediction();

                            // Debug.Log("NewPivot");
                        }
                    }
                    else
                    {
                        // TODO wall collision
                    }

                    mostRecentTransform = collision.transform;
                }
        
                // Check for ground and if any is found, lock our bean at hover height
                RaycastHit? groundHit = GroundCheck();
                if(groundHit.HasValue)
                {
                    // Debug.Log("Grounded");
                    float groundDistance = groundHit.Value.distance - collider.radius;
                    transform.position += Vector3.up * (hoverHeight - groundDistance);  // Hover the player slightly above the ground
                }
                else
                {
                    // TODO deal with sliding off and falling
                }
            }

            if(attempts == maxPhysicsAttempts)
                Debug.LogWarning("Maxed out number of physics attempts");

            currentAngularVelocity += currentAngularAccel * Time.fixedDeltaTime;  // Accelerate the angular velocity
        }
            
        nextTransform = new TransformAndTime(Time.fixedTime + Time.fixedDeltaTime, transform);
    }

    private void StartRoll()
    {
        // If we aren't currently rolling
        if(rollDirection == Vector3.zero)
        {
            rollDirection = CalculateMoveDirection();   // Get the direction to roll
            animRising = false;                         // Start with the downward animation since we're upright already
            currentMoveSpeed = startMoveSpeed;          // Set our speed to the starting speed
        }
        else
        {
            // Get the direction to roll according to the maximum change in angle
            rollDirection = Vector3.RotateTowards(rollDirection, CalculateMoveDirection(), maxAngleDelta * Mathf.Deg2Rad / currentMoveSpeed, 0);
            animRising = true;      // Start with the downward animation since we're upright already
            isOnFeet = !isOnFeet;   // Switch our orientation

            // Accelerate if we're below max speed
            if(currentMoveSpeed < moveSpeed)
            {
                currentMoveSpeed += moveAccel;
            }
        }

        // Limit speed to max speed
        if(currentMoveSpeed > moveSpeed)
            currentMoveSpeed = moveSpeed;

        // Figure out if we're on our feet or our head
        pivotPoint = isOnFeet ? footPoint : headPoint;  
        otherPivot = isOnFeet ? headPoint : footPoint;

        // The angular acceleration determines the cadence of the animation, and will be constant for the duration of the roll
        // It is calculated using calc and physics and stuff
        currentAngularAccel = 360 / Mathf.Pow((1 - rollAnimPausePercent) / currentMoveSpeed, 2);
        
        currentAngularVelocity = 0;     // The bean has just begun rolling

        CalculateRollPrediction();      // Calculate the prediction of where the player will be at the end of this roll

        // If there is a temporary pivot point, clean it up
        if(tempPivot != null)
        {
            Destroy(tempPivot.gameObject);
            tempPivot = null;
        }
    }

    private RaycastHit? GroundCheck()
    {
        bool hasTempPivot = tempPivot != null;

        // The optimal RaycastHit to return
        RaycastHit? closestValid = null;

        // Calculate the origin based on the current pivot
        Vector3 raycastOrigin;
        if(hasTempPivot)
        {
            raycastOrigin = tempPivot.position + Vector3.up * collider.radius;
        }
        else
        {
            raycastOrigin = pivotPoint.position + Vector3.up * collider.radius;
        }

        // Check all colliders for anything valid
        RaycastHit[] hits;
        hits = Physics.SphereCastAll(raycastOrigin, 
                                     collider.radius,
                                     Vector3.down,
                                     (collider.radius * 2) + groundCheckDistance, 
                                     groundLayers);

        // If anything was hit, find the raycast info of the point we hit
        if(hits.Length > 0)
        {
            foreach(RaycastHit hit in hits)
            {
                // Get the vector from the pivot point to the collision point
                Vector3 pivotToHit;
                if(hasTempPivot)
                    pivotToHit = hit.point - tempPivot.position;
                else
                    pivotToHit = hit.point - pivotPoint.position;

                // If the thing we hit was within the slope limit and distance
                if(Vector3.Angle(pivotToHit, Vector3.down) < slopeLimit &&
                   pivotToHit.magnitude < collider.radius + groundCheckDistance)
                {
                    // If closestValid hasn't been assigned yet or specificHit is closer to the pivot point, assign closestValid to specificHit
                    if(!closestValid.HasValue || hit.distance < closestValid.Value.distance)
                        closestValid = hit;
                }
            }
        }

        return closestValid;
    }

    private void CalculateRollPrediction()
    {
        // Predict the end of the roll using the ground angle and physics
        RaycastHit? groundHit = GroundCheck();
        if(groundHit.HasValue)
        {
            Vector3 groundNormal = groundHit.Value.normal;
            Vector3 localUp = otherPivot.position - pivotPoint.position;

            // The degrees traveled will change based on the animations we do during the roll
            float degreesTraveled = 0;
            if(animRising)
            {
                degreesTraveled += Vector3.Angle(localUp, Vector3.up);
            }
            Vector3 projectedRollDirection = Vector3.ProjectOnPlane(rollDirection, groundNormal);
            degreesTraveled += Vector3.Angle(Vector3.up, projectedRollDirection);

            // Kinematic equation 3 solved for t with v0 = 0
            float timeToTravel = Mathf.Sqrt(2 * degreesTraveled / currentAngularAccel);

            // Store current transform values so we can calculate predictions
            Vector3 currentPosition = transform.position;
            Quaternion currentRotation = transform.rotation;

            // Perform the theoretical movement that will occur
            Vector3 pivotPosition = pivotPoint.position;
            transform.rotation = Quaternion.FromToRotation(localUp, Vector3.up) * transform.rotation;
            transform.rotation = Quaternion.FromToRotation(Vector3.up, projectedRollDirection) * transform.rotation;
            transform.position += pivotPosition - pivotPoint.position;

            // Store the new transform as the prediction transform
            startRollTransform = predictionTransform;
            predictionTransform = new TransformAndTime(Time.fixedTime + timeToTravel + Time.fixedDeltaTime, transform);

            // Reset transform
            transform.position = currentPosition;
            transform.rotation = currentRotation;
        }
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
            testRotation = Quaternion.Slerp(initial.rotation, final.rotation, testContactTime);
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
}
