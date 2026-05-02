using NUnit.Framework;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEngine;

public class AntAgentConfigTests
{
    private const string AntPrefabPath = "Assets/Prefabs/Dynamic Sprite.prefab";

    [Test]
    public void AntPrefabUsesAgentLayerForNearbyQueries()
    {
        GameObject prefab = LoadAntPrefab();
        AntAgent ant = prefab.GetComponent<AntAgent>();
        int agentLayer = LayerMask.NameToLayer("Agent");

        Assert.AreNotEqual(-1, agentLayer);
        Assert.AreEqual(agentLayer, prefab.layer);
        Assert.AreEqual(1 << agentLayer, ant.antLayer.value);
    }

    [Test]
    public void BehaviorParametersMatchAgentCode()
    {
        GameObject prefab = LoadAntPrefab();
        BehaviorParameters behavior = prefab.GetComponent<BehaviorParameters>();

        Assert.NotNull(behavior);
        Assert.AreEqual(9, behavior.BrainParameters.VectorObservationSize);
        Assert.AreEqual(1, behavior.BrainParameters.ActionSpec.NumContinuousActions);
        Assert.AreEqual(1, behavior.BrainParameters.ActionSpec.NumDiscreteActions);
        Assert.AreEqual(2, behavior.BrainParameters.ActionSpec.BranchSizes[0]);
    }

    [Test]
    public void RewardShapingMakesDirectProgressPositive()
    {
        AntAgent ant = LoadAntPrefab().GetComponent<AntAgent>();
        float directProgressReward = ant.maxSpeed * Time.fixedDeltaTime * ant.progressRewardScale;
        float netRewardForDirectProgress = directProgressReward + ant.stepPenalty;

        Assert.Less(ant.stepPenalty, 0f);
        Assert.Greater(netRewardForDirectProgress, 0f);
    }

    [Test]
    public void ResetAgentStateClearsFrozenPhysics()
    {
        GameObject instance = Object.Instantiate(LoadAntPrefab());
        GameObject food = new GameObject("Test Food");

        try
        {
            AntAgent ant = instance.GetComponent<AntAgent>();
            Rigidbody2D rb = instance.GetComponent<Rigidbody2D>();
            food.transform.position = Vector3.right;

            ant.Setup(null, food.transform);
            rb.constraints = RigidbodyConstraints2D.FreezePosition | RigidbodyConstraints2D.FreezeRotation;
            rb.linearVelocity = Vector2.one;
            rb.angularVelocity = 10f;

            ant.ResetAgentState();

            Assert.AreEqual(RigidbodyConstraints2D.FreezeRotation, rb.constraints);
            Assert.AreEqual(0f, rb.linearVelocity.sqrMagnitude);
            Assert.AreEqual(0f, rb.angularVelocity);
            Assert.False(ant.IsTouchingAnything);
            Assert.False(ant.IsGrounded);
            Assert.False(ant.IsTouchingWall);
            Assert.False(ant.IsTouchingAnt);
            Assert.False(ant.IsClimbing);
            Assert.AreEqual(0, ant.AntContactCount);
        }
        finally
        {
            Object.DestroyImmediate(instance);
            Object.DestroyImmediate(food);
        }
    }

    [Test]
    public void NearbyAntCountDetectsOtherAgentsOnAgentLayer()
    {
        GameObject first = Object.Instantiate(LoadAntPrefab());
        GameObject second = Object.Instantiate(LoadAntPrefab());

        try
        {
            AntAgent firstAnt = first.GetComponent<AntAgent>();
            int agentLayer = LayerMask.NameToLayer("Agent");

            first.layer = agentLayer;
            second.layer = agentLayer;
            first.transform.position = Vector3.zero;
            second.transform.position = Vector3.right * (firstAnt.neighborRadius * 0.5f);
            Physics2D.SyncTransforms();

            Assert.AreEqual(1, firstAnt.GetNearbyAntCount());
        }
        finally
        {
            Object.DestroyImmediate(first);
            Object.DestroyImmediate(second);
        }
    }

    private static GameObject LoadAntPrefab()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AntPrefabPath);
        Assert.NotNull(prefab, $"Could not load ant prefab at {AntPrefabPath}");
        return prefab;
    }
}
