using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents;

public class AntArenaController : MonoBehaviour
{
    [Header("References")]
    public GameObject food;
    public List<AntAgent> ants = new List<AntAgent>();
    private int reached = 0;
    private bool success = false;
    private bool successAdded = false;
    public int reachedNumberToWin = 3;
    private bool teamSuccess = false;

    [Header("Ant Spawn")]
    public float groundY = -4f;
    public float minSpawnX = -4f;
    public float maxSpawnX = 4f;
    public float minAntSpacing = 1.2f;
    public int maxSpawnAttemptsPerAnt = 50;
    StatsRecorder statsRecorder;
    public enum RewardMode
    {
        Individual,
        Cooperative
    }

    [Header("Experiment")]
    public RewardMode rewardMode = RewardMode.Individual;
    public int maxEnvironmentSteps = 1000;

    private int episodeStepCount = 0;
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

        if (maxEnvironmentSteps > 0 && episodeStepCount >= maxEnvironmentSteps)
        {
            timedOut = true;
            EndArenaEpisode(false);
        }
    }

    public void Awake()
    {
        Academy.Instance.OnEnvironmentReset += EnvironmentReset;
        statsRecorder = Academy.Instance.StatsRecorder;

        foreach (AntAgent ant in ants)
        {
            ant.Setup(this, food.transform);
        }
    }

    public void EnvironmentReset()
    {
        success = false;
        successAdded = false;
        episodeStepCount = 0;
        episodeEnded = false;
        timedOut = false;

        if (ants == null || ants.Count == 0) return;

        List<Vector2> placedPositions = new List<Vector2>();

        ResetFoodPosition();

        foreach (AntAgent ant in ants)
        {
            Vector2 spawnPos = GetRandomGroundSpawnPosition(placedPositions);
            ResetAntPosition(ant, spawnPos);
            placedPositions.Add(spawnPos);
        }

        reached = 0;
    }

    private void ResetFoodPosition() {
        food.transform.position = new Vector2(Random.Range(minSpawnX, maxSpawnX), food.transform.position.y);
    }

    public void ResetAntPosition(AntAgent ant, Vector2 spawnPos)
    {
        ant.transform.position = spawnPos;
        ant.transform.rotation = Quaternion.identity;
        ant.ResetAgentState();
    }

    private Vector2 GetRandomGroundSpawnPosition(List<Vector2> placedPositions)
    {
        for (int attempt = 0; attempt < maxSpawnAttemptsPerAnt; attempt++)
        {
            float randomX = Random.Range(minSpawnX, maxSpawnX);
            Vector2 candidate = new Vector2(randomX, groundY);

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
        return new Vector2(Random.Range(minSpawnX, maxSpawnX), groundY);
    }

    public void NotifyFoodReached(AntAgent winner)
    {
        if (episodeEnded)
        {
            return;
        }

        Vector2 newSpawn = GetRandomGroundSpawnPosition(GetAllOtherAntPositions(winner));
        ResetAntPosition(winner, newSpawn);
        success = true;

        reached++;

        if (reached >= reachedNumberToWin) {

            EndArenaEpisode(true);

        }

    }

    private void EndArenaEpisode(bool won)
    {
        if (episodeEnded)
        {
            return;
        }

        episodeEnded = true;
        teamSuccess = won;

        statsRecorder.Add(
            "AntArena/TimeoutRate",
            timedOut ? 1f : 0f,
            StatAggregationMethod.Average
        );

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
                positions.Add(ant.transform.position);
            }
        }

        return positions;
    }
}
