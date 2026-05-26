using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public static class AIMotor
{
    private static readonly HashSet<int> RootMotionSuppressedHosts = new HashSet<int>();

    public static void ApplyBlackPlayerLocomotion(NavMeshAgent agent, EnemyController host)
    {
        if (agent == null || host == null)
            return;

        PlayerController player = EnemyController.ResolveScenePlayer();
        if (player != null)
        {
            agent.speed = Mathf.Max(0.1f, player.moveSpeed * player.sprintMultiplier);
            agent.acceleration = Mathf.Max(0.1f, player.acceleration);
            agent.angularSpeed = Mathf.Max(30f, player.turnSpeed);
            host.rotationSpeed = Mathf.Max(8f, player.turnSpeed / 72f);
            return;
        }

        agent.speed = Mathf.Max(0.1f, host.sprintChaseSpeed);
        agent.acceleration = Mathf.Max(0.1f, host.agentAcceleration);
        agent.angularSpeed = Mathf.Max(30f, host.agentAngularSpeed);
    }

    public static void SuppressRootMotion(EnemyController host)
    {
        if (host == null)
            return;

        int hostId = host.GetInstanceID();
        if (RootMotionSuppressedHosts.Contains(hostId))
            return;

        Animator[] animators = host.GetComponentsInChildren<Animator>(true);
        for (int i = 0; i < animators.Length; i++)
        {
            if (animators[i] != null)
                animators[i].applyRootMotion = false;
        }
        RootMotionSuppressedHosts.Add(hostId);
    }

    public static void EnforceSprintPursuit(EnemyController host)
    {
        if (host == null || !host.PrepareAgentForCombatLocomotion())
            return;

        Transform target = host.CurrentTarget;
        if (target == null)
            return;

        NavMeshAgent agent = host.Agent;
        agent.isStopped = false;
        agent.updatePosition = true;
        agent.updateRotation = false;
        agent.autoBraking = false;
        agent.stoppingDistance = EnemyController.CombatStoppingDistance;
        ApplyBlackPlayerLocomotion(agent, host);
        SuppressRootMotion(host);
        Vector3 targetPosition = target.position;
        if (!agent.pathPending &&
            (!agent.hasPath || (agent.destination - targetPosition).sqrMagnitude > 1f))
        {
            agent.SetDestination(targetPosition);
        }
    }

    public static void DrivePursuit(EnemyController host, float stoppingDistance)
    {
        EnforceSprintPursuit(host);
        if (host.IsAgentReady())
            host.Agent.stoppingDistance = stoppingDistance;
    }

    public static void ForceAggressivePath(EnemyController host)
    {
        EnforceSprintPursuit(host);
    }
}
