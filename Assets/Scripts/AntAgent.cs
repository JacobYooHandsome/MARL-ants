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
    [SerializeField, Min(0f)] private float supportVerticalMargin = 0.05f;


    [Header("Neighbor Observations")]
    public float neighborRadius = 4f;
    public int maxNeighbors = 4;
    private Collider2D currentAntContact;
    public float orbitDegreesPerSecond = 180f;

    public LayerMask antLayer;

    [Header("Rewards")]
    public float stepPenalty = -0.0005f;
    public float progressRewardScale = 0.1f;

    [Header("Visual Feedback")]
    [SerializeField] private Color supportRewardGlowColor = Color.green;
    [SerializeField, Min(0f)] private float supportRewardGlowDuration = 0.1f;

    private Rigidbody2D rb;
    private float previousDistanceToFood;
    private int antContactCount = 0;
    private bool isLockedVisualState = false;
    private float supportRewardGlowEndTime = -1f;
    private Collider2D lockedContact;
    private FixedJoint2D attachmentJoint;
    private readonly HashSet<Collider2D> groundContacts = new HashSet<Collider2D>();
    private readonly HashSet<Collider2D> surfaceContacts = new HashSet<Collider2D>();
    private readonly HashSet<Collider2D> antContacts = new HashSet<Collider2D>();
    private readonly HashSet<AntAgent> supportCheckVisited = new HashSet<AntAgent>();

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
        ConfigureAttachmentJoint();
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

    private void Update()
    {
        ApplyVisualState();
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

        bool wantsAttach = discreteActions[0] == 1;
        Collider2D attachmentContact = GetAttachmentContact();
        Collider2D climbContact = GetSupportedAntContact();
        bool isLocked = wantsAttach && TryAttachToContact(attachmentContact);
        isLockedVisualState = isLocked;
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

        bool shouldClimb = climbContact != null && canStartClimbing && !landedFromClimb;

        // Attach follows a contacted ant, unlocked ant contact climbs, ground contact after climbing returns to normal walking.
        if (isLocked) {
            isClimbing = false;
            rb.angularVelocity = 0f;
        } else if (shouldClimb) {
            ReleaseAttachment();
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

            Vector3 otherCenter = climbContact.bounds.center;
            Vector3 offset = transform.position - otherCenter;

            float angleDelta = -moveX * orbitDegreesPerSecond * Time.fixedDeltaTime;
            Vector3 newOffset = Quaternion.Euler(0f, 0f, angleDelta) * offset;

            transform.position = otherCenter + newOffset;
            transform.rotation = Quaternion.identity;
        }
        else if (isGrounded) {
            ReleaseAttachment();
            isClimbing = false;
            rb.linearVelocity = new Vector2(moveX * maxSpeed, rb.linearVelocity.y);
        } else {
            ReleaseAttachment();
            isClimbing = false;
        }

        ApplyVisualState();

        AddReward(stepPenalty);

        if (foodTransform == null)
        {
            return;
        }

        // Rewards the act of moving closer
        float currentDistance = Vector2.Distance(transform.position, foodTransform.position);
        float progress = previousDistanceToFood - currentDistance;
        float progressReward = progress * progressRewardScale;

        if (arena != null)
        {
            arena.NotifyPositiveProgress(this, progressReward);
        }
        else
        {
            AddReward(progressReward);
        }

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
        ReleaseAttachment();
        antContactCount = 0;
        isLockedVisualState = false;
        supportRewardGlowEndTime = -1f;
        groundContacts.Clear();
        surfaceContacts.Clear();
        antContacts.Clear();

        if (rb == null)
            rb = GetComponent<Rigidbody2D>();

        rb.constraints = RigidbodyConstraints2D.FreezeRotation;
        rb.linearVelocity = Vector2.zero;
        rb.angularVelocity = 0f;
        transform.rotation = Quaternion.identity;

        RefreshDistanceToFood();
        ApplyVisualState();
    }

    public void ShowSupportRewardGlow()
    {
        supportRewardGlowEndTime = Time.time + supportRewardGlowDuration;
        ApplyVisualState();
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
        if (IsAssignedFood(other))
        {
            if (arena != null)
            {
                arena.NotifyFoodReached(this);
            }
            else
            {
                Debug.LogWarning($"{name} reached food, but no AntArenaController is assigned to provide the food reward.");
            }

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
        surfaceContacts.Remove(other);
        antContacts.Remove(other);

        if (other == currentAntContact)
        {
            currentAntContact = GetAnyAntContact();
        }

        if (other == lockedContact)
        {
            ReleaseAttachment();
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
            bool surface = collision.contactCount > 0;

            for (int i = 0; i < collision.contactCount; i++)
            {
                Vector2 normal = collision.GetContact(i).normal;
                ground |= normal.y > 0.5f;
            }

            UpdateContactSet(groundContacts, other, ground);
            UpdateContactSet(surfaceContacts, other, surface);
        }

        UpdateContactFlags();
    }

    private void UpdateContactFlags()
    {
        isGrounded = groundContacts.Count > 0;
        touchingAnt = antContacts.Count > 0;
        antContactCount = antContacts.Count;
        contactCount = surfaceContacts.Count + antContacts.Count;
        contact = contactCount > 0;

        if (!touchingAnt)
        {
            currentAntContact = null;
            isClimbing = false;
            canStartClimbing = true;
            climbHasLeftGround = false;
        }

        if (!contact)
        {
            ReleaseAttachment();
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

    private Collider2D GetAttachmentContact()
    {
        if (IsValidAttachmentContact(lockedContact))
        {
            return lockedContact;
        }

        if (IsValidAttachmentContact(currentAntContact))
        {
            return currentAntContact;
        }

        foreach (Collider2D antContact in antContacts)
        {
            if (IsValidAttachmentContact(antContact))
            {
                return antContact;
            }
        }

        return null;
    }

    private Collider2D GetSupportedAntContact()
    {
        if (IsSupportedAntContact(currentAntContact))
        {
            return currentAntContact;
        }

        foreach (Collider2D antContact in antContacts)
        {
            if (IsSupportedAntContact(antContact))
            {
                return antContact;
            }
        }

        return null;
    }

    private bool IsSupportedAntContact(Collider2D contactCollider)
    {
        AntAgent otherAnt = GetSameArenaAnt(contactCollider);
        return otherAnt != null && otherAnt.HasGroundSupport();
    }

    private bool HasGroundSupport()
    {
        supportCheckVisited.Clear();
        return HasGroundSupport(supportCheckVisited);
    }

    private bool HasGroundSupport(HashSet<AntAgent> visited)
    {
        if (isGrounded)
        {
            return true;
        }

        if (!visited.Add(this))
        {
            return false;
        }

        foreach (Collider2D antContact in antContacts)
        {
            AntAgent candidateSupporter = GetSameArenaAnt(antContact);
            if (candidateSupporter == null || !candidateSupporter.IsLowerThan(this))
            {
                continue;
            }

            if (candidateSupporter.HasGroundSupport(visited))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsLowerThan(AntAgent dependentAnt)
    {
        return dependentAnt != null &&
               transform.position.y <= dependentAnt.transform.position.y - supportVerticalMargin;
    }

    private bool TryAttachToContact(Collider2D contactCollider)
    {
        if (!IsValidAttachmentContact(contactCollider))
        {
            ReleaseAttachment();
            return false;
        }

        if (attachmentJoint == null)
        {
            ConfigureAttachmentJoint();
        }

        if (attachmentJoint == null)
        {
            return false;
        }

        if (attachmentJoint.enabled && lockedContact == contactCollider)
        {
            return true;
        }

        Rigidbody2D connectedBody = contactCollider.attachedRigidbody;
        if (connectedBody == rb)
        {
            ReleaseAttachment();
            return false;
        }

        Vector2 anchorWorldPosition = contactCollider.ClosestPoint(rb.position);
        if ((anchorWorldPosition - rb.position).sqrMagnitude < 0.0001f)
        {
            anchorWorldPosition = rb.position;
        }

        attachmentJoint.enabled = false;
        attachmentJoint.connectedBody = connectedBody;
        attachmentJoint.anchor = transform.InverseTransformPoint(anchorWorldPosition);
        attachmentJoint.connectedAnchor = connectedBody != null
            ? connectedBody.transform.InverseTransformPoint(anchorWorldPosition)
            : anchorWorldPosition;
        attachmentJoint.enabled = true;
        lockedContact = contactCollider;

        return true;
    }

    private void ConfigureAttachmentJoint()
    {
        if (attachmentJoint == null)
        {
            attachmentJoint = GetComponent<FixedJoint2D>();
        }

        if (attachmentJoint == null)
        {
            attachmentJoint = gameObject.AddComponent<FixedJoint2D>();
        }

        attachmentJoint.autoConfigureConnectedAnchor = false;
        attachmentJoint.enableCollision = true;
        attachmentJoint.enabled = false;
    }

    private void ReleaseAttachment()
    {
        if (attachmentJoint != null)
        {
            attachmentJoint.enabled = false;
            attachmentJoint.connectedBody = null;
        }

        lockedContact = null;
    }

    private bool IsValidAttachmentContact(Collider2D contactCollider)
    {
        if (contactCollider == null)
        {
            return false;
        }

        return contactCollider.CompareTag("agent") &&
               antContacts.Contains(contactCollider) &&
               IsSameArenaAnt(contactCollider);
    }

    public void GetTouchingAnts(List<AntAgent> touchingAnts)
    {
        touchingAnts.Clear();

        foreach (Collider2D antContact in antContacts)
        {
            if (!IsSameArenaAnt(antContact))
            {
                continue;
            }

            AntAgent otherAnt = antContact.GetComponentInParent<AntAgent>();
            if (otherAnt != null)
            {
                touchingAnts.Add(otherAnt);
            }
        }
    }

    private void ApplyVisualState()
    {
        if (spriteRenderer == null)
        {
            spriteRenderer = GetComponent<SpriteRenderer>();
            if (spriteRenderer == null)
            {
                return;
            }
        }

        if (Time.time < supportRewardGlowEndTime)
        {
            spriteRenderer.color = supportRewardGlowColor;
        }
        else
        {
            spriteRenderer.color = isLockedVisualState ? Color.blue : Color.red;
        }
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
        return GetSameArenaAnt(other) != null;
    }

    private AntAgent GetSameArenaAnt(Collider2D other)
    {
        if (arena == null || other == null || !other.CompareTag("agent"))
        {
            return null;
        }

        AntAgent otherAnt = other.GetComponentInParent<AntAgent>();
        return otherAnt != null && otherAnt != this && otherAnt.arena == arena
            ? otherAnt
            : null;
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, neighborRadius);
    }

}
