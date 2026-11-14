using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mimimi.MiScript;
using UnityEngine;

internal static class NativeGait
{
    [ThreadStatic] private static MiCharacter issuing;
    [ThreadStatic] private static string intent;
    [ThreadStatic] private static bool syncMove;
    internal static bool Running(string mode) => mode == "RW" || mode == "RC";
    internal static bool ReturnsCrouched(string mode) => mode == "GC" || mode == "RC";
    internal static string TravelStyle(string mode) => Running(mode) ? "R" : ReturnsCrouched(mode) ? "C" : "W";

    internal static string Read(MiCharacter actor)
    {
        var move = actor?.movementType;
        if(move==null) return "W";
        if(move.bIsInWater) return move.bIsInFastMovement?"R":"W";
        if (move.bIsCrouching) return "C";
        return move.eMovementStyle == MiCharacterMovementType.MovementStyle.Run ? "R" : "W";
    }

    internal static bool CanRun(MiCharacter actor) => actor?.movementType!=null && actor.controllerPlayer!=null && actor.movementType.bCanRun && actor.controllerPlayer.bCanRun;
    internal static bool StopForStance(MiCharacter actor,bool crouch)
    {
        if(actor?.controllerPlayer==null || actor.movementType==null ||
            crouch && !actor.movementType.bCanCrouch) return false;
        var controller=actor.controllerPlayer;
        var input=controller.skillCommand;var active=actor.controller.skillCommandProcess;
        bool Walking(SkillCommand c)=>c?.m_skill==null || c.m_skill.eSkillType==Skill.SkillType.None || c.m_skill.eSkillType==Skill.SkillType.GoTo;
        // Do not cancel an irreversible strike or a jump/throw animation.
        if(!Walking(input) || !Walking(active) || !Walking(actor.controller.skillCommandActive)) return false;
        // A native route can outlive both command slots. Stop it even after
        // the dispatcher has consumed its GoTo command.
        controller.cancelSkill();
        // Shipping cancelSkill only writes a cancellation flag; a consumed
        // GoTo route can continue running. Clear the walking slots and end
        // the native movement driver now, without a persistent custom lock.
        controller.skillCommand=null;
        actor.controller.skillCommandProcess=null;
        actor.controller.clearActiveSkillCommand();
        if(controller.m_skillCommandSlow?.m_skill?.eSkillType==Skill.SkillType.GoTo)
            controller.m_skillCommandSlow=null;
        actor.movement?.stopInternal();
        return Apply(actor,crouch?"C":"W");
    }
    internal static string AvailableTravel(MiCharacter actor,string mode)
    {
        if(actor.movementType.bIsInWater) mode=Running(mode)?"RW":"GW";
        if (Running(mode) && !CanRun(actor))
            mode = ReturnsCrouched(mode) || actor.movementType.bIsCrouching ? "GC" : "GW";
        return mode == "GC" && !actor.movementType.bCanCrouch && !actor.movementType.bIsCrouching ? "GW" : mode;
    }

    internal static bool Apply(MiCharacter actor, string style)
    {
        var move = actor.movementType;
        if (move == null) return false;
        // Dive uses the native water modifier; crouched ground travel must
        // not repeatedly try to impose a crouch on the swimming controller.
        if(move.bIsInWater && style=="C") style="W";
        var wanted = style == "R" ? MiCharacterMovementType.MovementStyle.Run :
            style == "C" ? MiCharacterMovementType.MovementStyle.Crouch : MiCharacterMovementType.MovementStyle.Walk;
        if (move.eMovementStyle == wanted) return true;
        if (style == "R" && !CanRun(actor) || style == "C" && !move.bCanCrouch) return false;
        move.setMovementStyle(wanted);
        return true;
    }

    private static void Stamp(SkillCommand command, string mode)
    {
        bool run = Running(mode), crouch = ReturnsCrouched(mode);
        if (command.hasModifier(SkillCommand.ExecutionModifier.Run) != run)
            command.setModifier(SkillCommand.ExecutionModifier.Run, run);
        if (command.hasModifier(SkillCommand.ExecutionModifier.WasCrawling) != crouch)
            command.setModifier(SkillCommand.ExecutionModifier.WasCrawling, crouch);
    }

    private static bool MatchingGoTo(SkillCommand command, Vector3 point) =>
        command?.m_skill?.eSkillType == Skill.SkillType.GoTo && command.m_target != null &&
        command.m_target.m_entityTarget == null &&
        Vector3.Distance(command.m_target.v3Target, point) < 1.5f;

    // The second click upgrades the first click's verified path in place.
    // The game can already have consumed the input and process slots.
    internal static bool ChangePathSpeed(MiCharacter actor,Vector3 destination,string travel)
    {
        var input=actor.controllerPlayer.skillCommand;
        var process=actor.controller.skillCommandProcess;
        var move=actor.movement;
        if (move?.target?.m_entityTarget!=null || Unrelated(input,destination) || Unrelated(process,destination)) return false;
        bool pending=MatchingGoTo(input,destination);
        bool executing=MatchingGoTo(process,destination);
        bool moving=move?.target!=null && !move.isStoppedOrInactive &&
            Vector3.Distance(move.target.v3Target,destination)<=0.65f;
        if ((!pending && !executing && !moving) || !Apply(actor,TravelStyle(travel))) return false;
        if (pending) Stamp(input,travel);
        if (executing && process!=input) Stamp(process,travel);
        return true;
    }

    private static bool Unrelated(SkillCommand command,Vector3 destination) =>
        command?.m_skill!=null && command.m_skill.eSkillType!=Skill.SkillType.None &&
        (!MatchingGoTo(command,destination) || Vector3.Distance(command.m_target.v3Target,destination)>0.65f);

    // Unity can move a command from the input slot into the active process
    // before the next frame. Both slots can briefly refer to different goals.
    internal static bool MaintainRun(MiCharacter actor, Vector3 point)
    {
        if (!CanRun(actor)) return false;
        var queued = actor.controllerPlayer.skillCommand;
        var active = actor.controller.skillCommandProcess;
        bool queuedMatch = MatchingGoTo(queued, point);
        bool activeMatch = MatchingGoTo(active, point);
        if (queuedMatch) Stamp(queued, "RW");
        if (activeMatch && active != queued) Stamp(active, "RW");
        if (queuedMatch || activeMatch || active?.m_skill == null || active.m_skill.eSkillType == Skill.SkillType.GoTo)
            Apply(actor, "R");
        return queuedMatch || activeMatch;
    }

    [HarmonyPatch]
    internal static class CommandSpeed
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertySetter(typeof(MiCharacterControllerPlayer), "skillCommand");
            yield return AccessTools.Method(typeof(MiCharacterControllerPlayer), "onCommand", new[] { typeof(SkillCommand) });
        }
        private static void Prefix(MiCharacterControllerPlayer __instance, SkillCommand __0)
        {
            if (issuing != null && __instance.character == issuing && __0?.m_skill?.eSkillType == Skill.SkillType.GoTo)
            {
                Stamp(__0, intent);
                if(syncMove) __0.setModifier(SkillCommand.ExecutionModifier.DontCreateMovementLine,true);
            }
        }
    }

    // Reuse an identical native route, including the interval after its
    // command slots have been consumed. Never reuse an entity-targeted action.
    private static bool SameRoute(MiCharacter actor,Vector3 point)
    {
        bool Exact(SkillCommand c)=>c?.m_skill?.eSkillType==Skill.SkillType.GoTo &&
            c.m_target!=null && c.m_target.m_entityTarget==null && Vector3.Distance(c.m_target.v3Target,point)<0.025f;
        var input=actor.controllerPlayer.skillCommand;
        var process=actor.controller.skillCommandProcess;
        var active=actor.controller.skillCommandActive;
        bool Other(SkillCommand c)=>c?.m_skill!=null && c.m_skill.eSkillType!=Skill.SkillType.None && !Exact(c);
        if(Other(input)||Other(process)||Other(active))return false;
        var move=actor.movement;
        return Exact(input)||Exact(process)||move?.target!=null && move.target.m_entityTarget==null &&
            !move.isStoppedOrInactive && Vector3.Distance(move.target.v3Target,point)<0.025f;
    }

    internal static bool QuietDestination(SkillCommand command)=>command?.m_skill?.eSkillType==Skill.SkillType.GoTo &&
        command.hasModifier(SkillCommand.ExecutionModifier.DontCreateMovementLine);

    internal static bool Execute(MiCharacter actor, Vector3 point, string mode, bool synchronize=false)
    {
        if (Running(mode) && !CanRun(actor)) return false;
        if(synchronize && SameRoute(actor,point) && ChangePathSpeed(actor,point,mode))return true;
        if (!Apply(actor, TravelStyle(mode))) return false;
        var previousActor = issuing; var previousIntent = intent;
        bool previousSync=syncMove;
        try
        {
            issuing = actor; intent = mode; syncMove=synchronize;
            MiScriptLibraryProject.CharacterExecuteSkill(actor, Skill.SkillType.GoTo, point);
            var cmd = actor.controllerPlayer.skillCommand;
            if (cmd?.m_skill?.eSkillType == Skill.SkillType.GoTo && cmd.m_target != null &&
                Vector3.Distance(cmd.m_target.v3Target, point) < 1.5f)
            {
                Stamp(cmd, mode);
                if(synchronize)cmd.setModifier(SkillCommand.ExecutionModifier.DontCreateMovementLine,true);
            }
            Apply(actor, TravelStyle(mode));
            return true;
        }
        finally { issuing = previousActor; intent = previousIntent; syncMove=previousSync; }
    }

    internal static void CancelRunModifiers(MiCharacter actor, Vector3 point)
    {
        var cmd = actor.controllerPlayer?.skillCommand;
        var active = actor.controller?.skillCommandProcess;
        if (MatchingGoTo(cmd, point)) Stamp(cmd, "GW");
        if (active != cmd && MatchingGoTo(active, point)) Stamp(active, "GW");
    }

    internal static void UpdateLocalPoint(MiCharacter actor, Vector3 point, string mode)
    {
        var cmd = actor.controllerPlayer?.skillCommand;
        if (cmd?.m_skill?.eSkillType != Skill.SkillType.GoTo || cmd.m_target == null ||
            Vector3.Distance(cmd.m_target.v3Target, point) >= 1.5f ||
            Running(mode) && !CanRun(actor)) return;
        Stamp(cmd, mode);
        Apply(actor, TravelStyle(mode));
    }
}
