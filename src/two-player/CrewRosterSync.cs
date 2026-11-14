using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

internal sealed class CrewRosterHost
{
    private sealed class ActorState
    {
        internal readonly AttackCue Attack=new AttackCue();
        internal readonly InteractionSync Interaction=new InteractionSync();
        internal readonly CoverSync Cover=new CoverSync();
    }
    private readonly Dictionary<IntPtr,ActorState> actors=new Dictionary<IntPtr,ActorState>();
    private ActorState For(MiCharacter actor)
    {
        if(!actors.TryGetValue(actor.Pointer,out var state))actors.Add(actor.Pointer,state=new ActorState());
        return state;
    }
    internal void Clear()=>actors.Clear();
    internal void Point(MiCharacter actor,SkillCommand command)
    {if(actor!=null&&actor.bControllerPlayer())For(actor).Attack.ObservePointCommand(command);}
    internal void Executed(MiCharacter actor,SkillCommand command)
    {if(actor!=null&&actor.bControllerPlayer())For(actor).Interaction.Mark(false,command);}
    internal void Observe(IReadOnlyList<MiCharacter> roster,MiCharacter a,MiCharacter b)
    {
        foreach(var actor in roster)if(actor!=null&&actor!=a&&actor!=b)For(actor).Attack.Observe(actor);
    }
    internal string Capture(string core,IReadOnlyList<MiCharacter> roster,MiCharacter a,MiCharacter b,Func<MiCharacter,char> owner=null,long revision=0,Func<MiCharacter,(string order,string cue,bool running)> confirmed=null)
    {
        string owners=owner==null?null:new string(new[]{owner(a),a==b?'-':owner(b)});
        var rows=new List<string>();var ids=new HashSet<string>(StringComparer.Ordinal){GuestMove.Identity(a),GuestMove.Identity(b)};
        foreach(var actor in roster)
        {
            if(actor==null||actor==a||actor==b)continue;
            string id=GuestMove.Identity(actor);
            if(!SyncProtocol.Identity(id)||!ids.Add(id)||actor.health==null)
                throw new InvalidOperationException("Ambiguous or incomplete crew roster");
            var state=For(actor);
            var order=confirmed==null?default:confirmed(actor);
            string row=CrewSync.Describe(actor,running:order.running,surface:true)+CrewSync.Cover(actor)+" "+Math.Max(0,actor.health.iHealth).ToString(CultureInfo.InvariantCulture)+" "+
                actor.health.eHealthState+" "+(order.cue??state.Attack.Wire())+" "+state.Interaction.WriteActor(actor)+" "+
                BodySync.CarryRoster(actor)+state.Cover.Write(actor,null)+(order.order??"");
            if(!CrewRosterProtocol.Actor(row.Split(' ')))throw new InvalidOperationException("Invalid native actor sample "+id);
            rows.Add(row);if(owner!=null)owners+=owner(actor);
        }
        if(rows.Count>CrewRosterProtocol.MaxRosterOnWire-2)throw new InvalidOperationException("Crew roster exceeds transport limit");
        return CrewRosterProtocol.Write(core,rows,revision,owners);
    }
}

internal sealed class CrewRosterGuest
{
    private sealed class Replica
    {
        internal MiCharacter Actor;
        internal readonly CrewFollower Follow=new CrewFollower();
        internal readonly CrewRoutePlayback Route=new CrewRoutePlayback();
        internal readonly ActorOrderReceipt Receipt=new ActorOrderReceipt();
        internal readonly GuestActorIntent Intent=new GuestActorIntent();
        internal readonly AttackPlayback Attack=new AttackPlayback();
        internal readonly InteractionSync Interaction=new InteractionSync();
        internal readonly BodySync Body=new BodySync();
        internal readonly CoverSync Cover=new CoverSync();
        internal Vector3 Previous;
        internal bool Seen,Using,Controlled,HadRoute,Fresh;
        internal long RouteSequence;
        internal Vector3 RouteGoal;
        internal float SampleStarted;
    }
    private readonly Dictionary<string,Replica> replicas=new Dictionary<string,Replica>(StringComparer.Ordinal);
    private string[][] current=Array.Empty<string[]>();
    private HashSet<string> active=new HashSet<string>(StringComparer.Ordinal);
    internal readonly HashSet<string> Protected=new HashSet<string>(StringComparer.Ordinal);
    internal bool Mirrors(MiCharacter actor)=>actor!=null&&GuestMove.Identity(actor) is string id&&active.Contains(id)&&
        replicas.TryGetValue(id,out var replica)&&replica.Actor==actor;
    internal void Clear()
    {
        foreach(var r in replicas.Values)r.Route.Stop(r.Actor);
        current=Array.Empty<string[]>();active.Clear();Protected.Clear();replicas.Clear();
    }
    // Resolve the entire roster before mutating native state. A guest missing
    // a DLC actor cannot quietly apply only the subset it happens to own.
    internal bool Prepare(string[][] rows,HashSet<string> deferred=null)
    {
        var next=new HashSet<string>(StringComparer.Ordinal);
        var resolved=new Dictionary<string,Replica>(StringComparer.Ordinal);
        var readyRows=new List<string[]>();
        foreach(var row in rows)
        {
            string id=row[0];
            var actor=MoveProbe.CrewById(id);
            if(actor==null){if(deferred?.Contains(id)==true)continue;return false;}
            next.Add(id);readyRows.Add(row);
            resolved[id]=replicas.TryGetValue(id,out var existing)&&existing.Actor==actor?existing:new Replica{Actor=actor};
        }
        foreach(var pair in replicas)
            if(!resolved.TryGetValue(pair.Key,out var kept)||kept!=pair.Value)pair.Value.Route.Stop(pair.Value.Actor);
        replicas.Clear();foreach(var pair in resolved)replicas.Add(pair.Key,pair.Value);
        current=readyRows.ToArray();active=next;return true;
    }
    internal IEnumerable<MiCharacter> Actors {get {foreach(var row in current)yield return replicas[row[0]].Actor;}}
    private Replica Find(MiCharacter actor)=>actor!=null&&replicas.TryGetValue(GuestMove.Identity(actor),out var r)&&active.Contains(GuestMove.Identity(actor))?r:null;
    internal bool CanRedirect(MiCharacter actor,float now)=>Find(actor)?.Intent.CanRedirect(now)==true;
    internal bool HasMelee(MiCharacter actor)=>Find(actor)?.Intent.HasMelee==true;
    internal bool PreferredCrouch(MiCharacter actor)=>Find(actor)?.Intent.PreferredCrouch==true;
    internal bool Crouched(MiCharacter actor)=>Find(actor)?.Intent.Crouched==true;
    internal bool PlayingContact(MiCharacter actor)=>Find(actor)?.Attack.PlayingContact==true;
    internal bool Busy(MiCharacter actor)=>Find(actor)?.Intent.Busy==true;
    internal void ResumeWait(MiCharacter actor,float elapsed,float now){var r=Find(actor);if(r!=null)r.Route.Route.ResumeWait(elapsed,now);}
    internal void Withdraw(MiCharacter actor)=>Find(actor)?.Intent.Withdraw();
    internal bool Confirmed(MiCharacter actor,float now,out Vector3 point)
    {
        var r=Find(actor);point=r?.Previous??default;
        return r!=null&&r.Seen&&r.Fresh&&!r.Receipt.Waiting&&!r.Intent.Holding(now)&&now-r.SampleStarted<=0.22f;
    }
    internal void Queued(MiCharacter actor,string command,float now)
    {
        var r=Find(actor);if(r==null)return;
        r.Intent.Queued(now);
        if(command.StartsWith("HIT ",StringComparison.Ordinal))r.Intent.Attack(command,now);
        else r.Intent.Withdraw();
        if(command=="CROUCH" || command=="WALK")
        {
            r.Intent.Stance(command=="CROUCH",now);
            r.Route.Stop(actor);CrewRoutePlayback.CancelFollow(actor,r.Follow);
            NativeGait.StopForStance(actor,command=="CROUCH");
        }
        else HoldNewIntent(r,command);
    }
    internal void ApplyHealth(GuardPursuit pursuit)
    {
        foreach(var row in current)pursuit.ReconcileHealth(replicas[row[0]].Actor,int.Parse(row[5],CultureInfo.InvariantCulture),row[6]);
    }
    internal void Sending(string actor,long sequence,int read,float now,string command,bool queued=false)
    {
        if(!replicas.TryGetValue(actor,out var r)||!active.Contains(actor))return;
        r.Receipt.Sending(sequence,read,now);
        r.Intent.Sent(command,sequence,queued);
        HoldNewIntent(r,command);
    }
    private static void HoldNewIntent(Replica r,string command)
    {
        var fields=command.Split(' ');
        bool ground=(fields.Length==4 || fields.Length==5) &&
            (fields[0]=="GC" || fields[0]=="GW" || fields[0]=="RC" || fields[0]=="RW");
        bool continuing=false;
        if(ground && r.Route.Route.Active)
        {
            var target=CrewSync.ReadPosition(fields,1);
            continuing=Vector3.Distance(target,r.Route.Route.Goal)<=0.25f ||
                AcceptedGroundRoute.Continuing(GuestGroundTarget.NavigationPosition(r.Actor),r.Route.Route.Goal,target);
        }
        if(!continuing)
        {
            r.Route.Stop(r.Actor);
            CrewRoutePlayback.CancelFollow(r.Actor,r.Follow);
        }
    }
    internal void Complete(string actor,long sequence,bool accepted,int read)
    {if(replicas.TryGetValue(actor,out var r)&&active.Contains(actor)){r.Receipt.Complete(sequence,accepted,read);r.Intent.Reply(sequence,accepted);}}
    internal void Tick(float now,bool held)
    {
        foreach(string id in active)
        {
            var r=replicas[id];
            if(r.Route.Route.Issued && (held || now-r.SampleStarted>0.5f))r.Route.Stop(r.Actor,clear:false);
        }
    }
    internal void ApplyActions(int read=0,float now=0f,float sampleStarted=0f,bool held=false)
    {
        Protected.Clear();
        foreach(var row in current)
        {
            var r=replicas[row[0]];var point=CrewSync.ReadPosition(row,1);var actor=r.Actor;
            int orderAt=CrewRosterProtocol.CoverEnd(row);bool controlled=row.Length>orderAt;
            long sequence=controlled?long.Parse(row[orderAt+1],CultureInfo.InvariantCulture):0;
            bool fresh=!controlled || r.Receipt.Fresh(sequence,read,now)&&!r.Intent.Holding(now);
            bool busy=controlled&&row[orderAt+3]=="1";
            r.Intent.Observe(row[4].Substring(0,1),controlled?row[orderAt+2]:row[4].Substring(0,1),busy,fresh,now);
            if(controlled&&r.Intent.HoldingStance(now))fresh=false;
            r.Fresh=fresh;
            r.Controlled=controlled;r.SampleStarted=sampleStarted;
            r.Using=r.Interaction.Apply(row,13,actor,point,fresh);
            bool playing=r.Using||r.Attack.Apply(row,7,actor,point,controlled&&!busy,fresh,controlled);
            bool relocate=!r.Seen||Vector3.Distance(r.Previous,point)>8f;
            Vector3 step=r.Seen?point-r.Previous:default;
            bool hasRoute=controlled && !busy && !playing && row[orderAt+4]=="1";
            bool replayed=false;
            if(!hasRoute || relocate)
            {r.Route.Stop(actor);r.HadRoute=false;}
            if(hasRoute && fresh && !relocate)
            {
                Vector3 goal=CrewSync.ReadPosition(row,orderAt+5);
                string surface=CrewRosterProtocol.DestinationSurface(row);
                if(!r.HadRoute || r.RouteSequence!=sequence || Vector3.Distance(r.RouteGoal,goal)>0.1f || r.Route.Route.Surface!=surface)
                {
                    r.Route.Begin(goal,surface,point,now);
                    r.RouteSequence=sequence;r.RouteGoal=goal;r.HadRoute=true;
                }
                if(r.Route.Route.Active)replayed=r.Route.Replay(actor,r.Follow,point,GuestGroundTarget.NavigationPosition(actor),
                    row[4].Substring(0,1),now,now-sampleStarted,goal,held);
            }
            if(!playing && !replayed && fresh && !(controlled&&held))
            {
                Vector3 local=GuestGroundTarget.NavigationPosition(actor);
                float error=Vector3.Distance(local,point);
                bool passed=controlled && step.sqrMagnitude>0.0004f && error<0.55f &&
                    Vector3.Dot(step.normalized,local-point)>error*0.8f;
                char hostCover=CrewRosterProtocol.Cover(row),localCover=CrewSync.Cover(actor);
                bool edgeMismatch=hostCover!='x' && localCover!='x' && (hostCover=='o')!=(localCover=='o') &&
                    step.sqrMagnitude<=0.0004f;
                if(!passed)r.Follow.Follow(actor,point,row[4].Substring(0,1),confirmed:controlled,relocate:relocate,
                    hostStep:step,edgeMismatch:edgeMismatch,inside:hostCover!='o',hostSurface:row[4][1]);
                else NativeGait.Apply(actor,row[4].Substring(0,1));
            }
            r.Previous=point;r.Seen=true;
            if(r.Attack.ProtectedId!=null)Protected.Add(r.Attack.ProtectedId);
            var effect=InteractionSync.ProtectedTarget(actor);if(effect!=null)Protected.Add(effect);
        }
    }
    internal void ApplyResults(Dictionary<string,MiCharacter> guards,string protectedA,string protectedB)
    {
        foreach(var row in current)
        {
            var r=replicas[row[0]];
            if(!r.Using&&!r.Interaction.ProtectBodies(row,13,r.Actor))
                r.Body.Apply(new[]{row[21],"-","0"},guards,r.Actor,null,protectedA,protectedB,false,Protected);
            r.Cover.Apply(row[23..CrewRosterProtocol.CoverEnd(row)],r.Actor,null);
        }
    }
}
