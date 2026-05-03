using System.Collections.Generic;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class AntAgent : Agent
{
    [Header("References")]
    public AntArenaController arena;
    public Transform foodTransform;
    private GameObject rayPerceptionSensorLocation;
    private SpriteRenderer spriteRenderer;

    [Header("Movement")]
    public float moveForce = 8f;
    public float maxSpeed = 2f;
    private int contactCount = 0;
    private bool contact = false;
    private bool isGrounded = false;
    private bool touchingAnt = false;
    private bool isClimbing = false;
    private bool facingRight = true;
    private bool canStartClimbing = true;
    private bool climbHasLeftGround = false;


    [Header("Neighbor Observations")]
    public float neighborRadius = 4f;
    public int maxNeighbors = 4;
    private Collider2D currentAntContact;
    public float orbitDegreesPerSecond = 180f;

    public LayerMask antLayer;

    [Header("Rewards")]
    public float stepPenalty = -0.0005f;
    public float progressRewardScale = 0.1f;
    public float foodReward = 1f;

    private Rigidbody2D rb;
    private float previousDistanceToFood;
    private int antContactCount = 0;
    private readonly HashSet<Collider2D> groundContacts = new HashSet<Collider2D>();
    private readonly HashSet<Collider2D> antContacts = new HashSet<Collider2D>();

    public bool IsGrounded => isGrounded;
    public bool IsTouchingAnt => touchingAnt;
    public bool IsClimbing => isClimbing;
    public bool IsTouchingAnything => contact;
    public int AntContactCount => antContactCount;

    public override void Initialize()
    {
        rb = GetComponent<Rigidbody2D>();
        rayPerceptionSensorLocation = transform.Find("RayPerceptionSensor").gameObject;
        rb.constraints = RigidbodyConstraints2D.FreezeRotation;
        rb.angularVelocity = 0f;
        transform.rotation = Quaternion.identity;
        spriteRenderer = GetComponent<SpriteRenderer>();
    }

    public void Setup(AntArenaController controller, Transform food)
    {
        arena = controller;
        foodTransform = food;
    }

    public override void OnEpisodeBegin()
    {
        ResetAgentState();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        if (rb == null)
            rb = GetComponent<Rigidbody2D>();

        // 1. Self velocity in local frame
        Vector2 localVelocity = transform.InverseTransformDirection(rb.linearVelocity);
        float speedScale = maxSpeed > 0f ? maxSpeed : 1f;
        sensor.AddObservation(localVelocity.x / speedScale);
        sensor.AddObservation(localVelocity.y / speedScale);

        // 2. Explicit contact state.
        sensor.AddObservation(isGrounded ? 1f : 0f);
        sensor.AddObservation(isClimbing ? 1f : 0f);
        sensor.AddObservation(GetAntProximityObservation());
        sensor.AddObservation(touchingAnt ? 1f : 0f);

        // 3. Direction + distance to food
        Vector2 toFood = foodTransform != null
            ? foodTransform.position - transform.position
            : Vector2.zero;
        Vector2 localToFood = transform.InverseTransformDirection(toFood);

        sensor.AddObservation(localToFood.x);
        sensor.AddObservation(localToFood.y);
        sensor.AddObservation(toFood.magnitude);

    }

    public override void OnActionReceived(ActionBuffers actionBuffers)
    {
        var continuous = actionBuffers.ContinuousActions;
        var discreteActions = actionBuffers.DiscreteActions;

        if (rb == null)
            rb = GetComponent<Rigidbody2D>();

        float moveX = continuous[0];

        if (moveX < 0f && facingRight)
        {
            rayPerceptionSensorLocation.transform.localPosition = new Vector3(-rayPerceptionSensorLocation.transform.localPosition.x, 0f, 0f);
            rayPerceptionSensorLocation.transform.localRotation = Quaternion.Euler(0f, 0f, -rayPerceptionSensorLocation.transform.localEulerAngles.z);
            facingRight = false;
        }
        else if (moveX > 0f && !facingRight)
        {
            rayPerceptionSensorLocation.transform.localPosition = new Vector3(-rayPerceptionSensorLocation.transform.localPosition.x, 0f, 0f);
            rayPerceptionSensorLocation.transform.localRotation = Quaternion.Euler(0f, 0f, -rayPerceptionSensorLocation.transform.localEulerAngles.z);
            facingRight = true;
        }

        bool contactingAnt = touchingAnt && currentAntContact != null;
        int wantsAttach = discreteActions[0];
        bool isLocked = contactingAnt && wantsAttach == 1;
        bool landedFromClimb = isClimbing && climbHasLeftGround && isGrounded;

        rb.constraints = RigidbodyConstraints2D.FreezeRotation;
        rb.angularVelocity = 0f;
        transform.rotation = Quaternion.identity;

        if (landedFromClimb)
        {
            isClimbing = false;
            climbHasLeftGround = false;
            canStartClimbing = false;
        }

        bool shouldClimb = contactingAnt && canStartClimbing && !landedFromClimb;

        // Attach locks in place, unlocked ant contact climbs, ground contact after climbing returns to normal walking.
        if (isLocked) {
            isClimbing = false;
            rb.linearVelocity = Vector2.zero;
            rb.angularVelocity = 0f;
            rb.constraints = RigidbodyConstraints2D.FreezePosition |
                             RigidbodyConstraints2D.FreezeRotation;
            spriteRenderer.color = Color.blue;
        } else if (shouldClimb) {
            if (!isClimbing)
            {
                isClimbing = true;
                climbHasLeftGround = !isGrounded;
            }
            else if (!isGrounded)
            {
                climbHasLeftGround = true;
            }

            rb.linearVelocity = Vector2.zero;
            rb.angularVelocity = 0f;

            Vector3 otherCenter = currentAntContact.bounds.center;
            Vector3 offset = transform.position - otherCenter;

            float angleDelta = -moveX * orbitDegreesPerSecond * Time.fixedDeltaTime;
            Vector3 newOffset = Quaternion.Euler(0f, 0f, angleDelta) * offset;

            transform.position = otherCenter + newOffset;
            transform.rotation = Quaternion.identity;
        }
        else if (isGrounded) {
            isClimbing = false;
            rb.linearVelocity = new Vector2(moveX * maxSpeed, rb.linearVelocity.y);
        } else {
            isClimbing = false;
        }

        if (!isLocked)
        {
            spriteRenderer.color = Color.red;
        }

        AddReward(stepPenalty);

        if (foodTransform == null)
        {
            return;
        }

        // Rewards the act of moving closer
        float currentDistance = Vector2.Distance(transform.position, foodTransform.position);
        float progress = previousDistanceToFood - currentDistance;
        AddReward(progress * progressRewardScale);
        previousDistanceToFood = currentDistance;
    }

    public void RefreshDistanceToFood()
    {
        if (foodTransform != null)
        {
            float currentDistance = Vector2.Distance(transform.position, foodTransform.position);
            previousDistanceToFood = currentDistance;

        }
    }

    public void ResetAgentState()
    {
        contactCount = 0;
        contact = false;
        isGrounded = false;
        touchingAnt = false;
        isClimbing = false;
        canStartClimbing = true;
        climbHasLeftGround = false;
        currentAntContact = null;
        antContactCount = 0;
        groundContacts.Clear();
        antContacts.Clear();

        if (rb == null)
            rb = GetComponent<Rigidbody2D>();

        rb.constraints = RigidbodyConstraints2D.FreezeRotation;
        rb.linearVelocity = Vector2.zero;
        rb.angularVelocity = 0f;
        transform.rotation = Quaternion.identity;

        RefreshDistanceToFood();
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var continuousActionsOut = actionsOut.ContinuousActions;
        var discreteActionsOut = actionsOut.DiscreteActions;

        continuousActionsOut[0] = Input.GetAxis("Horizontal");
        discreteActionsOut[0] = Input.GetKey(KeyCode.Space) ? 1 : 0;
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (arena != null && IsAssignedFood(other))
        {
            AddReward(foodReward);
            arena.NotifyFoodReached(this);
            // Debug.Log($"Ant reached food!");
        }
    }

    private void OnCollisionEnter2D(Collision2D collision)
    {
        TrackCollision(collision);
    }

    private void OnCollisionStay2D(Collision2D collision)
    {
        TrackCollision(collision);
    }

    private void OnCollisionExit2D(Collision2D collision)
    {
        Collider2D other = collision.collider;
        groundContacts.Remove(other);
        antContacts.Remove(other);

        if (other == currentAntContact)
        {
            currentAntContact = GetAnyAntContact();
        }

        UpdateContactFlags();
    }

    private void TrackCollision(Collision2D collision)
    {
        Collider2D other = collision.collider;

        if (other.CompareTag("agent"))
        {
            if (IsSameArenaAnt(other))
            {
                antContacts.Add(other);
                currentAntContact = other;
            }
            else
            {
                antContacts.Remove(other);
                if (other == currentAntContact)
                {
                    currentAntContact = GetAnyAntContact();
                }
            }
        }
        else
        {
            bool ground = false;

            for (int i = 0; i < collision.contactCount; i++)
            {
                Vector2 normal = collision.GetContact(i).normal;
                ground |= normal.y > 0.5f;
            }

            UpdateContactSet(groundContacts, other, ground);
        }

        UpdateContactFlags();
    }

    private void UpdateContactFlags()
    {
        isGrounded = groundContacts.Count > 0;
        touchingAnt = antContacts.Count > 0;
        antContactCount = antContacts.Count;
        contactCount = groundContacts.Count + antContacts.Count;
        contact = contactCount > 0;

        if (!touchingAnt)
        {
            currentAntContact = null;
            isClimbing = false;
            canStartClimbing = true;
            climbHasLeftGround = false;
        }
    }

    private static void UpdateContactSet(HashSet<Collider2D> contacts, Collider2D other, bool isTouching)
    {
        if (isTouching)
        {
            contacts.Add(other);
        }
        else
        {
            contacts.Remove(other);
        }
    }

    private Collider2D GetAnyAntContact()
    {
        foreach (Collider2D antContact in antContacts)
        {
            return antContact;
        }

        return null;
    }

    public int GetNearbyAntCount()
    {
        Collider2D[] hits = Physics2D.OverlapCircleAll(transform.position, neighborRadius, antLayer);

        int count = 0;
        foreach (Collider2D hit in hits)
        {
            if (IsSameArenaAnt(hit))
            {
                count++;
            }
        }

        return count;
    }

    private float GetAntProximityObservation()
    {
        return Mathf.Clamp01((float)GetNearbyAntCount() / maxNeighbors);
    }

    private bool IsAssignedFood(Collider2D other)
    {
        return foodTransform != null &&
               (other.transform == foodTransform || other.transform.IsChildOf(foodTransform));
    }

    private bool IsSameArenaAnt(Collider2D other)
    {
        if (arena == null || other == null || !other.CompareTag("agent"))
        {
            return false;
        }

        AntAgent otherAnt = other.GetComponentInParent<AntAgent>();
        return otherAnt != null && otherAnt != this && otherAnt.arena == arena;
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, neighborRadius);
    }

}
