using System;

internal sealed class ControlPlayback
{
    private readonly ControlRecovery recovery=new();
    private readonly System.Collections.Generic.Dictionary<string,SkillControl> releasing=new();
    internal void Clear(){recovery.Clear();releasing.Clear();}
    internal void ReconcileManifest(ControlledCrewProtocol.Entry[] previous,ControlledCrewProtocol.Entry[] current)
    {
        if(previous==null||current==null)return;
        foreach(var old in previous)
        {
            if(old.Kind!="O"&&old.Kind!="C")continue;
            if(Array.Exists(current,next=>next.Id==old.Id&&next.Target==old.Target))continue;
            var binding=ControlledCrew.Active.Find(old.Id);
            if(binding==null||binding.Manifest.Target!=old.Target)continue;
            var types=old.Kind=="C"?new[]{Skill.SkillType.Control}:new[]{Skill.SkillType.Overtake,Skill.SkillType.KillOvertake,Skill.SkillType.KnockoutOvertake,Skill.SkillType.OvertakeByOvertaken};
            foreach(var type in types)
            {
                var skill=binding.Caster?.controller?.getSkill(type)?.TryCast<SkillControl>();
                if(skill?.m_charControlled==binding.Actor)
                    releasing[old.Id]=skill;
            }
        }
        foreach(var id in new System.Collections.Generic.List<string>(releasing.Keys))
        {
            if(Array.Exists(current,next=>next.Id==id&&releasing[id].m_charControlled!=null&&Melee.GuardId(releasing[id].m_charControlled)==next.Target)||ControlNative.Release(releasing[id]))releasing.Remove(id);
        }
    }
    private static SkillControl SkillFor(string[] row,Func<string,MiCharacter> source)
    {
        var actor=source(row[0]);
        if(actor==null||!Enum.TryParse<Skill.SkillType>(row[1],out var type))return null;
        var skill=actor.controller?.getSkill(type)?.TryCast<SkillControl>();
        return skill!=null&&skill.eSkillType==type?skill:null;
    }
    internal void Apply(string[][] rows,float now,Func<string,MiCharacter> source,Func<string,MiCharacter> target)
    {
        recovery.Apply(rows,now,id=>source(id)!=null,row=>
        {
            var skill=SkillFor(row,source);if(skill==null)return false;
            if(row[3]=="0")return !skill.bInMindControl&&!skill.m_bInPreExecute&&skill.m_controllerCurrent==null&&skill.m_charControlled==null;
            return skill.bInMindControl&&skill.m_controllerCurrent!=null&&skill.m_charControlled!=null&&Melee.GuardId(skill.m_charControlled)==row[5];
        },row=>
        {
            try
            {
                var skill=SkillFor(row,source);if(skill==null)return false;
                return row[3]=="1"?ControlNative.Enter(skill,target(row[5])):ControlNative.Release(skill);
            }
            catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Control result recovery: "+ex.GetType().Name);return false;}
        });
    }
}
