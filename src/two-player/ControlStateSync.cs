using System;
using System.Collections.Generic;

// Persistent control observations are independent of short-lived cast cues.
// Not yet applied to guests: native controller restoration must be paired
// with explicit terminal state, not inferred from a missing roster entry.
internal sealed class ControlStateSync
{
    internal sealed class State
    {
        internal string Source,Target;
        internal Skill.SkillType Skill;
        internal bool Active,Preparing;
        internal int Generation;
    }
    private readonly Dictionary<string,State> previous=new(StringComparer.Ordinal);
    internal void Clear()=>previous.Clear();
    private static readonly Skill.SkillType[] types={Skill.SkillType.Control,Skill.SkillType.Overtake,
        Skill.SkillType.KillOvertake,Skill.SkillType.KnockoutOvertake,Skill.SkillType.OvertakeByOvertaken};
    internal List<string[]> CaptureRows(IReadOnlyList<MiCharacter> sources)
    {
        var rows=new List<string[]>();
        foreach(var state in Capture(sources))
            rows.Add(new[]{state.Source,state.Skill.ToString(),state.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
                state.Active?"1":"0",state.Preparing?"1":"0",state.Target});
        return rows;
    }
    internal List<State> Capture(IReadOnlyList<MiCharacter> sources)
    {
        var rows=new List<State>();var seen=new HashSet<IntPtr>();
        if(sources==null)return rows;
        foreach(var source in sources)
        {
            if(source?.controller==null)continue;
            string identity=GuestMove.Identity(source);
            if(!SyncProtocol.Identity(identity))throw new InvalidOperationException("Control source identity missing");
            foreach(var type in types)
            {
                var skill=source.controller.getSkill(type)?.TryCast<SkillControl>();
                if(skill==null||!seen.Add(skill.Pointer))continue;
                // Native preExecute sets bInMindControl before startControl
                // assigns m_charControlled. This is preparation, not a live proxy.
                bool active=skill.bInMindControl&&skill.m_charControlled!=null;
                if(skill.bInMindControl&&!skill.m_bInPreExecute&&skill.m_charControlled==null)
                    throw new InvalidOperationException("Control target identity missing");
                var target=active?skill.m_charControlled:skill.m_bInPreExecute?skill.targetCharacter:null;
                string id=target==null?"-":Melee.GuardId(target);
                if((active&&target==null)||(id!="-"&&!SyncProtocol.GuardId(id)))
                    throw new InvalidOperationException("Control target identity missing");
                rows.Add(new(){Source=identity,Target=id,Skill=skill.eSkillType,Active=active,Preparing=skill.m_bInPreExecute});
            }
        }
        // Commit only after the complete sample validates. A failed actor
        // must not consume generations for earlier actors in the same frame.
        var keys=new HashSet<string>(StringComparer.Ordinal);
        foreach(var row in rows)
        {
            string key=row.Source+"/"+(int)row.Skill;
            if(!keys.Add(key))throw new InvalidOperationException("Ambiguous control skill identity");
            previous.TryGetValue(key,out var old);
            bool changed=old==null||old.Target!=row.Target||old.Active!=row.Active||old.Preparing!=row.Preparing;
            row.Generation=changed?checked((old?.Generation??0)+1):old.Generation;
        }
        foreach(var row in rows)previous[row.Source+"/"+(int)row.Skill]=row;
        return rows;
    }
}
