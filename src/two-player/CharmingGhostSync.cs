using System;
using System.Collections.Generic;
using UnityEngine;

internal sealed class CharmingGhostSync
{
    internal sealed class State
    {
        internal string Source,Phase,Health;
        internal int Generation,HitPoints;
        internal IntPtr Pointer;
        internal Vector3 Position;
        internal bool Enabled;
    }
    private readonly Dictionary<string,State> previous=new(StringComparer.Ordinal);
    internal void Clear()=>previous.Clear();
    internal List<string[]> CaptureRows(IReadOnlyList<MiCharacter> sources)
    {
        var rows=new List<string[]>();var culture=System.Globalization.CultureInfo.InvariantCulture;
        foreach(var s in Capture(sources))rows.Add(new[]{s.Source,s.Generation.ToString(culture),s.Phase,
            s.Position.x.ToString("R",culture),s.Position.y.ToString("R",culture),s.Position.z.ToString("R",culture),
            s.HitPoints.ToString(culture),s.Health,s.Enabled?"1":"0"});
        return rows;
    }
    internal List<State> Capture(IReadOnlyList<MiCharacter> sources)
    {
        var rows=new List<State>();var seen=new HashSet<IntPtr>();var ids=new HashSet<string>(StringComparer.Ordinal);
        if(sources==null)return rows;
        foreach(var source in sources)
        {
            var skill=source?.controller?.getSkill(Skill.SkillType.CharmingGhost)?.TryCast<SkillCharmingGhost>();
            if(skill==null||!seen.Add(skill.Pointer))continue;
            string id=GuestMove.Identity(source);
            if(!SyncProtocol.Identity(id)||!ids.Add(id))throw new InvalidOperationException("Ambiguous charming ghost source");
            var ghost=skill.m_charGhost;
            var state=new State{Source=id,Phase=ghost==null?"N":skill.m_bGhostAtChar?"R":"D",Pointer=ghost?.Pointer??IntPtr.Zero,Health="-"};
            if(ghost!=null)
            {
                if(ghost.health==null)throw new InvalidOperationException("Ghost health unavailable");
                state.Position=ghost.transform.position;
                if(!float.IsFinite(state.Position.x)||!float.IsFinite(state.Position.y)||!float.IsFinite(state.Position.z))throw new InvalidOperationException("Invalid ghost position");
                state.HitPoints=Math.Max(0,ghost.health.iHealth);state.Health=ghost.health.eHealthState.ToString();state.Enabled=ghost.gameObject.activeInHierarchy;
            }
            previous.TryGetValue(id,out var old);
            state.Generation=old==null?1:old.Phase!=state.Phase||old.Pointer!=state.Pointer?checked(old.Generation+1):old.Generation;
            rows.Add(state);
        }
        if(rows.Count>64)throw new InvalidOperationException("Ghost roster exceeds bound");
        foreach(var row in rows)previous[row.Source]=row;
        return rows;
    }
}
