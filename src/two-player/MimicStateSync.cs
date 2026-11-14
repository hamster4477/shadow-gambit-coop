using System;
using System.Collections.Generic;
using System.Globalization;

// Current state persists beyond the short-lived interaction cue. Native
// transition generations change on form/incarnation changes, not every frame.
internal sealed class MimicStateSync
{
    private sealed class Stamp { internal IntPtr Copy; internal int Form,Generation; internal bool Pending; }
    private readonly Dictionary<string,Stamp> stamps=new(StringComparer.Ordinal);
    private sealed class Result { internal int Generation,Form; internal string Phase; internal float RetryAt; }
    private readonly Dictionary<string,Result> results=new(StringComparer.Ordinal);
    internal void Clear(){stamps.Clear();results.Clear();}
    internal void Apply(string[][] rows,Func<string,MiCharacter> resolve,float now)
    {
        if(rows==null||resolve==null||!float.IsFinite(now))return;
        foreach(var row in rows)
        {
            if(!MimicProtocol.Row(row))continue;
            string source=row[0],phase=row[3];int gen=int.Parse(row[1],CultureInfo.InvariantCulture),form=int.Parse(row[2],CultureInfo.InvariantCulture);
            if(results.TryGetValue(source,out var old))
            {
                if(gen<old.Generation||gen==old.Generation&&(old.Form!=form||old.Phase!="P"&&old.Phase!=phase))continue;
                if(gen>old.Generation)results[source]=old=new(){Generation=gen,Form=form,Phase=phase};
                else old.Phase=phase;
            }
            else results[source]=old=new(){Generation=gen,Form=form,Phase=phase};
            // Do not speculate on an unfinished host transition. Settled
            // state repeats until local async spawn/reuse actually completes.
            if(phase=="P"||now<old.RetryAt)continue;
            try
            {
                var root=resolve(source);var skill=MimicNative.Ability(root);
                if(skill?.character==null||GuestMove.RawIdentity(skill.character)!=source)continue;
                var current=skill.charCurrentMimicry??skill.character;
                bool original=current.Pointer==skill.character.Pointer;
                if((phase=="O")!=(form==skill.character.m_eCharacterName.m_iValue))continue;
                if(!skill.m_bInExecution&&MimicNative.Form(skill,current)==form&&original==(phase=="O"))continue;
                if(skill.m_bInExecution)continue;
                old.RetryAt=now+1f;
                MimicNative.Apply(current,form);
            }
            catch(Exception ex)
            {old.RetryAt=now+2f;MoveProbePlugin.ProbeLog.LogWarning("Mimic result: "+ex.GetType().Name);}
        }
    }
    internal List<string[]> Capture(IReadOnlyList<MiCharacter> sources)
    {
        var rows=new List<string[]>();var seen=new HashSet<IntPtr>();
        if(sources==null)return rows;
        foreach(var actor in sources)
        {
            var skill=MimicNative.Ability(actor);
            if(skill?.character==null||!seen.Add(skill.Pointer))continue;
            var root=skill.character;string source=GuestMove.RawIdentity(root);
            if(!SyncProtocol.Identity(source))throw new InvalidOperationException("Mimic source unavailable");
            var copy=skill.charCurrentMimicry??root;bool pending=skill.m_bInExecution;
            int form=pending?skill.m_eCurrentMimickedCharName.m_iValue:MimicNative.Form(skill,copy);
            if(form<0)continue; // Never publish an unknown native enum as a return.
            if(!stamps.TryGetValue(source,out var stamp))stamps.Add(source,stamp=new());
            // Completing the same pending form belongs to its existing
            // generation; beginning another choice or incarnation increments.
            bool completion=stamp.Pending&&!pending&&stamp.Form==form;
            if(stamp.Generation==0||stamp.Form!=form||pending&&!stamp.Pending||!completion&&stamp.Copy!=copy.Pointer)
                stamp.Generation=checked(stamp.Generation+1);
            stamp.Copy=copy.Pointer;stamp.Form=form;stamp.Pending=pending;
            var row=new[]{source,stamp.Generation.ToString(CultureInfo.InvariantCulture),form.ToString(CultureInfo.InvariantCulture),pending?"P":copy.Pointer==root.Pointer?"O":"C"};
            if(!MimicProtocol.Row(row))throw new InvalidOperationException("Invalid native mimic state");
            rows.Add(row);
        }
        if(rows.Count>MimicProtocol.MaxActors)throw new InvalidOperationException("Mimic roster exceeds bound");
        return rows;
    }
}
