using System;
using HarmonyLib;
// Explicit allow-list: never PatchAll the whole plugin. It also contains
// diagnostic and optional native detours which must not run in normal play.
internal static class NativeSkillHooks
{
    internal static readonly Type[] Patches={
        typeof(GuestWorldMap.Play),
        typeof(ControlReleaseInput.Cancel),typeof(ControlReleaseInput.OvertakeCancel),
        typeof(PlanningSync.Edit),typeof(PlanningSync.ExecuteOne),typeof(PlanningSync.ExecuteScope),typeof(PlanningSync.RemoveLast),typeof(PlanningSync.RemoveAll),typeof(TrainingSession.Enter),typeof(TrainingSession.Follow),typeof(TrainingSession.External),typeof(TrainingSession.Load),typeof(TrainingDeckTrace.Entry),typeof(TrainingDeckTrace.Mission),typeof(TrainingDeckTrace.Finished),typeof(TrainingDeckTrace.Return),
        typeof(StoryObservation.MissionUse),typeof(StoryObservation.ShipRevival),
        typeof(StoryObservation.PlanningEnter),typeof(StoryObservation.PlanningExit),typeof(StoryObservation.PlanningExecute),
        typeof(GameSpeedSync.AlarmClockHook),
        typeof(GameSpeedSync.FastForwardAllowed),typeof(GameSpeedSync.FastForwardInput),
        typeof(StoryObservation.Usable),typeof(StoryObservation.HideExit),typeof(StoryObservation.ViewEnd),
        typeof(StoryObservation.NativeStart),
        typeof(StoryObservation.Dialog),
        typeof(GuestMissionPreparation.Landing),typeof(GuestMissionPreparation.Complete),
        typeof(GuestLoadMenuGuard.Continue),typeof(GuestLoadMenuGuard.Load),
        typeof(SkillLifecycleTrace.CrystalValid),typeof(SkillLifecycleTrace.CrystalStatic),typeof(SkillLifecycleTrace.CrystalDynamic),typeof(SkillLifecycleTrace.BaitRecall),typeof(SkillLifecycleTrace.GhostRecall),typeof(SkillLifecycleTrace.GhostRecallInput),typeof(SkillLifecycleTrace.BaitSelect),typeof(SkillLifecycleTrace.GunStart),typeof(SkillLifecycleTrace.TeleportCommand),typeof(SkillLifecycleTrace.TeleportExecute),typeof(SkillLifecycleTrace.CannonCreate),typeof(SkillLifecycleTrace.CannonHit),typeof(SkillLifecycleTrace.ProcessStart),
        typeof(MimicChoice.ChoiceHook),typeof(MimicChoice.WheelOpened),typeof(GameSpeedSync.ClockHook),typeof(StoryObservation.Script),typeof(StoryObservation.Timeline),typeof(StoryObservation.Clock),typeof(StoryObservation.View),
        typeof(SkillResourceSync.StartTimed),typeof(SkillResourceSync.StartDefault),typeof(SkillResourceSync.ConfirmedGunAmmo),typeof(SkillResourceSync.ConfirmedItemActivation),typeof(SkillResourceSync.ConfirmedItemAvailable),
        typeof(GuestSkillPreview.StartHook),typeof(GuestSkillPreview.StopHook),typeof(GuestSkillPreview.RangeHook),
        typeof(EffectProcessClock.TimerHook),typeof(TimeFreezeSync.StartHook),
        typeof(BlindSync.StartHook),typeof(BlindSync.StopDurationHook),
        typeof(FrozenSync.StartHook),typeof(FrozenProcessClock.TimerHook),typeof(FrozenProcessClock.FollowUpHook),
        typeof(FrozenDuelSync.PhaseHook),typeof(FrozenDuelSync.StartHook),
        typeof(DigStateSync.AddHook),typeof(DigStateSync.RemoveHook),typeof(DigStateSync.HideHook),typeof(DigStateSync.RevealHook),
        typeof(CannonStateSync.StateHook),typeof(CannonStateSync.LoadHook),typeof(CannonStateSync.EmptyHook),
        typeof(CannonLaunchSync.ThrowHook),typeof(CannonStateSync.StunHook),
        typeof(BaitObjectSync.ThrowHook),
        typeof(CannonFlightSync.CreateHook),typeof(CannonFlightSync.ClockHook),typeof(CannonFlightSync.HitHook),typeof(CannonFlightSync.SkillHitHook),
        typeof(FishingPullSync.CharacterHook),typeof(FishingPullSync.ClockHook),typeof(FishingItemSync.ItemHook),typeof(FishingItemSync.PossessableHook),typeof(FishingItemSync.StopHook)
    };
    private static bool ready;
    internal static bool Install()
    {
        if(ready)return true;
        var harmony=new Harmony("local.shadowgambit.skillstate");
        Type installing=null;
        try
        {
            foreach(var patch in Patches){installing=patch;harmony.PatchAll(patch);}
            ready=true;MoveProbePlugin.ProbeLog.LogInfo("Native skill state hooks installed: "+Patches.Length);return true;
        }
        catch(Exception ex)
        {
            // A half-installed timer/authority pair can freeze an effect or
            // run its damage twice. Roll back this owner and fail co-op startup.
            try{harmony.UnpatchSelf();}catch(Exception rollback){MoveProbePlugin.ProbeLog.LogError("Skill hook rollback failed: "+rollback);}
            MoveProbePlugin.ProbeLog.LogError("Co-op skill hooks unavailable at "+installing?.FullName+": "+ex);
            return false;
        }
    }
}
