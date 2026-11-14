using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// Reuse the original confirmed command path when the cast cue was lost but a
// live trajectory arrived. Terminal history only retires work; it never fires.
internal sealed class FlightCastRecovery
{
    private sealed class Seen{internal int Serial;internal bool Ended;internal float Retry;}
    private readonly Dictionary<string,Seen> seen=new(StringComparer.Ordinal);
    internal void Clear()=>seen.Clear();
    internal void Apply(string[][] rows,bool cannon,Func<string[],bool> bound)
    {
        if(rows==null||NativeInput.Probe?.NativeGuest!=true)return;
        var latest=new Dictionary<string,string[]>(StringComparer.Ordinal);
        int serialAt=cannon?1:21;
        foreach(var row in rows)
        {
            if(!(cannon?CannonFlightProtocol.Row(row):FishingPullProtocol.Row(row)))continue;
            int serial=int.Parse(row[serialAt],CultureInfo.InvariantCulture);
            if(!latest.TryGetValue(row[0],out var old)||serial>int.Parse(old[serialAt],CultureInfo.InvariantCulture)||
                serial==int.Parse(old[serialAt],CultureInfo.InvariantCulture)&&row[3]!="A")latest[row[0]]=row;
        }
        foreach(var pair in latest)
        {
            var row=pair.Value;int serial=int.Parse(row[serialAt],CultureInfo.InvariantCulture);
            if(!seen.TryGetValue(pair.Key,out var state))seen[pair.Key]=state=new();
            if(serial<state.Serial||serial==state.Serial&&state.Ended)continue;
            if(serial>state.Serial){state.Serial=serial;state.Ended=false;state.Retry=0;}
            if(row[3]!="A"){state.Ended=true;continue;}
            if(bound(row))continue;
            float now=Time.realtimeSinceStartup;if(!float.IsFinite(now)||now<state.Retry)continue;
            state.Retry=now+.75f;
            try
            {
                var actor=InteractionSync.Resolve(row[0])?.TryCast<MiCharacter>();
                if(actor?.controllerPlayer==null||actor.health?.eHealthState!=MiCharacterHealth.HealthState.Alive)continue;
                bool busy=false;
                foreach(var command in new[]{actor.controller.skillCommand,actor.controller.skillCommandProcess,actor.controller.skillCommandActive})
                {
                    if(command==null)continue;
                    int current=InteractionSync.CommandSerial(command);
                    if(current>serial){state.Serial=current;state.Ended=true;busy=true;break;}
                    if(current==serial||command.m_skill?.eSkillType!=Skill.SkillType.GoTo){busy=true;break;}
                }
                if(busy)continue;
                Vector3 point;
                if(cannon&&row[4]=="P")
                {
                    if(!FishingPullProtocol.Points(row[24],out var points))continue;
                    var end=points[row[5]=="1"?0:points.Length-1];point=new(end.x,end.y,end.z);
                }
                else point=CrewSync.ReadPosition(row,cannon?21:13);
                var descriptor=new[]{InteractionProtocol.Code(cannon?"CannonShoot":"Fishing"),cannon?"-":row[2],
                    point.x.ToString("R",CultureInfo.InvariantCulture),point.y.ToString("R",CultureInfo.InvariantCulture),point.z.ToString("R",CultureInfo.InvariantCulture),"0"};
                // Issue retains serial, ignores already-spent resources and,
                // for cannons, restores the matching confirmed loaded victim.
                if(InteractionSync.Issue(actor,descriptor,0,true,serial))InteractionSync.MarkRecovery(actor.controller.skillCommand);
            }
            catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Flight cast recovery: "+ex.GetType().Name);}
        }
    }
}
