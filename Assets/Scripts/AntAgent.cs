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

    [Header("Movement")]
    public float moveForce = 8f;
    public float maxSpeed = 2f;
    private int contactCount = 0;
    private bool contact = false;
    private bool isGrounded = false;
    private bool touchingWall = false;
    private bool touchingAnt = false;
    private bool isClimbing = false;


    [Header("Neighbor Observations")]
    public float neighborRadius = 4f;
    public int maxNeighbors = 3;
    private Collider2D currentAntContact;
    public float orbitDegreesPerSecond = 180f;

    public LayerMask antLayer;

    [Header("Rewards")]
    public float stepPenalty = -0.0005f;
    public float progressRewardScale = 0.05f;
    public float foodReward = 10.0f;
    private float bestDistanceToFood;

    private Rigidbody2D rb;
    private float previousDistanceToFood;
    private int antContactCount = 0;
    private readonly HashSet<Collider2D> groundContacts = new HashSet<Collider2D>();
    private readonly HashSet<Collider2D> wallContacts = new HashSet<Collider2D>();
    private readonly HashSet<Collider2D> antContacts = new HashSet<Collider2D>();

    public bool IsGrounded => isGrounded;
    public bool IsTouchingWall => touchingWall;
    public bool IsTouchingAnt => touchingAnt;
    public bool IsClimbing => isClimbing;
    public bool IsTouchingAnything => contact;
    public int AntContactCount => antContactCount;

    public override void Initialize()
    {
        rb = GetComponent<Rigidbody2D>();
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

        // 2. Explicit contact state. Keep this at 4 observations so the vector size stays 9.
        sensor.AddObservation(isGrounded ? 1f : 0f);
        sensor.AddObservation(touchingWall ? 1f : 0f);
        sensor.AddObservation(isClimbing ? 1f : 0f);
        sensor.AddObservation(GetAntProximityObservation());

        // 3. Direction + distance to food
        Vector2 toFood = foodTransform != null
            ? foodTransform.position - transform.position
            : Vector2.zero;
        Vector2 localToFood = transform.InverseTransformDirection(toFood);

        sensor.AddObservation(localToFood.x);
        sensor.AddObservation(localToFood.y);
        sensor.AddObservation(toFood.magnitude);

    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        var continuous = actions.ContinuousActions;
        var discreteActions = actions.DiscreteActions;

        if (rb == null)
            rb = GetComponent<Rigidbody2D>();

        float moveX = continuous.Length > 0 ? Mathf.Clamp(continuous[0], -1f, 1f) : 0f;
        int isLocked = discreteActions.Length > 0 ? discreteActions[0] : 0;

        rb.constraints = RigidbodyConstraints2D.FreezeRotation;

        if (touchingAnt && currentAntContact != null)
        {
            if (isLocked == 1)
            {
                isClimbing = false;
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;
                rb.constraints = RigidbodyConstraints2D.FreezePosition |
                                 RigidbodyConstraints2D.FreezeRotation;
            }
            else
            {
                isClimbing = true;
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;

                Vector3 otherCenter = currentAntContact.bounds.center;
                float angleDelta = -moveX * orbitDegreesPerSecond * Time.fixedDeltaTime;

                transform.RotateAround(otherCenter, Vector3.forward, angleDelta);
            }
        }
        else if (isGrounded)
        {
            isClimbing = false;
            rb.linearVelocity = new Vector2(moveX * maxSpeed, rb.linearVelocity.y);
        }
        else
        {
            isClimbing = false;
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

        // Rewards getting as close as possible
        float improvementThreshold = 0.05f;

        if (currentDistance < bestDistanceToFood - improvementThreshold)
        {
            float improvement = bestDistanceToFood - currentDistance;
            AddReward(improvement * progressRewardScale);
            bestDistanceToFood = currentDistance;
        }
    }

    public void RefreshDistanceToFood()
    {
        if (foodTransform != null)
        {
            float currentDistance = Vector2.Distance(transform.position, foodTransform.position);
            previousDistanceToFood = currentDistance;
            bestDistanceToFood = currentDistance;

        }
    }

    public void ResetAgentState()
    {
        contactCount = 0;
        contact = false;
        isGrounded = false;
        touchingWall = false;
        touchingAnt = false;
        isClimbing = false;
        currentAntContact = null;
        antContactCount = 0;
        groundContacts.Clear();
        wallContacts.Clear();
        antContacts.Clear();

        if (rb == null)
            rb = GetComponent<Rigidbody2D>();

        rb.constraints = RigidbodyConstraints2D.FreezeRotation;
        rb.linearVelocity = Vector2.zero;
        rb.angularVelocity = 0f;

        RefreshDistanceToFood();
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var continuousActionsOut = actionsOut.ContinuousActions;
        if (continuousActionsOut.Length > 0)
            continuousActionsOut[0] = Input.GetAxis("Horizontal");
        var discreteActionsOut = actionsOut.DiscreteActions;
        discreteActionsOut[0] = Input.GetKey(KeyCode.Space) ? 1 : 0;
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (other.CompareTag("food"))
        {
            AddReward(foodReward);
            arena.NotifyFoodReached(this);
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
        wallContacts.Remove(other);
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
            antContacts.Add(other);
            currentAntContact = other;
        }
        else
        {
            bool ground = false;
            bool wall = false;

            for (int i = 0; i < collision.contactCount; i++)
            {
                Vector2 normal = collision.GetContact(i).normal;
                ground |= normal.y > 0.5f;
                wall |= Mathf.Abs(normal.x) > 0.5f && normal.y < 0.5f;
            }

            UpdateContactSet(groundContacts, other, ground);
            UpdateContactSet(wallContacts, other, wall);
        }

        UpdateContactFlags();
    }

    private void UpdateContactFlags()
    {
        isGrounded = groundContacts.Count > 0;
        touchingWall = wallContacts.Count > 0;
        touchingAnt = antContacts.Count > 0;
        antContactCount = antContacts.Count;
        contactCount = groundContacts.Count + wallContacts.Count + antContacts.Count;
        contact = contactCount > 0;

        if (!touchingAnt)
        {
            currentAntContact = null;
            isClimbing = false;
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
            if (hit.gameObject != gameObject && hit.CompareTag("agent"))
            {
                count++;
            }
        }

        return count;
    }

    private float GetAntProximityObservation()
    {
        if (touchingAnt)
        {
            return 1f;
        }

        if (maxNeighbors <= 0)
        {
            return 0f;
        }

        return Mathf.Clamp01((float)GetNearbyAntCount() / (maxNeighbors + 1f));
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, neighborRadius);
    }

}
