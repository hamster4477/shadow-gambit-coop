using System;
using UnityEngine;

internal static class GuestDisposal
{
    internal static SkillCommand ForInteraction(MiCharacter actor)
    {
        var controller=actor?.controllerPlayer;
        var input=controller?.m_playerInput;
        if(input?.m_mouseInput==null||input.m_mouseInput.bOverUI||input.m_mouseInput.bOverGuardHouseUI||input.m_planningModeInput?.m_bActive==true)return null;
        var selected=controller.skillSelection;
        if(selected!=null&&selected.Pointer!=controller.m_skillSelectionNull?.Pointer&&
            selected.eSkillType is not (Skill.SkillType.None or Skill.SkillType.GoTo or Skill.SkillType.AutoUse or Skill.SkillType.ExplicitUse or Skill.SkillType.UseGeneric or Skill.SkillType.UseCharacterInHub))return null;
        var cache=input.m_raycastCache;
        if(cache==null)return null;
        cache.Recache();
        // Context hints can refer to a nearby object the user did not click.
        // Only the primary action for the exact current cursor target wins.
        // Advanced/held actions retain their original input semantics.
        var context=controller.m_characterPlayerTrigger?.m_contextAction;
        var command=context?.m_skillCommand;
        var target=cache.entity;
        if(target==null&&cache.bMouseOverNPC)target=cache.charNPC;
        if(target==null&&cache.bMouseOverSelectable)target=cache.selectable?.character;
        if(target==null)return null;
        var mapCommand=ForMapTable(controller,target);
        if(mapCommand!=null)return mapCommand;
        if(context?.entity?.Pointer==target.Pointer)
        {
            if(!context.bEnabled||context.bAdvanced)return null;
            if(context.bInteractable)
            {
                if(command?.m_skill==null||command.m_target==null)return null;
                if(!InteractionProtocol.Context(InteractionProtocol.Code(command.m_skill.eSkillType.ToString())))return null;
                var ability=command.m_skill.TryCast<PlayerSkill>();
                if(ability==null||!ability.isTargetValid(command.m_target))return null;
                return new SkillCommand(command,true);
            }
        }
        // Nearby context hints disappear outside their trigger radius. A ship
        // conversation still needs the native approach action from far away,
        // including the entry beside Red Marley's floor projection.
        if(!ShipCrewSession.Active)return null;
        var resolver=controller.m_skillSelectionNull?.TryCast<PlayerSkill>()??controller.getSkill(Skill.SkillType.AutoUse)?.TryCast<PlayerSkill>();
        if(resolver==null)return null;
        var nativeTarget=new MiCharacterTarget(target,null);
        var mode=PlayerSkill.MiValidSkillTarget.GetModifier.Command|PlayerSkill.MiValidSkillTarget.GetModifier.Apply|PlayerSkill.MiValidSkillTarget.GetModifier.Default;
        var action=resolver.getSkillForTarget(nativeTarget,mode).m_playerSkill;
        if(action==null||!InteractionProtocol.Context(InteractionProtocol.Code(action.eSkillType.ToString()))||!action.isTargetValid(nativeTarget))return null;
        return new SkillCommand(action,nativeTarget);
    }
    private static SkillCommand ForMapTable(MiCharacterControllerPlayer controller,Entity target)
    {
        if(!ShipCrewSession.Active||!target.bAsUsable(out var usable)||usable==null)return null;
        var commands=usable.m_lCmdOnUse;
        bool opensMap=false;
        if(commands!=null)foreach(var command in commands)
            if(command?.TryCast<CmdOpenWorldMap>()!=null){opensMap=true;break;}
        if(!opensMap)return null;
        // The observed table command is UseGeneric with AdvancedExecution.
        // An idle GoTo resolver and the nearby primary hint can both omit it.
        // Rebuild only this known ship interaction through the native skill,
        // preserving its approach point, requirements and host validation.
        var skill=controller.getSkill(Skill.SkillType.UseGeneric)?.TryCast<PlayerSkill>();
        if(skill==null)return null;
        var nativeTarget=new MiCharacterTarget(target,null);
        var mode=PlayerSkill.MiValidSkillTarget.GetModifier.Command|PlayerSkill.MiValidSkillTarget.GetModifier.Apply|PlayerSkill.MiValidSkillTarget.GetModifier.Advanced;
        var action=skill.getSkillForTarget(nativeTarget,mode).m_playerSkill;
        if(action==null||action.eSkillType!=Skill.SkillType.UseGeneric||!action.isTargetValid(nativeTarget))return null;
        var result=new SkillCommand(action,nativeTarget);
        result.setModifier(SkillCommand.ExecutionModifier.AdvancedExecution,true);
        return result;
    }
    internal static bool IsDisposal(Skill.SkillType kind)=>kind is Skill.SkillType.UseCarryBodyThrow or Skill.SkillType.UseCarryBodyDrop or Skill.SkillType.UseCarryTorchThrow or Skill.SkillType.UseCarryTorchDrop or Skill.SkillType.UseCarryObjectDrop;
    internal static SkillCommand ForClick(MiCharacter actor)
    {
        var controller=actor?.controllerPlayer;
        var input=controller?.m_playerInput;
        if(controller==null || input?.m_mouseInput==null || input.m_mouseInput.bOverUI || input.m_mouseInput.bOverGuardHouseUI || input.m_planningModeInput?.m_bActive==true) return null;
        var cache=input.m_raycastCache;
        if(cache==null) return null;
        cache.Recache();
        var trigger=controller.m_characterPlayerTrigger;
        if(trigger==null) return null;
        foreach(var context in new[]{trigger.m_contextAction,trigger.m_contextActionAdvanced})
        {
            var command=context?.m_skillCommand;
            if(context==null || !context.bEnabled || !context.bInteractable || command?.m_skill==null || !IsDisposal(command.m_skill.eSkillType) || command.m_target==null) continue;
            var target=command.m_target;
            bool sameEntity=cache.entity!=null && context.entity!=null && cache.entity.Pointer==context.entity.Pointer;
            bool samePoint=cache.iWalkableLayer>=0 && (Vector3.Distance(cache.v3WalkableLayerRaycastHitPoint,target.v3Target)<1.2f ||
                target.m_charTargetData?.m_bValue==true && Vector3.Distance(cache.v3WalkableLayerRaycastHitPoint,target.m_charTargetData.m_v3AlternativeTarget)<1.2f);
            // Nearby contextual hints are not instructions: the click must
            // address this exact entity/landing, never merely be near a carrier.
            if(!sameEntity && !samePoint) continue;
            var ability=command.m_skill.TryCast<PlayerSkill>();
            if(ability==null || !ability.isTargetValid(target)) continue;
            var copy=new SkillCommand(command,true);
            copy.setModifier(SkillCommand.ExecutionModifier.AdvancedExecution,context.bAdvanced || command.hasModifier(SkillCommand.ExecutionModifier.AdvancedExecution));
            return copy;
        }
        return null;
    }
}
