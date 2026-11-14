using System;
using System.Collections.Generic;
using UnityEngine;

// Per-instance native decision leases. Do not hook virtual command setters:
// removing those hooks restored startup after the 0.8.19 native crash.
internal sealed class GuardAuthority
{
    // Shipping AIBlockReason uses priorities 0..4, RoutineBlockedReason bits 0..6.
    // Remove only our priority/bit, never clear the game's existing owners.
    private const int Owner = 1 << 28;
    private const AIHandler.RoutineBlockedReason Reason = (AIHandler.RoutineBlockedReason)Owner;
    private sealed class Lease
    {
        internal MiCharacter actor;
        internal AIHandler ai;
        internal PriorityLockBool decision;
        internal bool footprintCleared;
        internal float expires;
    }
    private readonly Dictionary<string,Lease> leases = new Dictionary<string,Lease>();
    private readonly List<string> expired = new List<string>();
    private static readonly List<WeakReference<GuardAuthority>> instances = new List<WeakReference<GuardAuthority>>();
    private static bool nativeReady;
    internal GuardAuthority() { instances.Add(new WeakReference<GuardAuthority>(this)); }
    internal static bool Available => nativeReady && CoopWaitLock.CanHoldTransient && !MiCutsceneHandler.bCutsceneRunning;
    internal static void VerifyNativeLocks()
    {
        nativeReady=false;
        try
        {
            // Exercise the shipping native generic methods on a detached lock,
            // without touching a character or save. Fail closed if unavailable.
            // The empty IL2CPP Nullable<Vector3> constructor crashed target
            // replication in 0.8.22. Exercise the retained value constructor.
            var targetPosition=new Il2CppSystem.Nullable<Vector3>(new Vector3());
            if(!targetPosition.HasValue) throw new InvalidOperationException("Native target position unavailable");
            var test=new PriorityLockBool();
            test.set(false,0,"baseline",false);
            test.set(true,Owner,"Coop probe",false);
            if(!test.currentValue) throw new InvalidOperationException("Decision owner not effective");
            test.release(Owner);
            if(test.currentValue) throw new InvalidOperationException("Decision owner not released");
            test.set(true,2,"native skill",false);
            test.set(true,Owner,"Coop probe",false);
            test.release(Owner);
            if(!test.currentValue) throw new InvalidOperationException("Native owner overwritten");
            test.release(2); test.release(0);
            nativeReady=true;
            MoveProbePlugin.ProbeLog.LogInfo("Guard authority native lock verification passed.");
        }
        catch(Exception ex) { MoveProbePlugin.ProbeLog.LogWarning("Guard authority unavailable: "+ex); }
    }
    internal bool Owns(string id,MiCharacter actor) => leases.TryGetValue(id,out var lease) && actor!=null &&
        lease.actor!=null && lease.actor.Pointer==actor.Pointer && actor.bControllerAI() &&
        actor.controllerAI?.m_ai?.Pointer==lease.ai.Pointer;

    internal bool Acquire(string id, MiCharacter actor)
    {
        if (!Available || actor == null || !actor.bControllerAI() || actor.health?.eHealthState != MiCharacterHealth.HealthState.Alive)
        { Release(id); return false; }
        var ai=actor.controllerAI?.m_ai;
        var decision=ai?.m_varLockAIBlocked;
        if (decision==null) { Release(id); return false; }
        if (leases.TryGetValue(id,out var old) && (old.actor.Pointer!=actor.Pointer || old.ai.Pointer!=ai.Pointer || old.decision.Pointer!=decision.Pointer)) Release(id);
        if (!leases.TryGetValue(id,out var lease))
        {
            lease=new Lease {actor=actor, ai=ai, decision=decision};
            leases[id]=lease; // Register before native calls so partial acquisition can be released.
            MoveProbePlugin.ProbeLog.LogInfo("Guard authority acquired id="+id);
        }
        try
        {
            // AI decisions are blocked, not movement, animations, or incoming host commands.
            // Reassert after native clearAIBlock/reinitialization without adding duplicate owners.
            decision.set(true,Owner,"Coop host decisions",false);
            ai.setRoutineBlocked(true,Reason);
            ai.setDetectionReactionSkillBlocked(true,Reason);
            // A trail may already have been cached before this guard's first
            // authoritative page. Do not let that stale local target survive.
            if(!lease.footprintCleared && actor.controllerAI?.m_detection!=null)
            { actor.controllerAI.m_detection.clearFootprint(); lease.footprintCleared=true; }
            lease.expires=Time.realtimeSinceStartup+2f;
            if (!ai.bAIIsBlocked()) { Release(id); return false; }
            return true;
        }
        catch { Release(id); throw; }
    }

    internal void Tick()
    {
        if (!Available) { Release(); return; }
        expired.Clear();
        foreach(var pair in leases)
            if (Time.realtimeSinceStartup>pair.Value.expires || !Owns(pair.Key,pair.Value.actor) ||
                pair.Value.actor.health?.eHealthState!=MiCharacterHealth.HealthState.Alive)
                expired.Add(pair.Key);
        foreach(var id in expired) Release(id);
    }
    internal void Release(string id)
    {
        if (!leases.TryGetValue(id,out var lease)) return;
        leases.Remove(id);
        try
        {
            try { if(lease.decision!=null) lease.decision.release(Owner); }
            finally
            {
                if(lease.ai!=null)
                {
                    try { lease.ai.setRoutineBlocked(false,Reason); }
                    finally { lease.ai.setDetectionReactionSkillBlocked(false,Reason); }
                }
            }
        }
        catch(Exception ex) { MoveProbePlugin.ProbeLog.LogWarning("Guard authority release id="+id+": "+ex.GetType().Name); }
    }
    internal void Release()
    {
        foreach(var id in new List<string>(leases.Keys)) Release(id);
    }
    internal static void BeforeSave()
    {
        for(int i=instances.Count-1;i>=0;i--)
            if(instances[i].TryGetTarget(out var authority)) authority.Release(); else instances.RemoveAt(i);
    }
}
