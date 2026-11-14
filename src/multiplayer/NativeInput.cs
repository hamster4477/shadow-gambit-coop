using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Guest ground and melee orders wait for the host. Snapshot playback is exempt.
internal static class NativeInput
{
    internal static MoveProbe Probe;
    [ThreadStatic] internal static bool Restoring;
    [ThreadStatic] internal static bool PlanningExecution;
    [ThreadStatic] private static HashSet<IntPtr> inputCrew;
    [ThreadStatic] private static int inputFrame;
    internal static bool InputScopeReady;
    internal static bool InPlayerInput(MiCharacter actor)=>actor!=null && inputFrame==Time.frameCount && inputCrew?.Contains(actor.Pointer)==true;
    internal static bool PlayerGesture(MiCharacter actor)=>PlanningExecution||InMouseCommand(actor)||InPlayerInput(actor)||!InputScopeReady&&(MouseEdge()||Input.anyKeyDown);
    internal static bool AutomaticCommand(MiCharacter actor)=>!PlanningExecution&&InputScopeReady&&!InMouseCommand(actor)&&!InPlayerInput(actor);
    internal static void EndGame(){teleportSubmitted.Clear();MimicChoice.Clear();Probe=null;mouseActor=null;mouseCrew=null;inputCrew=null;Restoring=false;}
    [HarmonyPatch]
    internal static class PlayerUpdate
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MiPlayerInput),"updateInput");
            yield return AccessTools.Method(typeof(MiPlayerInput),"updateCharacterInput");
        }
        private static void Prefix(MiPlayerInput __instance,out HashSet<IntPtr> __state)
        {
            __state=inputCrew;inputCrew=new HashSet<IntPtr>();inputFrame=Time.frameCount;
            if(Probe==null)return;
            Probe.ObserveGuestInput(__instance);
            var selected=GuestCrewSelection.Members(__instance);
            if(selected!=null)for(int i=0;i<selected.Count;i++)if(selected[i]!=null)inputCrew.Add(selected[i].Pointer);
        }
        private static Exception Finalizer(Exception __exception,HashSet<IntPtr> __state)
        {inputCrew=__state;return __exception;}
    }

    // UI selection can dispatch an instant ability outside MiPlayerInput's
    // update scope. Give that native callback the same ownership gate.
    [HarmonyPatch(typeof(PlayerSkill),"onSelectedFromUI")]
    internal static class UiSkillInput
    {
        private static void Prefix(PlayerSkill __instance,out HashSet<IntPtr> __state)
        {__state=inputCrew;inputCrew=new HashSet<IntPtr>();inputFrame=Time.frameCount;if(__instance?.charOrigin!=null)inputCrew.Add(__instance.charOrigin.Pointer);}
        private static Exception Finalizer(Exception __exception,HashSet<IntPtr> __state)
        {inputCrew=__state;return __exception;}
    }
    [HarmonyPatch(typeof(PlayerSkill),"executeByCall")]
    internal static class InstantSkillInput
    {
        private static bool Prefix(PlayerSkill __instance,SkillCommand __0,ref bool __result)
        {
            var actor=__instance?.charOrigin;
            if(Restoring||Probe==null||!Probe.GuestOrderOwner(actor)||
                __instance.eSkillType is not (Skill.SkillType.TeleportMove or Skill.SkillType.TeleportKill or Skill.SkillType.TeleportKnockout or Skill.SkillType.ControlCancel or Skill.SkillType.OvertakeCancelPaused))return true;
            // Confirmed replay owns a remembered command. A direct UI skill
            // call has no such identity and must go through the host first.
            if(InteractionSync.CommandSerial(__0)>0)return true;
            if(Probe.ListensTo(actor))Probe.CaptureCommand(actor,__0??new SkillCommand(__instance,null),true);
            __result=true;return false;
        }
    }
    private static readonly HashSet<IntPtr> teleportSubmitted=new();
    private static bool ConfirmedTeleport(SkillTeleportTeleport skill,SkillCommand command)
        =>command?.m_skill?.Pointer==skill.Pointer&&InteractionSync.CommandSerial(command)>0;
    [HarmonyPatch(typeof(SkillTeleportTeleport),"onCommand")]
    internal static class TeleportCommand
    {
        private static bool Prefix(SkillTeleportTeleport __instance,SkillCommand __0)
        {
            if(Probe==null||!Probe.GuestOrderOwner(__instance.charOrigin)||Restoring||ConfirmedTeleport(__instance,__0))return true;
            if(__0!=null&&teleportSubmitted.Add(__0.Pointer)&&Probe.ListensTo(__instance.charOrigin))
            {Probe.CaptureCommand(__instance.charOrigin,__0,true);MoveProbePlugin.ProbeLog.LogInfo("Teleport native command forwarded: "+__instance.eSkillType);}
            return false;
        }
    }
    [HarmonyPatch(typeof(SkillTeleportTeleport),"execute")]
    internal static class TeleportExecute
    {
        private static bool Prefix(SkillTeleportTeleport __instance)
        {
            var actor=__instance.charOrigin;
            if(Probe==null||!Probe.GuestOrderOwner(actor)||Restoring)return true;
            var controller=actor.controller;
            if(ConfirmedTeleport(__instance,controller.skillCommandProcess)||ConfirmedTeleport(__instance,controller.skillCommandActive)||ConfirmedTeleport(__instance,controller.skillCommand))return true;
            // Command publication must precede the native warp, consumption,
            // and marker teardown, even for paths bypassing the player setter.
            var command=controller.skillCommandProcess??controller.skillCommandActive??controller.skillCommand;
            if(command?.m_skill?.Pointer!=__instance.Pointer)command=new SkillCommand(__instance,null);
            if(teleportSubmitted.Add(command.Pointer)&&Probe.ListensTo(actor))Probe.CaptureCommand(actor,command,true);
            return false;
        }
    }
    internal static bool MouseEdge() => Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)
        || Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1);

    [ThreadStatic] private static MiCharacter mouseActor;
    [ThreadStatic] private static int mouseFrame;
    [ThreadStatic] private static HashSet<IntPtr> mouseCrew;
    internal static bool InMouseCommand(MiCharacter actor) => actor!=null && mouseFrame==Time.frameCount && (mouseActor==actor || mouseCrew?.Contains(actor.Pointer)==true);
    private static int guardedFacingReports;

    // Network correction waypoints are not player click destinations. This
    // hook is installed only when joining as guest and changes visuals only.
    [HarmonyPatch(typeof(MiCharacterControllerPlayer),"notifyMovementDestination")]
    internal static class SyncDestination
    {
        private static bool Prefix(MiCharacterControllerPlayer __instance,SkillCommand __0)=>
            Probe==null || !Probe.MirroredCrew(__instance.character) || !NativeGait.QuietDestination(__0);
    }

    [HarmonyPatch(typeof(SkillThrow), "execute")]
    internal static class PointExecuted
    {
        private static void Postfix(SkillThrow __instance)
        {
            try { NativeInput.Probe?.PlayerPointProcess(__instance.character,__instance.m_skillCommandThrow); }
            catch(Exception ex) { MoveProbePlugin.ProbeLog.LogWarning("Point execution observation: "+ex.GetType().Name); }
        }
    }

    [HarmonyPatch]
    internal static class PreviewCompleted
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlayerSkill),"postExecute");
            yield return AccessTools.Method(typeof(SkillMeleeBase),"postExecute");
            yield return AccessTools.Method(typeof(SkillMelee),"postExecute");
        }
        private static void Postfix(PlayerSkill __instance)
        {
            if(Probe==null)return;
            try { GuestPreview.Completed(__instance); }
            catch(Exception ex) { MoveProbePlugin.ProbeLog.LogWarning("Skill preview completion: "+ex.GetType().Name); }
        }
    }
    [HarmonyPatch(typeof(MiCharacterTargetPlayer), "updateAimingObjects")]
    internal static class RetiredAiming
    {
        private static void Prefix(MiCharacterTargetPlayer __instance,ref bool __0)
        { if(Probe!=null&&!GuestPreview.AllowAiming(__instance))__0=false; }
    }

    [HarmonyPatch(typeof(SkillMeleeBase),"contextNoiseRangeCreate")]
    internal static class MeleePreview
    {
        private static bool Prefix(SkillMeleeBase __instance,Entity __0)=>GuestPreview.Creating(__instance,__0);
    }

    // A ground click can also call cancelSkill directly during the mouse
    // update. Preserve the committed animation while LateUpdate sends retreat.
    private static bool LockedContactMouse(MiCharacter actor) => actor != null && InMouseCommand(actor) && MouseEdge() && Probe != null && Probe.GuestContactLocked(actor);

    [HarmonyPatch(typeof(MiCharacterControllerPlayer), "cancelSkill")]
    internal static class CommittedCancel
    {
        private static bool Prefix(MiCharacterControllerPlayer __instance) => Probe==null || !LockedContactMouse(__instance.character);
    }

    [HarmonyPatch(typeof(MiCharacterControllerPlayer), "checkReorientateMovementHidingCommand")]
    internal static class MouseGroundFacing
    {
        // Foliage has its own native movement reorientation path. The mirrored
        // B should only take that turn when applying the host's movement.
        private static bool Prefix(MiCharacterControllerPlayer __instance, SkillCommand __0)
        {
            if (Probe == null || !InMouseCommand(__instance.character) || !MouseEdge() ||
                !Probe.GuestOrderOwner(__instance.character) ||
                __0?.m_skill?.eSkillType != Skill.SkillType.GoTo || __0.m_target == null) return true;
            // Native GoTo may target a character or usable. Its reorientation
            // still belongs to host playback, just like a terrain GoTo.
            if (guardedFacingReports < 4)
            {
                guardedFacingReports++;
                MoveProbePlugin.ProbeLog.LogInfo("Guest deferred native ground reorientation to host.");
            }
            return false;
        }
    }

    [HarmonyPatch]
    internal static class GuestOrder
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertySetter(typeof(MiCharacterControllerPlayer), "skillCommand");
            yield return AccessTools.Method(typeof(MiCharacterControllerPlayer), "onSlowCommand", new[] { typeof(SkillCommand) });
            yield return AccessTools.Method(typeof(MiCharacterControllerPlayer), "onCommand", new[] { typeof(SkillCommand) });
        }

        private static bool Prefix(MiCharacterControllerPlayer __instance, SkillCommand __0)
            => Probe==null || !Forward(__instance.character, __0);

        internal static bool Forward(MiCharacter actor, SkillCommand command)
        {
            if(Restoring)return false;
            var probe = Probe;
            if (LockedContactMouse(actor) && command?.m_skill == null) return true;
            bool gesture=PlayerGesture(actor);
            if (command?.m_skill != null && probe != null && probe.UnassignedGuestPlayer(actor) && gesture) return true;
            if (probe == null || !probe.GuestOrderOwner(actor) || command?.m_skill == null ||
                !gesture) return false;
            var kind = command.m_skill.eSkillType;
            // Parallel ladder states generate their own enter/exit commands.
            // A coincidental mouse edge elsewhere is not another player order.
            if(InteractionSync.LadderSkill(kind) && !InMouseCommand(actor) && !InPlayerInput(actor))return false;
            bool bodyMove=kind==Skill.SkillType.GoTo && command.m_target?.m_entityTarget!=null &&
                command.m_target.m_entityTarget.bAsCharacter(out var body) && BodySync.Corpse(body);
            if(BodySync.Skill(kind) || InteractionSync.Supported(kind) || bodyMove)
            {
                // Context actions can become commands on the hold threshold,
                // between mouse-down and mouse-up. The native mouse dispatcher
                // is evidence of input even when Unity has no button edge.
                if(probe.ListensTo(actor)) probe.CaptureCommand(actor,command,PlanningExecution||InMouseCommand(actor)||InPlayerInput(actor));
                return true;
            }
            if (kind != Skill.SkillType.GoTo && !Melee.Contact(kind) && Melee.Decode(kind.ToString()) == Skill.SkillType.None) return false;
            if (command.m_target == null)
                return kind == Skill.SkillType.GoTo && LockedContactMouse(actor);
            if (probe.ListensTo(actor))
            {
                probe.ObserveDispatchedPoint(actor, command);
                probe.CaptureCommand(actor, command, PlanningExecution||InMouseCommand(actor)||InPlayerInput(actor));
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(MiCharacterControllerPlayer), "onCommand")]
    internal static class DispatchedCommand
    {
        private static void Prefix(MiCharacterControllerPlayer __instance, SkillCommand __0)
        {
            try
            {
                if (Probe == null || !InMouseCommand(__instance.character) || !MouseEdge() ||
                    !Probe.ListensTo(__instance.character)) return;
                Probe.ObserveDispatchedPoint(__instance.character, __0);
                Probe.CaptureCommand(__instance.character, __0);
            }
            catch (Exception ex) { MoveProbePlugin.ProbeLog.LogWarning(ex); }
        }
    }

    [HarmonyPatch(typeof(MiMouseInput), "setCommand")]
    internal static class MouseCommand
    {
        private static void Postfix(MiCharacter __1, SkillCommand __result)
        {
            try
            {
                if (Probe == null || !Probe.ListensTo(__1)) return;
                Probe.ObserveDispatchedPoint(__1,__result);
                Probe.ReserveTraversal(__1,__result);
                Probe.CaptureCommand(__1, __result, true);
            }
            catch (Exception ex) { MoveProbePlugin.ProbeLog.LogWarning(ex); }
        }
    }

    // The leader can be assigned directly during the mouse update.
    [HarmonyPatch(typeof(MiMouseInput), "update")]
    internal static class MouseUpdate
    {
        private static void Prefix(MiPlayerInput __0,out List<GuestMouseSnapshot> __state)
        {
            __state=null;mouseActor=null;mouseCrew=null;
            if(Probe==null)return;
            try
            {
                mouseActor=__0.leader;mouseFrame=Time.frameCount;mouseCrew=new HashSet<IntPtr>();
                if(!Probe.NativeGuest)
                {
                    var selected=__0.lSelectedCharacter;
                    if(selected!=null)for(int i=0;i<selected.Count;i++)mouseCrew.Add(selected[i].Pointer);
                    return;
                }
                __state=new List<GuestMouseSnapshot>();Probe.TraceMouse(mouseActor);
                foreach(var actor in Probe.GuestOwnedCrew())
                {
                    mouseCrew.Add(actor.Pointer);
                    __state.Add(GuestMouseSnapshot.Capture(actor,MouseEdge(),Input.GetMouseButton(0)||Input.GetMouseButton(1)));
                }
            }
            catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning(ex);}
        }
        private static void Postfix(MiPlayerInput __0,List<GuestMouseSnapshot> __state)
        {
            Probe?.ObserveGuestInput(__0);
            mouseActor=null;mouseCrew=null;
            if(Probe==null||!Probe.NativeGuest||__state==null)return;
            foreach(var state in __state)
            {
                try
                {
                    if(!Probe.GuestOrderOwner(state.Actor))continue;
                    state.Restore(MouseEdge(),(actor,cmd)=>Probe.CaptureCommand(actor,cmd),
                        (actor,cmd)=>Probe.ObserveDispatchedPoint(actor,cmd));
                }
                catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning(ex);}
            }
        }
    }

    [HarmonyPatch(typeof(MiPlayerInput), "s_trySetCrouch")]
    internal static class Stance
    {
        private static bool Prefix(MiCharacter __0)
        {
            if(Probe==null||!Probe.NativeGuest)return true;
            var controls=__0?.controllerPlayer?.m_playerInput?.m_rewired;
            int toggle=(int)MiInputActions.ToggleCrawl;
            if(controls==null||!(controls.GetButtonDown(toggle)||controls.GetButtonUp(toggle)))return true;
            // Tentatively selected free crew cannot change locally before
            // the group ownership frame arrives. Confirmed replay is exempt.
            return !Probe.UnassignedGuestPlayer(__0)&&(!Probe.GuestOrderOwner(__0)||Probe.ListensTo(__0));
        }
        private static void Postfix(MiCharacter __0, bool __1)
        {
            try
            {
                if (Probe == null || !Probe.ListensTo(__0)) return;
                var controls = __0.controllerPlayer?.m_playerInput?.m_rewired;
                int toggle = (int)MiInputActions.ToggleCrawl;
                if (controls != null && (controls.GetButtonDown(toggle) || controls.GetButtonUp(toggle)) &&
                    __0.movementType.bIsCrouching == __1)
                    Probe.Capture(__0, __1 ? "CROUCH" : "WALK");
            }
            catch (Exception ex) { MoveProbePlugin.ProbeLog.LogWarning(ex); }
        }
    }
}

// Intercept only explicit release execution. Range/alarm cleanup uses native
// returnControlInternal/stopControlNPC and must remain independent of input.
internal static class ControlReleaseInput
{
    internal static bool Forward(PlayerSkill __instance)
    {
            var actor=__instance?.charOrigin;
            var probe=NativeInput.Probe;
            if(NativeInput.Restoring||probe==null||!probe.GuestOrderOwner(actor))return true;
            var controller=actor.controller;
            bool Confirmed(SkillCommand command)=>command?.m_skill?.Pointer==__instance.Pointer&&InteractionSync.CommandSerial(command)>0;
            if(Confirmed(controller?.skillCommand)||Confirmed(controller?.skillCommandProcess)||Confirmed(controller?.skillCommandActive))return true;
            if(probe.ListensTo(actor))probe.TryCapture(actor,"USE "+InteractionProtocol.Code(__instance.eSkillType.ToString())+" n 0 0 0 0");
            return false;
    }
    [HarmonyPatch(typeof(SkillControlCancel),"execute")]
    internal static class Cancel
    { private static bool Prefix(SkillControlCancel __instance)=>Forward(__instance); }
    [HarmonyPatch(typeof(SkillOvertakeCancelPaused),"execute")]
    internal static class OvertakeCancel
    { private static bool Prefix(SkillOvertakeCancelPaused __instance)=>Forward(__instance); }
}
