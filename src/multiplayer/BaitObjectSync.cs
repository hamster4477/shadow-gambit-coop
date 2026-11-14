using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using HarmonyLib;

// Thrown reusable items retain native inventory/soft-link behaviour. Inventory
// ownership is an explicit state, not mistaken for a vanished world object.
internal sealed class BaitObjectSync
{
    private static readonly Dictionary<IntPtr,MiFlyingBallistic> flights=new();
    [HarmonyPatch(typeof(SkillThrowBallisticPossessable),"onThrow",new[]{typeof(MiFlyingBallistic)})]
    internal static class ThrowHook
    {private static void Postfix(SkillThrowBallisticPossessable __instance,MiFlyingBallistic __0){if(__instance!=null&&__0!=null)flights[__instance.Pointer]=__0;}}
    private readonly Dictionary<string,(IntPtr pointer,int generation,string state)> stamps=new(StringComparer.Ordinal);
    private readonly Dictionary<IntPtr,float> castWait=new();
    // All mimic forms share one network actor slot, but each native skill
    // owns a different retained item. Never recover a fish/head from another
    // cached form merely because it uses the same BaitDistract wire code.
    private readonly Dictionary<IntPtr,MiPossessable> known=new();
    private readonly Dictionary<string,string> reports=new(StringComparer.Ordinal);
    internal void Clear(){stamps.Clear();castWait.Clear();known.Clear();reports.Clear();flights.Clear();}
    private static MiFlyingBallistic Flight(SkillThrowBallisticPossessable skill)=>flights.TryGetValue(skill.Pointer,out var flight)?flight:skill.flying;
    private static SkillThrowBallisticPossessable Ability(MiCharacter actor,string code)=>
        actor?.controller?.getSkill(code=="BA"?Skill.SkillType.BaitDistract:Skill.SkillType.BaitTanuki)?.TryCast<SkillThrowBallisticPossessable>();
    private static bool Casting(MiCharacter actor,SkillThrowBallisticPossessable skill)=>
        CoroWait.bIsRunning(skill.m_cwThrow)||Flight(skill)!=null&&Flight(skill).bIsActive();
    internal static bool PrepareThrow(MiCharacter actor,Skill.SkillType kind)
    {
        if(kind!=Skill.SkillType.BaitDistract&&kind!=Skill.SkillType.BaitTanuki)return true;
        var skill=Ability(actor,kind==Skill.SkillType.BaitDistract?"BA":"BN");
        if(skill?.m_possessablePrefab==null||actor?.inventory==null)return false;
        if(actor.inventory.tryGetPossessable(skill.m_possessablePrefab.m_eType,out var held)&&held!=null)
        {skill.m_possessablePlaced=null;return true;}
        // A result page may have placed this same head before its delayed
        // throw cue. Return the original item to native inventory for replay,
        // rather than letting onThrow dereference a missing possessed item.
        var item=skill.m_possessablePlaced;
        if(item==null)item=skill.spawnPossessable(actor);
        if(item==null)return false;
        if(item.charNPCOwner!=null)item.free(item.charNPCOwner);
        actor.inventory.insert(item,true);skill.m_possessablePlaced=null;
        return actor.inventory.bHasPossessable(item);
    }
    private MiPossessable Item(MiCharacter actor,SkillThrowBallisticPossessable skill,string code)
    {
        var slot=skill.Pointer;
        if(skill.m_possessablePrefab!=null&&actor?.inventory!=null&&
            actor.inventory.tryGetPossessable(skill.m_possessablePrefab.m_eType,out var item))return known[slot]=item;
        if(skill.m_possessablePlaced!=null)return known[slot]=skill.m_possessablePlaced;
        // Native soft links survive saves and transfers to another inventory.
        // They identify this caster's item without guessing by world proximity
        // or taking another character's item with the same prefab type.
        var links=actor?.inventory?.m_liObjectsSoftLinked;
        MiPossessable linked=null;
        if(links!=null&&skill.m_possessablePrefab!=null)for(int i=0;i<links.Count;i++)
        {
            var candidate=links[i]?.TryCast<MiPossessable>();
            if(candidate==null||candidate.m_eType!=skill.m_possessablePrefab.m_eType)continue;
            if(linked!=null&&linked!=candidate)throw new InvalidOperationException("Ambiguous reusable item soft links");
            linked=candidate;
        }
        if(linked!=null)return known[slot]=linked;
        return known.TryGetValue(slot,out var previous)?previous:null;
    }
    private static MiCharacter Holder(MiPossessable item,IEnumerable<MiCharacter> crew)
    {
        if(item==null)return null;
        if(item.charNPCOwner!=null)return item.charNPCOwner.character;
        foreach(var actor in crew)if(actor?.inventory?.bHasPossessable(item)==true)return actor;
        return null;
    }
    internal List<string[]> Capture(IReadOnlyList<MiCharacter> crew)
    {
        var rows=new List<string[]>();
        foreach(var actor in crew)foreach(string code in new[]{"BA","BN"})
        {
            var skill=Ability(actor,code);if(skill==null||skill.m_possessablePrefab==null)continue;
            var item=Item(actor,skill,code);var owner=Holder(item,crew);
            string state=owner!=null?"I":item!=null&&item.gameObject.activeInHierarchy?"A":"N";
            string id=GuestMove.Identity(actor),slot=id+":"+code;
            var pointer=state=="N"?IntPtr.Zero:item.Pointer;
            stamps.TryGetValue(slot,out var stamp);
            if(stamp.pointer!=pointer||stamp.state!=state)stamp=(pointer,checked(stamp.generation+1),state);
            stamps[slot]=stamp;
            string target=owner==null?"-":InteractionSync.EntityIdentity(owner);
            if(target==null)continue;
            Vector3 point=state=="A"?item.transform.position:default;
            var row=string.Format(CultureInfo.InvariantCulture,"{0} {1} {2} {3} {4} - {5:0.###} {6:0.###} {7:0.###} {8}",
                id,code,stamp.generation,state,target,point.x,point.y,point.z,Casting(actor,skill)?4:0).Split(' ');
            if(!SkillObjectProtocol.Row(row))throw new InvalidOperationException("Invalid native reusable item state");
            if(state=="N")SkillObjectRegistry.Active.Remove(id,code);
            else SkillObjectRegistry.Active.Bind(id,code,stamp.generation,item);
            rows.Add(row);
        }
        return rows;
    }
    internal void Apply(string[] row,IReadOnlyList<MiCharacter> crew)
    {
        var actor=MoveProbe.CrewById(row[0]);var skill=Ability(actor,row[1]);
        SkillObjectRegistry.Active.Remove(row[0],row[1]);
        if(skill==null||actor.inventory==null||skill.m_possessablePrefab==null)return;
        string slot=row[0]+":"+row[1];
        string status=row[2]+"/"+row[3]+"/"+row[9];
        if(!reports.TryGetValue(slot,out var reported)||reported!=status)
        {
            reports[slot]=status;
            MoveProbePlugin.ProbeLog.LogInfo("Reusable item host "+slot+" state="+status+" point="+row[6]+","+row[7]+","+row[8]+" localCasting="+Casting(actor,skill));
        }
        if(row[9]=="4"){castWait.Remove(skill.Pointer);return;}
        if(Casting(actor,skill))
        {
            if(!castWait.TryGetValue(skill.Pointer,out float started))castWait[skill.Pointer]=started=Time.realtimeSinceStartup;
            if(Time.realtimeSinceStartup-started<1f)return;
            // A confirmed settled result must eventually win over a stuck
            // local projectile. Finish the same native object, never spawn a
            // second head while its original flight still owns it.
            var flight=Flight(skill);
            if(flight!=null&&flight.bIsActive())
            {
                if(row[3]=="A")
                {
                    var landing=CrewSync.ReadPosition(row,6);
                    flight.m_v3TargetPos=landing;
                    flight.m_v3CurrentPos=landing;
                    flight.hitExternal();
                }
                else flight.cancelExternal();
            }
            if(CoroWait.bIsRunning(skill.m_cwThrow))return;
        }
        else castWait.Remove(skill.Pointer);
        known.TryGetValue(skill.Pointer,out var previousItem);
        var item=Item(actor,skill,row[1]);
        var holder=Holder(item,crew);
        if(row[3]=="N")
        {
            if(item==null)return;
            if(holder==null&&!item.gameObject.activeInHierarchy&&skill.m_possessablePlaced==null)return;
            if(holder?.inventory?.bHasPossessable(item)==true)holder.inventory.removeAndDeactivate(item);
            else {if(item.charNPCOwner!=null)item.free(item.charNPCOwner);item.setActive(false);}
            if(skill.m_possessablePlaced==item)skill.m_possessablePlaced=null;
            skill.updateUI();return;
        }
        MiCharacter desiredOwner=null;
        if(row[3]=="I")
        {
            desiredOwner=InteractionSync.Resolve(row[4])?.TryCast<MiCharacter>();
            if(desiredOwner==null||desiredOwner.inventory==null&&desiredOwner.controllerAI==null)return;
        }
        if(item==null)
        {
            item=skill.spawnPossessable(actor);if(item==null)return;
            known[skill.Pointer]=item;
            // Some native variants return the retained pooled item and others
            // create it. Re-read ownership after the native factory.
            holder=Holder(item,crew);
        }
        if(row[3]=="I")
        {
            // A locally replayed pickup may already own an inventory item
            // while the old placed object remains in the scene. Item() gives
            // inventory priority; reconcile the separate placed reference too.
            var placed=skill.m_possessablePlaced;
            if(placed!=null&&placed!=item&&Holder(placed,crew)==null)
                placed.setActive(false);
            if(previousItem!=null&&previousItem!=item&&previousItem!=placed&&Holder(previousItem,crew)==null)
                previousItem.setActive(false);
            // Trace shows a picked-up head at its owner's position while
            // remaining active. Repair a stranded world attachment through
            // native remove/insert even when inventory membership arrived first.
            bool stranded=holder==desiredOwner&&!desiredOwner.bControllerAI()&&
                Vector3.Distance(item.transform.position,desiredOwner.transform.position)>.5f;
            if(holder!=desiredOwner||stranded)
            {
                if(item.charNPCOwner!=null)item.free(item.charNPCOwner);
                if(holder?.inventory?.bHasPossessable(item)==true)holder.inventory.removeAndDeactivate(item);
                if(desiredOwner.bControllerAI())item.onPossess(desiredOwner.controllerAI);
                else desiredOwner.inventory.insert(item,true);
            }
            // Native pickup keeps this same object active in inventory.
            // Do not deactivate the held item; only stale unowned duplicates
            // above are retired. Inventory.insert owns its attachment lifecycle.
            if(skill.m_possessablePlaced==item||placed!=null&&Holder(placed,crew)==null)skill.m_possessablePlaced=null;
            skill.updateUI();
        }
        else
        {
            var point=CrewSync.ReadPosition(row,6);
            bool newlyPlaced=holder!=null||!item.gameObject.activeInHierarchy||skill.m_possessablePlaced!=item;
            if(newlyPlaced)
            {
                var options=new MiCharacterInventory.ThrowOptions{m_v3Position=point,m_qRotation=Quaternion.identity,m_bMoveToNavMesh=false,m_bTransfer=false};
                if(item.charNPCOwner!=null)item.free(item.charNPCOwner);
                if(holder?.inventory?.bHasPossessable(item)==true)holder.inventory.throwPossessable(item,options);
                else actor.inventory.unparentAndActivate(item,options);
                // Native onThrow(MiFlyingBallistic) sets this reference before
                // invoking onThrow(MiPossessable); the latter does not set it.
                skill.m_possessablePlaced=item;
                skill.onThrow(item);item.onFlyingHit();
            }
            // Small native bob/idle motion is visual animation, not a route.
            // Do not replace it with ten discrete network teleports per second.
            if(newlyPlaced||Vector3.Distance(item.transform.position,point)>0.08f)item.transform.position=point;
        }
        SkillObjectRegistry.Active.Bind(row[0],row[1],int.Parse(row[2],CultureInfo.InvariantCulture),item);
    }
}
