using System;
using System.Collections.Generic;
using System.Globalization;

internal sealed class CharmingGhostPlayback
{
    private readonly CharmingGhostResults order=new();
    private readonly Dictionary<string,(int generation,float next,bool failed)> attempts=new(StringComparer.Ordinal);
    internal void Clear(){order.Clear();attempts.Clear();CharmingGhostNative.Clear();}
    internal void Apply(string[][] rows,float now,Func<string,MiCharacter> source)
    {
        if(rows==null||!float.IsFinite(now))return;
        foreach(var row in rows)
        {
            if(!order.Accept(row))continue;
            int generation=int.Parse(row[1],CultureInfo.InvariantCulture);
            attempts.TryGetValue(row[0],out var previous);
            bool pending=previous.generation==generation&&previous.failed;
            try
            {
                var skill=source(row[0])?.controller?.getSkill(Skill.SkillType.CharmingGhost)?.TryCast<SkillCharmingGhost>();
                if(skill==null)continue;
                if(row[2]=="D")
                {
                    if(CharmingGhostNative.PoolReady(skill)&&!skill.m_bGhostAtChar){attempts.Remove(row[0]);continue;}
                    if(previous.generation==generation&&now<previous.next)continue;
                    attempts[row[0]]=(generation,now+1f,true);
                    CharmingGhostNative.RequestDeployment(skill,new UnityEngine.Vector3(
                        float.Parse(row[3],CultureInfo.InvariantCulture),float.Parse(row[4],CultureInfo.InvariantCulture),float.Parse(row[5],CultureInfo.InvariantCulture)));
                    continue;
                }
                CharmingGhostNative.CancelPending(skill);
                // A pooled ghost need not be created merely to match a return.
                if(skill.m_charGhost==null){attempts.Remove(row[0]);continue;}
                if(skill.m_bGhostAtChar&&!pending){attempts.Remove(row[0]);continue;}
                if(previous.generation==generation&&now<previous.next)continue;
                attempts[row[0]]=(generation,now+1f,true);
                // Verified original cancel entry performs movement, controller,
                // targeting and visual cleanup. Do not just hide the GameObject.
                skill.stopExtern();
                if(skill.m_bGhostAtChar)attempts.Remove(row[0]);
            }
            catch(Exception ex)
            {
                // stopExtern sets the return flag before finishing cleanup.
                // A throwing call remains pending even if that flag is now true.
                attempts[row[0]]=(generation,now+1f,true);
                MoveProbePlugin.ProbeLog.LogWarning("Charming ghost return recovery: "+ex.GetType().Name);
            }
        }
    }
}
