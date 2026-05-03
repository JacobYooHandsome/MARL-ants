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
        BasePpo = 0,
        Team = 1,
        Reciprocal = 2,
        TeamAndReciprocal = 3
    }

    [Header("Experiment")]
    public RewardMode rewardMode = RewardMode.BasePpo;
    [SerializeField, Min(0f)] private float foodReward = 1f;
    [SerializeField, Min(0f)] private float supportVerticalMargin = 0.05f;
    private int maxEnvironmentSteps = 10000;
    private int episodeStepCount = 0;
    private int totalStepCount = 0;
    private bool episodeEnded = false;
    private bool timedOut = false;
    private RewardMode activeRewardMode = RewardMode.BasePpo;
    private int episodeTeamRewardEvents = 0;
    private int episodeReciprocalSuccessRewardEvents = 0;
    private int episodeReciprocalProgressRewardEvents = 0;
    private readonly List<AntAgent> supportChainBuffer = new List<AntAgent>();
    private readonly List<AntAgent> touchingAntBuffer = new List<AntAgent>();
    private readonly HashSet<AntAgent> supportVisited = new HashSet<AntAgent>();
    private readonly Stack<AntAgent> supportSearchStack = new Stack<AntAgent>();
    private const string RewardModeParameterName = "ant_reward_mode";
    private const float HalfRewardShare = 0.5f;
    private const float ThirdRewardShare = 1f / 3f;

    [Header("Reward Debug")]
    [SerializeField] private bool recordRewardDebugStats = true;
    [SerializeField] private bool debugFoodRewardSplits = false;
    [SerializeField] private bool debugProgressRewardSplits = false;
    [SerializeField, Min(0f)] private float rewardBudgetTolerance = 0.0001f;
    private int episodeRewardBudgetMismatches = 0;
    private float lastFoodRewardExpected = 0f;
    private float lastFoodRewardActual = 0f;
    private float lastFoodRewardError = 0f;
    private string lastFoodRewardSplit = "none";
    private float lastProgressRewardExpected = 0f;
    private float lastProgressRewardActual = 0f;
    private float lastProgressRewardError = 0f;
    private string lastProgressRewardSplit = "none";

    public void Update() {

        // statsRecorder.Add(
        //     "AntArena/EpisodeTeamRewardEvents",
        //     episodeTeamRewardEvents,
        //     StatAggregationMethod.MostRecent
        // );

        // statsRecorder.Add(
        //     "AntArena/EpisodeReciprocalSuccessRewardEvents",
        //     episodeReciprocalSuccessRewardEvents,
        //     StatAggregationMethod.MostRecent
        // );

        // statsRecorder.Add(
        //     "AntArena/EpisodeReciprocalProgressRewardEvents",
        //     episodeReciprocalProgressRewardEvents,
        //     StatAggregationMethod.MostRecent
        // );

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
        RefreshRewardMode();

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
        RefreshRewardMode();

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
        episodeTeamRewardEvents = 0;
        episodeReciprocalSuccessRewardEvents = 0;
        episodeReciprocalProgressRewardEvents = 0;
        episodeRewardBudgetMismatches = 0;
        lastFoodRewardExpected = 0f;
        lastFoodRewardActual = 0f;
        lastFoodRewardError = 0f;
        lastFoodRewardSplit = "none";
        lastProgressRewardExpected = 0f;
        lastProgressRewardActual = 0f;
        lastProgressRewardError = 0f;
        lastProgressRewardSplit = "none";

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
        if (winner == null)
        {
            return;
        }

        List<AntAgent> supportChain = GetSupportChain(winner);
        int supportChainCount = supportChain.Count;

        AddFoodRewards(winner, supportChain);
        RecordFoodSupportStats(supportChainCount);

        Vector2 newSpawn = GetRandomGroundSpawnLocalPosition(GetAllOtherAntPositions(winner));
        ResetAntPosition(winner, newSpawn);
        // Debug.Log($"Food reached by {winner.name}!");
        success = true;
        episodeFoodSuccesses++;
        totalFoodSuccesses++;

        uniqueAntsThatReached.Add(winner);

        if (uniqueAntsThatReached.Count >= reachedNumberToWin) {

            teamSuccess = true;
            EndArenaEpisode(true);
        }

    }

    private void AddFoodRewards(AntAgent winner, List<AntAgent> supportChain)
    {
        float configuredFoodReward = foodReward;
        float actualReward = 0f;
        float winnerReward = 0f;
        float teamReward = 0f;
        float reciprocalReward = 0f;
        float reciprocalFallbackReward = 0f;
        int teamRecipientCount = CountTeamRewardRecipients(winner);
        int supportRecipientCount = supportChain != null ? supportChain.Count : 0;

        switch (activeRewardMode)
        {
            case RewardMode.Team:
                winnerReward += AddTrackedReward(winner, configuredFoodReward * HalfRewardShare);
                actualReward += winnerReward;
                teamReward = AddTeamFoodReward(winner, configuredFoodReward * HalfRewardShare);
                actualReward += teamReward;
                if (teamRecipientCount == 0)
                {
                    winnerReward += teamReward;
                }
                break;
            case RewardMode.Reciprocal:
                winnerReward += AddTrackedReward(winner, configuredFoodReward * HalfRewardShare);
                actualReward += winnerReward;
                AddReciprocalFoodRewardOrWinnerFallback(
                    winner,
                    supportChain,
                    configuredFoodReward * HalfRewardShare,
                    out reciprocalReward,
                    out reciprocalFallbackReward
                );
                actualReward += reciprocalReward + reciprocalFallbackReward;
                winnerReward += reciprocalFallbackReward;
                break;
            case RewardMode.TeamAndReciprocal:
                winnerReward += AddTrackedReward(winner, configuredFoodReward * ThirdRewardShare);
                actualReward += winnerReward;
                teamReward = AddTeamFoodReward(winner, configuredFoodReward * ThirdRewardShare);
                actualReward += teamReward;
                if (teamRecipientCount == 0)
                {
                    winnerReward += teamReward;
                }
                AddReciprocalFoodRewardOrWinnerFallback(
                    winner,
                    supportChain,
                    configuredFoodReward * ThirdRewardShare,
                    out reciprocalReward,
                    out reciprocalFallbackReward
                );
                actualReward += reciprocalReward + reciprocalFallbackReward;
                winnerReward += reciprocalFallbackReward;
                break;
            case RewardMode.BasePpo:
            default:
                winnerReward += AddTrackedReward(winner, configuredFoodReward);
                actualReward += winnerReward;
                break;
        }

        string splitSummary =
            $"W={winnerReward:F4} T={teamReward:F4}/{teamRecipientCount} " +
            $"R={reciprocalReward:F4}/{supportRecipientCount} F={reciprocalFallbackReward:F4}";

        CheckRewardBudget(
            "Food",
            winner.name,
            configuredFoodReward,
            actualReward,
            splitSummary,
            debugFoodRewardSplits
        );
    }

    private void AddReciprocalFoodRewardOrWinnerFallback(
        AntAgent winner,
        List<AntAgent> supportChain,
        float reciprocalShare,
        out float reciprocalReward,
        out float fallbackReward
    )
    {
        reciprocalReward = 0f;
        fallbackReward = 0f;

        if (TryAddReciprocalSupportReward(
                supportChain,
                reciprocalShare,
                "AntArena/ReciprocalSuccessRewardValue"))
        {
            reciprocalReward = reciprocalShare;
            episodeReciprocalSuccessRewardEvents++;
            RecordStat("AntArena/ReciprocalSuccessRewardEvents", 1f, StatAggregationMethod.Sum);
            RecordStat("AntArena/ReciprocalSuccessSupportChainLength", supportChain.Count, StatAggregationMethod.Average);
            return;
        }

        fallbackReward = AddTrackedReward(winner, reciprocalShare);
    }

    public void NotifyPositiveProgress(AntAgent dependentAnt, float progressReward)
    {
        if (dependentAnt == null)
        {
            return;
        }

        if (!UsesReciprocalRewards() || progressReward <= 0f)
        {
            float actualReward = AddTrackedReward(dependentAnt, progressReward);
            if (progressReward > 0f || debugProgressRewardSplits)
            {
                CheckRewardBudget(
                    "Progress",
                    dependentAnt.name,
                    progressReward,
                    actualReward,
                    $"M={actualReward:F4} R=0.0000/0 F=0.0000",
                    debugProgressRewardSplits
                );
            }
            return;
        }

        List<AntAgent> supportChain = GetSupportChain(dependentAnt);
        if (supportChain.Count == 0)
        {
            float actualReward = AddTrackedReward(dependentAnt, progressReward);
            CheckRewardBudget(
                "Progress",
                dependentAnt.name,
                progressReward,
                actualReward,
                $"M={actualReward:F4} R=0.0000/0 F={progressReward:F4}",
                debugProgressRewardSplits
            );
            Debug.Log($"No supporters found for {dependentAnt.name} to receive reciprocal progress reward.");
            return;
        }

        float moverReward = AddTrackedReward(dependentAnt, progressReward * HalfRewardShare);
        float reciprocalReward = 0f;
        float fallbackReward = 0f;

        if (TryAddReciprocalSupportReward(
            supportChain,
            progressReward * HalfRewardShare,
            "AntArena/ReciprocalProgressRewardValue"
        ))
        {
            reciprocalReward = progressReward * HalfRewardShare;
        }
        else
        {
            fallbackReward = AddTrackedReward(dependentAnt, progressReward * HalfRewardShare);
            moverReward += fallbackReward;
        }

        float actualProgressReward = moverReward + reciprocalReward;
        CheckRewardBudget(
            "Progress",
            dependentAnt.name,
            progressReward,
            actualProgressReward,
            $"M={moverReward:F4} R={reciprocalReward:F4}/{supportChain.Count} F={fallbackReward:F4}",
            debugProgressRewardSplits
        );

        if (reciprocalReward > 0f)
        {
            episodeReciprocalProgressRewardEvents++;
            RecordStat("AntArena/ReciprocalProgressRewardEvents", 1f, StatAggregationMethod.Sum);
            RecordStat("AntArena/ReciprocalProgressSupportChainLength", supportChain.Count, StatAggregationMethod.Average);
        }
    }

    private void RefreshRewardMode()
    {
        float configuredMode = Academy.Instance.EnvironmentParameters.GetWithDefault(
            RewardModeParameterName,
            (float)rewardMode
        );

        int modeIndex = Mathf.Clamp(Mathf.RoundToInt(configuredMode), 0, 3);
        activeRewardMode = (RewardMode)modeIndex;
    }

    private bool UsesTeamRewards()
    {
        return activeRewardMode == RewardMode.Team ||
               activeRewardMode == RewardMode.TeamAndReciprocal;
    }

    private bool UsesReciprocalRewards()
    {
        return activeRewardMode == RewardMode.Reciprocal ||
               activeRewardMode == RewardMode.TeamAndReciprocal;
    }

    private int CountTeamRewardRecipients(AntAgent winner)
    {
        if (ants == null)
        {
            return 0;
        }

        int teamRecipientCount = 0;
        foreach (AntAgent ant in ants)
        {
            if (ant != null && ant != winner)
            {
                teamRecipientCount++;
            }
        }

        return teamRecipientCount;
    }

    private float AddTeamFoodReward(AntAgent winner, float teamReward)
    {
        if (ants == null || ants.Count == 0 || teamReward <= 0f)
        {
            return 0f;
        }

        int teamRecipientCount = CountTeamRewardRecipients(winner);

        if (teamRecipientCount == 0)
        {
            return AddTrackedReward(winner, teamReward);
        }

        float rewardPerAnt = teamReward / teamRecipientCount;
        foreach (AntAgent ant in ants)
        {
            if (ant != null && ant != winner)
            {
                AddTrackedReward(ant, rewardPerAnt);
            }
        }

        episodeTeamRewardEvents++;
        RecordStat("AntArena/TeamRewardEvents", 1f, StatAggregationMethod.Sum);
        RecordStat("AntArena/TeamRewardValue", teamReward, StatAggregationMethod.Sum);
        return teamReward;
    }

    private bool TryAddReciprocalSupportReward(List<AntAgent> supportChain, float totalReward, string valueStatName)
    {
        if (supportChain == null || supportChain.Count == 0 || totalReward <= 0f)
        {
            return false;
        }

        float rewardPerSupporter = totalReward / supportChain.Count;
        foreach (AntAgent supporter in supportChain)
        {
            AddTrackedReward(supporter, rewardPerSupporter);
            supporter.ShowSupportRewardGlow();
        }

        RecordStat(valueStatName, totalReward, StatAggregationMethod.Sum);
        return true;
    }

    private float AddTrackedReward(AntAgent ant, float reward)
    {
        if (ant == null || reward == 0f)
        {
            return 0f;
        }

        ant.AddReward(reward);
        return reward;
    }

    private void CheckRewardBudget(
        string rewardType,
        string agentName,
        float expectedReward,
        float actualReward,
        string splitSummary,
        bool logSplit
    )
    {
        float error = Mathf.Abs(expectedReward - actualReward);

        if (rewardType == "Food")
        {
            lastFoodRewardExpected = expectedReward;
            lastFoodRewardActual = actualReward;
            lastFoodRewardError = error;
            lastFoodRewardSplit = splitSummary;
        }
        else
        {
            lastProgressRewardExpected = expectedReward;
            lastProgressRewardActual = actualReward;
            lastProgressRewardError = error;
            lastProgressRewardSplit = splitSummary;
        }

        if (recordRewardDebugStats)
        {
            RecordStat($"AntArena/RewardDebug/{rewardType}Expected", expectedReward, StatAggregationMethod.MostRecent);
            RecordStat($"AntArena/RewardDebug/{rewardType}Actual", actualReward, StatAggregationMethod.MostRecent);
            RecordStat($"AntArena/RewardDebug/{rewardType}AbsError", error, StatAggregationMethod.MostRecent);
            RecordStat(
                $"AntArena/RewardDebug/{rewardType}Mismatch",
                error > rewardBudgetTolerance ? 1f : 0f,
                StatAggregationMethod.Sum
            );
        }

        if (error > rewardBudgetTolerance)
        {
            episodeRewardBudgetMismatches++;
            Debug.LogWarning(
                $"{name} {rewardType} reward budget mismatch for {agentName}: " +
                $"mode={activeRewardMode}, expected={expectedReward:F6}, actual={actualReward:F6}, " +
                $"error={error:F6}, split=({splitSummary})"
            );
            return;
        }

        if (logSplit)
        {
            Debug.Log(
                $"{name} {rewardType} reward split for {agentName}: " +
                $"mode={activeRewardMode}, expected={expectedReward:F6}, actual={actualReward:F6}, " +
                $"split=({splitSummary})"
            );
        }
    }

    private List<AntAgent> GetSupportChain(AntAgent dependentAnt)
    {
        supportChainBuffer.Clear();
        supportVisited.Clear();
        supportSearchStack.Clear();

        if (dependentAnt == null)
        {
            return supportChainBuffer;
        }

        supportVisited.Add(dependentAnt);
        supportSearchStack.Push(dependentAnt);

        while (supportSearchStack.Count > 0)
        {
            AntAgent currentDependent = supportSearchStack.Pop();
            currentDependent.GetTouchingAnts(touchingAntBuffer);

            foreach (AntAgent candidateSupporter in touchingAntBuffer)
            {
                if (candidateSupporter == null ||
                    supportVisited.Contains(candidateSupporter) ||
                    !IsLowerSupporter(candidateSupporter, currentDependent))
                {
                    continue;
                }

                supportVisited.Add(candidateSupporter);
                supportChainBuffer.Add(candidateSupporter);
                supportSearchStack.Push(candidateSupporter);
            }
        }

        return supportChainBuffer;
    }

    private bool IsLowerSupporter(AntAgent candidateSupporter, AntAgent dependentAnt)
    {
        float supporterY = GetEnvironmentLocalPosition(candidateSupporter.transform).y;
        float dependentY = GetEnvironmentLocalPosition(dependentAnt.transform).y;
        return supporterY <= dependentY - supportVerticalMargin;
    }

    private void RecordFoodSupportStats(int supportChainCount)
    {
        RecordStat("AntArena/FoodSupportChainLength", supportChainCount, StatAggregationMethod.Average);
        RecordStat("AntArena/FoodReachedWithSupport", supportChainCount > 0 ? 1f : 0f, StatAggregationMethod.Average);
    }

    private void RecordStat(string statName, float value, StatAggregationMethod aggregationMethod)
    {
        if (statsRecorder != null)
        {
            statsRecorder.Add(statName, value, aggregationMethod);
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

        Rect boxRect = new Rect(10f, 10f, 420f, 348f);
        Color previousColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.85f);
        GUI.DrawTexture(boxRect, Texture2D.whiteTexture);
        GUI.color = previousColor;

        GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
        labelStyle.normal.textColor = Color.white;
        labelStyle.fontSize = 14;

        GUILayout.BeginArea(new Rect(22f, 20f, 396f, 330f));
        GUILayout.Label($"Episode: {episodeNumber}", labelStyle);
        GUILayout.Label($"Episode Steps: {episodeStepCount} / {maxEnvironmentSteps}", labelStyle);
        GUILayout.Label($"Total Steps: {totalStepCount}", labelStyle);
        GUILayout.Label($"Reward Mode: {(int)activeRewardMode} ({activeRewardMode})", labelStyle);
        GUILayout.Label($"Food Reward: {foodReward:F3}", labelStyle);
        GUILayout.Label($"Ants: {ants.Count}", labelStyle);
        GUILayout.Label($"Food Successes: {totalFoodSuccesses}", labelStyle);
        GUILayout.Label($"Team Successes: {totalTeamSuccesses}", labelStyle);
        GUILayout.Label($"Unique Ants Reached: {uniqueAntsThatReached.Count} / {reachedNumberToWin}", labelStyle);
        GUILayout.Label($"Team Reward Events: {episodeTeamRewardEvents}", labelStyle);
        GUILayout.Label($"Reciprocal Success Events: {episodeReciprocalSuccessRewardEvents}", labelStyle);
        GUILayout.Label($"Reciprocal Progress Events: {episodeReciprocalProgressRewardEvents}", labelStyle);
        GUILayout.Label($"Reward Budget Mismatches: {episodeRewardBudgetMismatches}", labelStyle);
        GUILayout.Label($"Food Budget: {lastFoodRewardActual:F3} / {lastFoodRewardExpected:F3} err {lastFoodRewardError:F5}", labelStyle);
        GUILayout.Label($"Food Split: {lastFoodRewardSplit}", labelStyle);
        GUILayout.Label($"Progress Budget: {lastProgressRewardActual:F4} / {lastProgressRewardExpected:F4} err {lastProgressRewardError:F5}", labelStyle);
        GUILayout.Label($"Progress Split: {lastProgressRewardSplit}", labelStyle);
        GUILayout.EndArea();
    }
}
