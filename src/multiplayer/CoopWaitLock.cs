using UnityEngine;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;

// Used on Unity's main thread. The current build's game-clock owners use
// bits through 14. MiMovement uses bits 0..30; its native stop/resume ORs
// and AND-NOTs the supplied mask, including bit 31.
internal sealed class CoopWaitLock
{
    private const int WorldReason = 1 << 30;
    private const int CrewBit = unchecked((int)0x80000000);
    private const MiMovement.StopMovementReason CrewReason = (MiMovement.StopMovementReason)CrewBit;
    // MiGameTime.setPause mirrors its aggregate state into MiTime at bit 1.
    private const int GameClockProxy = 2;
    private MiGameTime worldOwner;
    private MiMovement crewOwner;
    private static bool saveFiltersReady;

    private static readonly List<WeakReference<CoopWaitLock>> locks=new List<WeakReference<CoopWaitLock>>();
    private static int savingFrame=-1;
    internal CoopWaitLock() { locks.Add(new WeakReference<CoopWaitLock>(this)); }
    private static bool Saving => savingFrame==Time.frameCount || SaveProcessManager.bSaving;

    internal static bool CanHoldTransient => saveFiltersReady && !Saving;

    internal static void InstallSaveFilters()
    {
        if(saveFiltersReady) return;
        try
        {
            new Harmony("local.shadowgambit.wait.saveentry").PatchAll(typeof(BeforeSave));
            saveFiltersReady=true;
        }
        catch(Exception ex)
        { MoveProbePlugin.ProbeLog.LogWarning("Network wait disabled; save entry hook unavailable: "+ex.GetType().Name); }
    }

    // Do not patch serializeMi(ref Dictionary<...>): the IL2CPP trampoline
    // emitted for those signatures fails at runtime in the shipping build.
    // Release only our transient locks before serialization starts, and keep
    // them released until the native save operation has finished.
    [HarmonyPatch]
    internal static class BeforeSave
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SaveProcessManager),"trySaveScene");
            yield return AccessTools.Method(typeof(SaveProcessManager),"createSceneSaveDateAndStoreToMember");
        }
        private static void Prefix(SaveProcessManager __instance)
        {
            savingFrame=Time.frameCount;
            GuardAuthority.BeforeSave();
            for(int i=locks.Count-1;i>=0;i--)
                if(locks[i].TryGetTarget(out var item)) item.Release(); else locks.RemoveAt(i);
        }
    }

    private static MiGameTime CurrentClock() =>
        MiGameTime.bInstanceExists ? MiGameTime.instance : null;

    internal bool WorldHeld => worldOwner != null && worldOwner == CurrentClock() &&
        (worldOwner.m_iPauseLocks & WorldReason) != 0;
    internal bool CrewHeld => crewOwner != null && (crewOwner.m_iStopMovementReason & CrewBit) != 0;

    internal bool ExternalPause
    {
        get
        {
            var clock = CurrentClock();
            int gameLocks = clock != null ? clock.m_iPauseLocks : 0;
            bool mine = (gameLocks & WorldReason) != 0;
            int globalLocks = MiTime.s_iPauseLocks;
            if (mine) globalLocks &= ~GameClockProxy;
            return (gameLocks & ~WorldReason) != 0 || globalLocks != 0 ||
                (!mine && Time.timeScale <= 0f);
        }
    }

    internal bool SetWorld(bool hold)
    {
        // Target only the active singleton: calling setPause on a previous
        // scene's clock would update the global proxy for the wrong world.
        var clock = CurrentClock();
        hold &= saveFiltersReady && !Saving;
        worldOwner = hold ? clock : null;
        if (clock == null) return false;
        bool hasReason = (clock.m_iPauseLocks & WorldReason) != 0;
        if (hasReason != hold) clock.setPause(hold, WorldReason);
        return hold && (clock.m_iPauseLocks & WorldReason) != 0;
    }

    internal bool SetCrew(MiCharacter crew, bool hold)
    {
        var move = crew != null ? crew.movement : null;
        if (crewOwner != null && crewOwner != move) FreeCrew(crewOwner);
        crewOwner = null;
        if (move == null) return false;
        if (!saveFiltersReady || Saving || !hold || !Walking(crew))
        {
            FreeCrew(move);
            return false;
        }
        if ((move.m_iStopMovementReason & CrewBit) == 0) move.stop(CrewReason, false);
        crewOwner = move;
        return CrewHeld;
    }

    private static bool Walking(MiCharacter crew)
    {
        if (crew.health == null || crew.health.eHealthState != MiCharacterHealth.HealthState.Alive ||
            crew.controllerPlayer == null || crew.controller == null) return false;
        var processes = crew.processes;
        if (processes != null && (processes.bIsExecuting(MiCharacterProcess.Type.Kill) ||
            processes.bIsExecuting(MiCharacterProcess.Type.Knockout))) return false;
        var active = crew.controller.skillCommandProcess?.m_skill;
        var pending = crew.controllerPlayer.skillCommand?.m_skill;
        if (active != null && active.eSkillType != Skill.SkillType.GoTo) return false;
        if (pending != null && pending.eSkillType != Skill.SkillType.GoTo) return false;
        return active != null || pending != null;
    }

    private static void FreeCrew(MiMovement move)
    {
        if (move != null && (move.m_iStopMovementReason & CrewBit) != 0) move.resume(CrewReason);
    }

    internal void Release()
    {
        try { SetCrew(null, false); }
        finally { SetWorld(false); }
    }
    internal void ReleaseCrew()=>SetCrew(null,false);
}
