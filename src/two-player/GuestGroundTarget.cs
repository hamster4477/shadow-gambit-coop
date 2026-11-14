using System;
using UnityEngine;
using UnityEngine.AI;

internal static class GuestGroundTarget
{
    internal static bool Interactive(Entity entity) =>
        entity != null && (entity.bAsCharacter(out _) || entity.bAsUsable(out var usable)&&usable!=null&&!usable.bDisabled&&!(usable.bOnce&&usable.bUsed));


    internal static bool FoliageDestination(MiCharacter actor, Entity entity) =>
        entity != null && !entity.bAsCharacter(out _) && entity.bAsUsable(out var usable) &&
        usable != null && usable.GetComponent<MiUsableCorpseHidingFoliage>() != null &&
        actor.carry?.bIsCarryingCharacter != true;

    private static string Surface(int mask) => mask == 1 << (int)MiArea.ShallowWater ? "SHALLOW" :
        mask == 1 << (int)MiArea.DeepWater ? "DEEP" : mask == 1 << (int)MiArea.Walkable ? "LAND" : null;

    internal static char SurfaceCode(MiCharacter actor) => Surface(actor.iNavAreaFlag) switch {
        "LAND" => 'l', "SHALLOW" => 's', "DEEP" => 'd', _ => 'x'
    };

    // Classify the actual per-actor endpoint, not the shared mouse ray or the
    // actor's current shore. Keep the native formation point unchanged.
    internal static string DestinationSurface(MiCharacter actor,Vector3 point)
    {
        var nav=actor?.navigation;
        if(nav==null || !NavMesh.SamplePositionFilter(point,out var hit,0.12f,nav.agentType,nav.areaMask) ||
            Vector3.Distance(point,hit.position)>0.12f)return null;
        return Surface(hit.mask);
    }

    internal static Vector3 NavigationPosition(MiCharacter actor)
        => NavigationPoint(actor,actor.transform.position);
    internal static Vector3 NavigationPoint(MiCharacter actor,Vector3 position)
    {
        char surface=SurfaceCode(actor);
        if((surface=='s' || surface=='d') && actor.navigation!=null &&
            NavMesh.SamplePositionFilter(position,out var hit,2f,actor.navigation.agentType,
                1 << (int)(surface=='s'?MiArea.ShallowWater:MiArea.DeepWater)) &&
            WaterProjection(position,hit.position)) return hit.position;
        return position;
    }
    internal static bool WaterProjection(Vector3 origin,Vector3 projected)
    {
        float dx=origin.x-projected.x,dz=origin.z-projected.z;
        return dx*dx+dz*dz<=0.1225f && Math.Abs(origin.y-projected.y)<=2f;
    }

    internal static bool SurfaceFloor(MiCharacter actor, string surface, Vector3 near, out Vector3 point)
    {
        point = default;
        int area = surface switch { "LAND" => (int)MiArea.Walkable, "SHALLOW" => (int)MiArea.ShallowWater, "DEEP" => (int)MiArea.DeepWater, _ => -1 };
        if (area < 0) return false;
        int mask = 1 << area;
        if (actor.navigation == null || (actor.navigation.areaMask & mask) == 0 ||
            !NavMesh.SamplePositionFilter(near,out var floor,0.65f,actor.navigation.agentType,mask) || Math.Abs(floor.position.y-near.y)>0.45f) return false;
        point = floor.position;
        return true;
    }

    internal static bool TerrainEntity(MiCharacter actor, Entity entity)
    {
        var cache = actor.controllerPlayer?.m_playerInput?.m_raycastCache;
        if (cache == null || cache.bMouseOverNPC || cache.bMouseOverSelectable) return false;
        if (cache.bMouseOverHiding && FoliageDestination(actor,entity)) return true;
        string surface = Surface(cache.iWalkableLayer);
        return (surface == "SHALLOW" || surface == "DEEP") && actor.carry?.bIsCarryingCharacter != true &&
            entity != null && !entity.bAsCharacter(out _) && entity.bAsUsable(out var usable) &&
            usable != null && usable.GetComponent<MiUsableCorpseHidingWater>() != null;
    }

    internal static bool HidingFloor(Vector3 near, out Vector3 point)
    {
        point = default;
        if (!NavMesh.SamplePosition(near, out var floor, 1.5f, 1 << (int)MiArea.HidingSpot) ||
            !HidingTrigger.bPointInHiding(floor.position))
        {
            if (!NavMesh.SamplePosition(near, out floor, 1.5f, -1) || !HidingTrigger.bPointInHiding(floor.position))
                return false;
        }
        point = floor.position;
        return true;
    }

    private static bool HidingDestination(MiCharacter actor, Vector3 origin, Vector3 candidate, Ray ray,
        out Vector3 point, out string detail)
    {
        if (!HidingFloor(candidate, out point))
        {
            // A dynamic ThrowBush has cover geometry without a baked hiding area.
            foreach(var hitCover in Physics.RaycastAll(ray,1000f,-1,QueryTriggerInteraction.Collide))
            {
                if(hitCover.collider==null||hitCover.collider.GetComponentInParent<CreateCoverThrowBush>()==null||
                    Vector3.Distance(hitCover.point,candidate)>3f||actor.navigation==null)continue;
                if(NavMesh.SamplePositionFilter(candidate,out var coverFloor,1.5f,actor.navigation.agentType,actor.navigation.areaMask)&&
                    Math.Abs(coverFloor.position.y-candidate.y)<=.75f)
                {point=coverFloor.position;return WithinRange(origin,point,"dynamic cover floor; host validates",out detail);}
            }
            // On the rim the closest ordinary NavMesh point can lie outside
            // cover. Ask the clicked polygon for an interior point instead.
            detail = "no walkable point inside clicked foliage";
            const int hidingColliders = (1 << (int)MiLayer.HidingSpot) | (1 << (int)MiLayer.HidingSpotTrigger);
            if (!Physics.Raycast(ray, out var hit, 1000f, hidingColliders, QueryTriggerInteraction.Collide) ||
                Vector3.Distance(hit.point,candidate) > 3f) return false;
            var spot = hit.collider.GetComponentInParent<MiEditablePolyHidingSpot>();
            if (spot == null || !HidingFloor(spot.getClosestPointInside(hit.point), out point)) return false;
        }
        return WithinRange(origin, point, "walkable point inside foliage", out detail);
    }

    internal static bool ValidDestination(Vector3 point)=>
        float.IsFinite(point.x)&&float.IsFinite(point.y)&&float.IsFinite(point.z)&&
        Math.Abs(point.x)<=100000f&&Math.Abs(point.y)<=100000f&&Math.Abs(point.z)<=100000f;
    private static bool WithinRange(Vector3 actor, Vector3 point, string source, out string detail)
    {
        float distance = Vector3.Distance(actor, point);
        detail = $"{source} point={CrewSync.Position(point)} distance={distance:F2}";
        return float.IsFinite(distance) && ValidDestination(point);
    }

    private static bool VisibleFloor(RaycastHit surface, out NavMeshHit floor)
    {
        floor = default;
        return surface.normal.y >= 0.2f && NavMesh.SamplePosition(surface.point,out floor,0.65f,-1) &&
            Math.Abs(floor.position.y-surface.point.y) <= 0.45f;
    }

    private static bool IsCameraBatch(RaycastHit hit) => hit.collider != null &&
        hit.collider.name.StartsWith("batch_Camera_", StringComparison.OrdinalIgnoreCase);

    private static bool TryPhysicsSurface(Ray ray, out RaycastHit ground)
    {
        const int layersExceptIgnoreRaycast = ~(1 << 2);
        if (!Physics.Raycast(ray, out ground, 1000f, layersExceptIgnoreRaycast, QueryTriggerInteraction.Ignore))
            return false;
        if (!IsCameraBatch(ground)) return true;
        // RaycastAll is unordered. Ignore only camera batches, preserving the nearest
        // character, usable, wall or terrain behind them.
        float nearest = float.PositiveInfinity;
        foreach (var hit in Physics.RaycastAll(ray, 1000f, layersExceptIgnoreRaycast, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider == null || IsCameraBatch(hit) || hit.distance >= nearest) continue;
            nearest = hit.distance;
            ground = hit;
        }
        return float.IsFinite(nearest);
    }

    internal static bool TryPick(MiCharacter actor, bool leftClick, bool hasIssuedPoint, Vector3 issuedPoint,
        out Vector3 point, out string detail, bool retreat = false)
        => TryPick(actor,leftClick,hasIssuedPoint,issuedPoint,out point,out detail,out _,retreat);

    internal static bool TryPick(MiCharacter actor, bool leftClick, bool hasIssuedPoint, Vector3 issuedPoint,
        out Vector3 point, out string detail, out string surface, bool retreat, bool characterClick=false)
    {
        surface = null;
        point = issuedPoint;
        var origin = actor.transform.position;

        string cacheStatus = "no player raycast cache";
        Ray ray = default;
        bool hasRay = false;
        bool nativeReserve = false;
        var camera = Camera.main;
        try
        {
            var cache = actor.controllerPlayer?.m_playerInput?.m_raycastCache;
            if (cache != null)
            {
                int previousStamp = cache.m_iCachedFrame;
                cache.Recache();
                var candidate = cache.v3WalkableLayerRaycastHitPoint;
                ray = cache.ray;
                var gameCamera = cache.m_cam?.cam;
                if (gameCamera != null) camera = gameCamera;
                bool underPointer = false;
                if (camera != null)
                {
                    var screen = camera.WorldToScreenPoint(candidate);
                    var mouse = Input.mousePosition;
                    float dx = screen.x - mouse.x, dy = screen.y - mouse.y;
                    underPointer = screen.z > 0f && dx*dx + dy*dy <= 9f;
                }
                int stamp = cache.m_iCachedFrame;
                // Game cache stamps and Unity render frames have different origins.
                bool fresh = stamp >= 0 && (stamp == MiTime.frameCount || stamp != previousStamp || underPointer);
                cacheStatus = $"cache={stamp} game={MiTime.frameCount} unity={Time.frameCount} pixelMatch={underPointer} layer={cache.iWalkableLayer}";
                hasRay = fresh && ray.direction.sqrMagnitude > 0.001f;
                // The default left-click path may never construct GoTo when
                // it hits a person. The caller has already excluded explicit
                // skills, selection changes, drags and contextual actions.
                if(fresh && leftClick && characterClick)
                {
                    MiCharacter person=null;
                    if(cache.entity!=null)cache.entity.bAsCharacter(out person);
                    if(person==null && cache.bMouseOverNPC)person=cache.charNPC;
                    if(person==null && cache.bMouseOverSelectable)person=cache.selectable?.character;
                    if(person!=null)
                    {
                        if(hasIssuedPoint){point=issuedPoint;return WithinRange(origin,point,"native character click GoTo",out detail);}
                        if(hasRay && cache.iWalkableLayer>=0 && Vector3.Dot(candidate-ray.origin,ray.direction)>=0f && Vector3.Distance(candidate,ray.origin+ray.direction.normalized*Vector3.Dot(candidate-ray.origin,ray.direction.normalized))<=1.5f)
                        {point=candidate;surface=Surface(cache.iWalkableLayer);return WithinRange(origin,point,"ground beneath character pixel",out detail);}
                        detail="character click has no current walkable ray hit";return false;
                    }
                }
                if (hasRay && leftClick && cache.bMouseOverHiding && !cache.bMouseOverNPC && !cache.bMouseOverSelectable &&
                    (!Interactive(cache.entity) || FoliageDestination(actor, cache.entity)))
                {
                    if (hasIssuedPoint) { point=issuedPoint;
                        return WithinRange(origin, point, "native GoTo inside foliage; host validates", out detail); }
                    return HidingDestination(actor, origin, candidate, ray, out point, out detail);
                }
                if (fresh && leftClick && (cache.bMouseOverNPC || cache.bMouseOverSelectable ||
                    cache.bMouseOverHiding || Interactive(cache.entity) && !TerrainEntity(actor,cache.entity)))
                {
                    if (hasIssuedPoint && (!cache.bMouseOverHiding || cache.bMouseOverNPC ||
                        cache.bMouseOverSelectable || !FoliageDestination(actor,cache.entity)))
                    { point=issuedPoint; return WithinRange(origin,point,"game GoTo",out detail); }
                    // Ordinary movement may point at an unusable spot on a prop
                    // (e.g. a cooking pot). Keep the native ray destination and
                    // let host GoTo resolve the nearby reachable endpoint.
                    // Explicit skill targeting does not use characterClick.
                    if(characterClick&&hasRay&&underPointer&&cache.iWalkableLayer>=0&&
                        !cache.bMouseOverNPC&&!cache.bMouseOverSelectable&&!cache.bMouseOverHiding&&
                        cache.entity!=null&&!cache.entity.bAsCharacter(out _))
                    {point=candidate;return WithinRange(origin,point,"native prop movement; host resolves endpoint",out detail);}
                    detail = $"game cursor over reserved target (NPC={cache.bMouseOverNPC} selectable={cache.bMouseOverSelectable} hiding={cache.bMouseOverHiding} {cacheStatus})";
                    if (!retreat || camera == null) return false;
                    // The selection halo can cover the floor beside an actor. During
                    // withdrawal use the visible surface under the current pixel only.
                    if (!TryPhysicsSurface(camera.ScreenPointToRay(Input.mousePosition), out var visible))
                    { detail += "; no visible surface"; return false; }
                    if (Interactive(visible.collider.GetComponentInParent<Entity>()))
                    { detail += "; visible entity " + visible.collider.name; return false; }
                    if (!VisibleFloor(visible, out var floor))
                    {
                        if(visible.normal.y<0.2f) { detail += "; no close walkable point on " + visible.collider.name; return false; }
                        point=visible.point;
                        return WithinRange(origin,point,"retreat native nearest reachable ground",out detail);
                    }
                    point = floor.position;
                    return WithinRange(origin, point, "retreat visible floor", out detail);
                }
                var direction = ray.direction.normalized;
                float along = Vector3.Dot(candidate - ray.origin, direction);
                float offRay = Vector3.Distance(candidate, ray.origin + direction * along);
                if (hasRay && cache.iWalkableLayer >= 0 && along >= 0f && offRay <= 1.5f)
                {
                    point = hasIssuedPoint ? issuedPoint : candidate;
                    if (!WithinRange(origin, point, "game walkable cursor", out detail)) return false;
                    surface = Surface(cache.iWalkableLayer);
                    if (surface != null && (!hasIssuedPoint || Vector3.Distance(candidate,issuedPoint) <= 0.65f))
                    {
                        if (SurfaceFloor(actor,surface,point,out var sameSurface))
                        { point=sameSurface; return WithinRange(origin,point,"game surface "+surface,out detail); }
                        // Some guest missions have no local projection for a valid
                        // native cursor point. The host must still verify this exact layer.
                        detail += " (host to verify "+surface+")";
                        return true;
                    }
                    surface = null;
                    if (hasIssuedPoint) return WithinRange(origin,point,"game GoTo",out detail);
                    if (NavMesh.SamplePosition(point, out var projected, 1.5f, -1))
                    {
                        point = projected.position;
                        return WithinRange(origin, point, "game walkable cursor (" + cacheStatus + ")", out detail);
                    }
                    // Some missions only confirm this native point on the host. First
                    // try to find an actual locally walkable surface behind camera geometry.
                    nativeReserve = true;
                }
                cacheStatus += $" offRay={offRay:F2}";
            }
        }
        catch (Exception ex) { surface=null; cacheStatus = "cache read failed " + ex.GetType().Name; }

        if (hasIssuedPoint) { point=issuedPoint; return WithinRange(origin,point,"game GoTo",out detail); }

        if (!hasRay)
        {
            if (camera == null) { detail = cacheStatus + "; no active camera"; return false; }
            ray = camera.ScreenPointToRay(Input.mousePosition);
        }
        if (!TryPhysicsSurface(ray, out RaycastHit ground))
        {
            detail = cacheStatus + "; no non-camera surface";
            if (nativeReserve) detail = "game walkable cursor for host validation; " + detail;
            return nativeReserve;
        }
        if (leftClick && Interactive(ground.collider.GetComponentInParent<Entity>()))
        { detail = "physics hit character or usable"; return false; }
        if (!VisibleFloor(ground, out var hit))
        {
            // A visible terrain click can legitimately have a black marker.
            // Send its intention to the host's native GoTo resolver instead
            // of rejecting it solely because the exact pixel lacks NavMesh.
            // Keep character/usable and vertical-wall picks excluded here;
            // explicit native GoTo points above already take precedence.
            if(ground.normal.y>=0.2f)
            {
                point=ground.point;
                return WithinRange(origin,point,"native nearest reachable ground",out detail);
            }
            detail = $"{cacheStatus}; no NavMesh near {CrewSync.Position(ground.point)} collider={ground.collider.name}";
            if (nativeReserve) detail = "game walkable cursor for host validation; " + detail;
            return nativeReserve;
        }
        point = hit.position;
        return WithinRange(origin, point, "physics fallback (" + cacheStatus + ")", out detail);
    }
}
