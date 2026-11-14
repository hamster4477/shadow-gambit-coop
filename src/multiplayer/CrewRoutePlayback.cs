using UnityEngine;
using UnityEngine.AI;

// Native ground-route playback is actor-local. Both the primary guest and
// additional owned crew use this executor; it never changes the selected actor.
internal sealed class CrewRoutePlayback
{
    internal readonly AcceptedGroundRoute Route=new AcceptedGroundRoute();
    private Vector3 goalAtIssue;
    private Vector3? issuedPoint;
    private float nextIssue;
    private string lastStyle;
    internal void Begin(Vector3 target,string surface,Vector3 host,float now)
    { Route.Begin(target,surface,host,now);nextIssue=0f; }
    internal static bool Follows(SkillCommand command,Vector3? point)=>point.HasValue &&
        command?.m_skill?.eSkillType==Skill.SkillType.GoTo && command.m_target!=null &&
        command.m_target.m_entityTarget==null && Vector3.Distance(command.m_target.v3Target,point.Value)<0.18f;
    internal static void CancelFollow(MiCharacter actor,CrewFollower follow)
    {
        if(actor==null)return;
        var input=actor.controllerPlayer?.skillCommand;var active=actor.controller?.skillCommandProcess;
        bool ours=Follows(input,follow.Last)||Follows(input,follow.Previous);
        bool activeOurs=Follows(active,follow.Last)||Follows(active,follow.Previous);
        if((ours || input?.m_skill==null) && (activeOurs || active?.m_skill==null) && (ours || activeOurs))
            actor.controllerPlayer?.cancelSkill();
        follow.Previous=follow.Last=follow.RunWaypoint=null;
    }
    internal void Stop(MiCharacter actor,bool clear=true,bool protectContact=false)
    {
        // Begin may reset the policy's Issued flag during a sharp retarget.
        // The old native GoTo still exists until we cancel or replace it.
        if(issuedPoint.HasValue && actor!=null && !protectContact)
        {
            var input=actor.controllerPlayer?.skillCommand;
            var active=actor.controller?.skillCommandProcess;
            bool ours=Follows(input,issuedPoint),activeOurs=Follows(active,issuedPoint);
            if((ours || input?.m_skill==null) && (activeOurs || active?.m_skill==null) && (ours || activeOurs))
                actor.controllerPlayer?.cancelSkill();
        }
        Route.Issued=false;issuedPoint=null;
        if(clear)Route.Clear();
    }
    internal bool Replay(MiCharacter actor,CrewFollower follow,Vector3 host,Vector3 local,string style,
        float now,float age,Vector3? actual,bool held=false)
    {
        if(actor==null || actor.controllerPlayer==null || actor.controller==null ||
            actor.health?.eHealthState!=MiCharacterHealth.HealthState.Alive || InteractionSync.Climbing(actor) || DigStateSync.Held(actor) || CannonStateSync.Held(actor) || FishingPullSync.Held(actor))
        { Stop(actor,protectContact:true);return false; }
        if(!Route.UpdateGoal(actual,now))
        {
            bool wait=Route.AwaitingGoal(now);Stop(actor,clear:!wait);return wait;
        }
        var action=Route.Decide(host,local,now,age);
        if(action==AcceptedGroundRoute.Action.FollowSnapshot){Stop(actor);return false;}
        if(action==AcceptedGroundRoute.Action.Wait || held){Stop(actor,clear:false);return true;}
        var controller=actor.controllerPlayer;
        Vector3 destination=Route.Goal;
        if(Route.Issued && lastStyle=="R" && Vector3.Distance(host,destination)<0.45f &&
            Vector3.Distance(local,destination)>0.50f && NativeGait.CanRun(actor))style="R";
        bool sameGoal=Vector3.Distance(goalAtIssue,destination)<0.10f;
        if(Route.Issued && sameGoal && style==lastStyle &&
            (Follows(controller.skillCommand,issuedPoint) || Follows(actor.controller.skillCommandProcess,issuedPoint)))
        { NativeGait.Apply(actor,style);return true; }
        if(Vector3.Distance(local,destination)<0.2f || sameGoal && style==lastStyle && now<nextIssue)return true;
        nextIssue=now+0.25f;
        if(Route.Surface!=null)
        {
            if(!GuestGroundTarget.SurfaceFloor(actor,Route.Surface,destination,out destination)){Stop(actor);return false;}
        }
        else if(NavMesh.SamplePosition(destination,out var hit,0.65f,actor.navigation?.areaMask ?? -1))destination=hit.position;
        else {Stop(actor);return false;}
        bool run=style=="R" && NativeGait.CanRun(actor);
        string gait=NativeGait.AvailableTravel(actor,run?"RW":style=="C"?"GC":"GW");
        if(!NativeGait.Execute(actor,destination,gait,true)){Stop(actor);return false;}
        follow.Previous=follow.Last;follow.Last=issuedPoint=destination;
        Route.Issued=true;lastStyle=style;goalAtIssue=Route.Goal;
        return true;
    }
}
