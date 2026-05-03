using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.SideChannels;

public class AntArenaController : MonoBehaviour
{
    [Header("References")]
    [SerializeField, HideInInspector] private GameObject food;
    [SerializeField] private Transform environmentRoot;
    [SerializeField] private Transform foodTransform;
    [SerializeField] private Collider2D groundCollider;
    [SerializeField] private GameObject antPrefab;
    [SerializeField, Min(1)] private int antSpawnCount = 6;
    public List<AntAgent> ants = new List<AntAgent>();
    private readonly HashSet<AntAgent> uniqueAntsThatReached = new HashSet<AntAgent>();
    private bool success = false;
    private bool successAdded = false;
    private int reachedNumberToWin = 3;
    private bool teamSuccess = false;

    [Header("Stats Overlay")]
    [SerializeField] private bool showStatsOverlay = true;
    private int episodeNumber = 1;
    private int episodeFoodSuccesses = 0;
    private int totalFoodSuccesses = 0;
    private int totalTeamSuccesses = 0;

    [Header("Ant Spawn")]
    private float antRadius;
    [SerializeField] private float minSpawnX = -4f;
    [SerializeField] private float maxSpawnX = 4f;
    [SerializeField] private int maxSpawnAttemptsPerAnt = 50;
    StatsRecorder statsRecorder;
    public enum RewardMode
    {
        Individual,
        Cooperative
    }

    [Header("Experiment")]
    public RewardMode rewardMode = RewardMode.Individual;
    private int maxEnvironmentSteps = 10000;
    private int episodeStepCount = 0;
    private int totalStepCount = 0;
    private bool episodeEnded = false;
    private bool timedOut = false;

    // Optional bookkeeping for custom reward stats
    private float teamEpisodeReward = 0f;
    // private Dictionary<AntAgent, float> individualEpisodeRewards = new Dictionary<AntAgent, float>();

    public void Update() {

        statsRecorder.Add(
            "AntArena/SuccessRate",
            success ? 1f : 0f,
            StatAggregationMethod.Average
        );

        if (!successAdded && success) {


            statsRecorder.Add(
                "AntArena/StepsToSuccess",
                episodeStepCount,
                StatAggregationMethod.Average
            );

            successAdded = true;
        }

        statsRecorder.Add(
            "AntArena/TeamSuccessRate",
            teamSuccess ? 1f : 0f,
            StatAggregationMethod.Average
        );

        if (teamSuccess) {

            statsRecorder.Add(
                "AntArena/TeamStepsToSuccess",
                episodeStepCount,
                StatAggregationMethod.Average
            );

            teamSuccess = false;
        }

    }

    private void FixedUpdate() {
        if (episodeEnded)
        {
            return;
        }

        episodeStepCount++;
        totalStepCount++;

        if (episodeStepCount >= maxEnvironmentSteps)
        {
            timedOut = true;
            EndArenaEpisode(false);
        }
    }

    public void Awake()
    {
        Academy.Instance.OnEnvironmentReset += EnvironmentReset;
        statsRecorder = Academy.Instance.StatsRecorder;

        CacheSceneReferences();
        if (foodTransform == null)
        {
            return;
        }

        SpawnAnts();

        foreach (AntAgent ant in ants)
        {
            ant.Setup(this, foodTransform);
        }

        if (ants.Count > 0)
        {
            CircleCollider2D collider = ants[0].GetComponent<CircleCollider2D>();
            if (collider != null)
            {
                antRadius = collider.radius;
            }
        }

        EnvironmentReset();
    }

    private void OnDestroy()
    {
        if (Academy.IsInitialized)
        {
            Academy.Instance.OnEnvironmentReset -= EnvironmentReset;
        }
    }

    private void CacheSceneReferences()
    {
        if (environmentRoot == null)
        {
            environmentRoot = transform.parent != null ? transform.parent : transform;
        }

        if (foodTransform == null && food != null)
        {
            foodTransform = food.transform;
        }

        if (foodTransform == null)
        {
            foodTransform = FindChildWithTag(environmentRoot, "food");
        }

        if (groundCollider == null)
        {
            groundCollider = FindChildColliderWithTag(environmentRoot, "ground");
        }

        if (ants == null)
        {
            ants = new List<AntAgent>();
        }

        if (foodTransform == null)
        {
            Debug.LogError($"{name} could not find a child GameObject tagged 'food' under {environmentRoot.name}.");
            return;
        }

        if (groundCollider == null)
        {
            Debug.LogWarning($"{name} could not find a child Collider2D tagged 'ground' under {environmentRoot.name}. Falling back to local Y 0 for ant spawns.");
        }
    }

    private void SpawnAnts()
    {
        ants.Clear();

        GameObject prefab = GetAntPrefab();
        if (prefab == null)
        {
            Debug.LogError($"{name} cannot spawn ants because no ant prefab is assigned.");
            return;
        }

        for (int i = 0; i < antSpawnCount; i++)
        {
            GameObject antObject = Instantiate(prefab, environmentRoot, false);
            antObject.name = $"{prefab.name} {i + 1}";
            antObject.SetActive(true);

            AntAgent ant = antObject.GetComponent<AntAgent>();
            if (ant == null)
            {
                Debug.LogError($"{name} spawned {prefab.name}, but it does not have an AntAgent component.");
                Destroy(antObject);
                continue;
            }

            ants.Add(ant);
        }

        Debug.Log($"{name} spawned {ants.Count} ants under {environmentRoot.name}.");
    }

    private GameObject GetAntPrefab()
    {
        if (antPrefab != null)
        {
            return antPrefab;
        }

#if UNITY_EDITOR
        antPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Ant.prefab");
#endif

        return antPrefab;
    }

    public void EnvironmentReset()
    {

        Debug.Log($"ENVIRONMENT RESET!");

        if (episodeEnded || episodeStepCount > 0 || episodeFoodSuccesses > 0 || uniqueAntsThatReached.Count > 0)
        {
            episodeNumber++;
        }

        success = false;
        successAdded = false;
        episodeStepCount = 0;
        episodeFoodSuccesses = 0;
        episodeEnded = false;
        timedOut = false;
        uniqueAntsThatReached.Clear();
        teamSuccess = false;

        if (ants == null || ants.Count == 0 || foodTransform == null) return;

        List<Vector2> placedPositions = new List<Vector2>();

        ResetFoodPosition();

        foreach (AntAgent ant in ants)
        {
            Vector2 spawnPos = GetRandomGroundSpawnLocalPosition(placedPositions);
            ResetAntPosition(ant, spawnPos);
            placedPositions.Add(spawnPos);
        }

    }

    private void ResetFoodPosition() {
        Vector3 foodLocalPosition = GetEnvironmentLocalPosition(foodTransform);
        foodLocalPosition.x = Random.Range(minSpawnX, maxSpawnX);
        SetEnvironmentLocalPosition(foodTransform, foodLocalPosition);
    }

    public void ResetAntPosition(AntAgent ant, Vector2 localSpawnPos)
    {
        Vector3 antLocalPosition = GetEnvironmentLocalPosition(ant.transform);
        antLocalPosition.x = localSpawnPos.x;
        antLocalPosition.y = localSpawnPos.y;
        SetEnvironmentLocalPosition(ant.transform, antLocalPosition);
        ant.transform.rotation = Quaternion.identity;
        ant.ResetAgentState();
    }

    /// <summary>
    /// Attempts to find a random spawn position for an ant on the ground that does not overlap with existing ants.
    /// </summary>
    /// <param name="placedPositions">This holds the positions of already placed ants to prevent overlaps.</param>
    /// <returns></returns>
    private Vector2 GetRandomGroundSpawnLocalPosition(List<Vector2> placedPositions)
    {
        float spawnY = GetGroundSurfaceLocalY() + antRadius; // Slightly above ground to prevent spawning inside it
        float minAntSpacing = antRadius * 1.1f;
        for (int attempt = 0; attempt < maxSpawnAttemptsPerAnt; attempt++)
        {
            float randomX = Random.Range(minSpawnX, maxSpawnX);
            Vector2 candidate = new Vector2(randomX, spawnY);

            bool overlapsExistingAnt = false;

            foreach (Vector2 placedPos in placedPositions)
            {
                if (Vector2.Distance(candidate, placedPos) < minAntSpacing)
                {
                    overlapsExistingAnt = true;
                    break;
                }
            }

            if (!overlapsExistingAnt)
            {
                return candidate;
            }
        }

        // Fallback if the spawn range is too crowded.
        // This prevents infinite loops, but you should widen the range
        // or reduce minAntSpacing if this happens often.
        Debug.LogWarning("Could not find non-overlapping spawn position. Using fallback position.");
        return new Vector2(Random.Range(minSpawnX, maxSpawnX), spawnY);
    }

    public void NotifyFoodReached(AntAgent winner)
    {
        Vector2 newSpawn = GetRandomGroundSpawnLocalPosition(GetAllOtherAntPositions(winner));
        ResetAntPosition(winner, newSpawn);
        Debug.Log($"Food reached by {winner.name}!");
        success = true;
        episodeFoodSuccesses++;
        totalFoodSuccesses++;

        uniqueAntsThatReached.Add(winner);

        if (uniqueAntsThatReached.Count >= reachedNumberToWin) {

            teamSuccess = true;
            EndArenaEpisode(true);
        }

    }

    private void EndArenaEpisode(bool won)
    {

        episodeEnded = true;
        teamSuccess = won;

        if (won)
        {
            totalTeamSuccesses++;
        }

        foreach (AntAgent ant in ants)
        {
            ant.EndEpisode();
        }

        EnvironmentReset();
    }

    private List<Vector2> GetAllOtherAntPositions(AntAgent excludedAnt)
    {
        List<Vector2> positions = new List<Vector2>();

        foreach (AntAgent ant in ants)
        {
            if (ant != excludedAnt)
            {
                Vector3 localPosition = GetEnvironmentLocalPosition(ant.transform);
                positions.Add(localPosition);
            }
        }

        return positions;
    }

    private Transform FindChildWithTag(Transform root, string tagName)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child.CompareTag(tagName))
            {
                return child;
            }
        }

        return null;
    }

    private Collider2D FindChildColliderWithTag(Transform root, string tagName)
    {
        foreach (Collider2D collider in root.GetComponentsInChildren<Collider2D>(true))
        {
            if (collider.CompareTag(tagName))
            {
                return collider;
            }
        }

        return null;
    }

    private float GetGroundSurfaceLocalY()
    {
        if (groundCollider == null)
        {
            return 0f;
        }

        return GetEnvironmentLocalPosition(groundCollider.bounds.max).y;
    }

    private Vector3 GetEnvironmentLocalPosition(Transform target)
    {
        return GetEnvironmentLocalPosition(target.position);
    }

    private Vector3 GetEnvironmentLocalPosition(Vector3 worldPosition)
    {
        return environmentRoot != null
            ? environmentRoot.InverseTransformPoint(worldPosition)
            : worldPosition;
    }

    private void SetEnvironmentLocalPosition(Transform target, Vector3 localPosition)
    {
        target.position = environmentRoot != null
            ? environmentRoot.TransformPoint(localPosition)
            : localPosition;
    }

    private void OnGUI()
    {
        if (!showStatsOverlay)
        {
            return;
        }

        Rect boxRect = new Rect(10f, 10f, 340f, 194f);
        Color previousColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.85f);
        GUI.DrawTexture(boxRect, Texture2D.whiteTexture);
        GUI.color = previousColor;

        GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
        labelStyle.normal.textColor = Color.white;
        labelStyle.fontSize = 14;

        GUILayout.BeginArea(new Rect(22f, 20f, 316f, 176f));
        GUILayout.Label($"Episode: {episodeNumber}", labelStyle);
        GUILayout.Label($"Episode Steps: {episodeStepCount} / {maxEnvironmentSteps}", labelStyle);
        GUILayout.Label($"Total Steps: {totalStepCount}", labelStyle);
        GUILayout.Label($"Ants: {ants.Count}", labelStyle);
        GUILayout.Label($"Food Successes: {totalFoodSuccesses}", labelStyle);
        GUILayout.Label($"Team Successes: {totalTeamSuccesses}", labelStyle);
        GUILayout.Label($"Unique Ants Reached: {uniqueAntsThatReached.Count} / {reachedNumberToWin}", labelStyle);
        GUILayout.EndArea();
    }
}
