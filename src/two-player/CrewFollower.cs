using System;
using UnityEngine;
using UnityEngine.AI;

// Each actor keeps its own follow waypoint and shore/cover correction timer.
internal sealed class CrewFollower
{
    internal Vector3? Previous,Last,RunWaypoint;
    private Vector3 runHeading;
    private float nextEdgeCorrection;
    internal void Reset(){Previous=Last=RunWaypoint=null;runHeading=default;nextEdgeCorrection=0;}
    internal void Follow(MiCharacter crew, Vector3 target, string style, bool confirmed = false, bool relocate = true, Vector3 hostStep = default,

        bool edgeMismatch = false, bool inside = false,char hostSurface = 'x')

    {

        float distance = Vector3.Distance(GuestGroundTarget.NavigationPosition(crew), target);

        if (crew.health == null || crew.health.eHealthState != MiCharacterHealth.HealthState.Alive) return;

        if (InteractionSync.Climbing(crew)||DigStateSync.Held(crew)||CannonStateSync.Held(crew)||FishingPullSync.Held(crew)) return;

        var cmd = confirmed ? crew.controllerPlayer.skillCommand : null;

        if (confirmed && cmd?.m_skill == null && crew.controller.skillCommandProcess?.m_skill?.eSkillType == Skill.SkillType.GoTo)

            cmd = crew.controller.skillCommandProcess;

        bool moving = cmd?.m_skill?.eSkillType == Skill.SkillType.GoTo && cmd.m_target != null;

        float retarget = moving ? Vector3.Distance(cmd.m_target.v3Target, target) : 0f;

        bool needsMove = confirmed ? distance > 0.18f && (!moving || retarget > 0.12f) || retarget > 0.5f : distance > 0.55f;

        if (confirmed)

        {

            if (style != "R" || relocate) RunWaypoint = null;

            else if (RunWaypoint.HasValue)

            {

                // Only retain a waypoint that this follower issued. Replace an

                // unrelated native click even if the last host sample is close.

                bool followingWaypoint = moving ? Vector3.Distance(cmd.m_target.v3Target,RunWaypoint.Value) < 0.15f :

                    cmd?.m_skill == null && Vector3.Distance(crew.transform.position,RunWaypoint.Value) < 0.25f;

                if (followingWaypoint && ConfirmedRunFollow.Keep(RunWaypoint.Value,target,runHeading,hostStep))

                    needsMove = false;

            }

        }

        // Ordinary follow tolerates a small position error. At a cover boundary

        // even that error can leave one B visible while the other is concealed.

        char localSurface = confirmed ? GuestGroundTarget.SurfaceCode(crew) : 'x';

        bool matchShore = hostSurface == 's' || hostSurface == 'd' ||

            hostSurface == 'l' && (localSurface == 's' || localSurface == 'd');

        bool wrongShore = matchShore && hostSurface != localSurface && hostStep.sqrMagnitude <= 0.0004f;

        bool correctEdge = !needsMove && (edgeMismatch || wrongShore) && distance > 0.035f && distance <= 0.3f &&

            (!moving || retarget > 0.035f) && Time.realtimeSinceStartup >= nextEdgeCorrection;

        if (correctEdge) nextEdgeCorrection = Time.realtimeSinceStartup + 0.4f;

        int mask = matchShore ? 1 << (int)(hostSurface=='s' ? MiArea.ShallowWater : hostSurface=='d' ? MiArea.DeepWater : MiArea.Walkable) : -1;

        bool waterSurface=hostSurface=='s' || hostSurface=='d';

        if ((needsMove || correctEdge) && NavMesh.SamplePosition(target, out var hit, waterSurface?2f:correctEdge ? 0.3f : 1.5f, mask) &&

            (waterSurface ? GuestGroundTarget.WaterProjection(target,hit.position) : !matchShore || Math.Abs(hit.position.y-target.y)<=0.45f) &&

            (!correctEdge || !edgeMismatch || HidingTrigger.bPointInHiding(hit.position) == inside))

        {

            if (distance > 5f && relocate)

            {

                crew.warp(hit.position, crew.transform.rotation);

                if (confirmed && moving) NativeGait.Execute(crew,hit.position,NativeGait.AvailableTravel(crew,style=="R"?"RW":style=="C"?"GC":"GW"),true);

            }

            else if (confirmed)

            {

                bool run = style == "R" && NativeGait.CanRun(crew);

                string gait = NativeGait.AvailableTravel(crew,run ? "RW" : style == "C" ? "GC" : "GW");

                if (NativeGait.Execute(crew, hit.position, gait,true))

                {

                    Previous = Last;

                    Last = hit.position;

                    RunWaypoint = run ? hit.position : (Vector3?)null;

                    runHeading = hostStep.normalized;

                }

            }

            else

                NativeGait.Execute(crew,hit.position,NativeGait.AvailableTravel(crew,style=="R"?"RW":style=="C"?"GC":"GW"),true);

        }

        NativeGait.Apply(crew, style);

    }
}
