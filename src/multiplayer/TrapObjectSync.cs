using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using HarmonyLib;

// Host pool slots are stable identities. Guest pool ordering and native
// allocation addresses may differ, so matching/adoption is explicit.
internal sealed class TrapObjectSync
{
    private sealed class Stamp{internal IntPtr Pointer;internal int Generation;internal string State;internal bool Armed;}
    private readonly Dictionary<string,Stamp> stamps=new(StringComparer.Ordinal);
    private readonly Dictionary<string,Dictionary<int,MiPossessableLineTrap>> replicas=new(StringComparer.Ordinal);
    private readonly Dictionary<string,float> wait=new(StringComparer.Ordinal);
    private readonly Dictionary<string,Dictionary<IntPtr,int>> hostSlots=new(StringComparer.Ordinal);
    private float nextApply,nextError;
    private static string Code=>InteractionProtocol.Code("LineTrapKill");
    internal void Clear()
    {
        foreach(var pool in replicas.Values)foreach(var item in pool.Values)
            try{if(item!=null&&item.m_colActivator!=null)item.m_colActivator.enabled=item.m_bActive;}catch{}
        stamps.Clear();replicas.Clear();wait.Clear();hostSlots.Clear();nextApply=nextError=0;
    }
    private static SkillThrowBallisticLineTrapBase Ability(MiCharacter actor,bool knockout=false)=>
        actor?.controller?.getSkill(knockout?Skill.SkillType.LineTrapKnockout:Skill.SkillType.LineTrapKill)?.TryCast<SkillThrowBallisticLineTrapBase>();
    private static bool Casting(MiCharacter actor)
    {
        foreach(bool knockout in new[]{false,true})
        {
            var skill=Ability(actor,knockout);if(skill==null)continue;
            if(actor.controller.skillCommandProcess?.m_skill==skill||CoroWait.bIsRunning(skill.m_cwThrow)||skill.flying!=null&&skill.flying.bIsActive())return true;
        }
        return false;
    }
    private static MiCharacter Holder(MiPossessable item,IReadOnlyList<MiCharacter> crew)
    {
        if(item==null)return null;if(item.charNPCOwner!=null)return item.charNPCOwner.character;
        foreach(var actor in crew)if(actor?.inventory?.bHasPossessable(item)==true)return actor;
        return null;
    }
    internal List<TrapProtocol.Group> Capture(IReadOnlyList<MiCharacter> crew)
    {
        var groups=new List<TrapProtocol.Group>();
        foreach(var actor in crew)try
        {
            var provider=(Ability(actor)??Ability(actor,true))?.m_skillLineTrapProvider;
            var pool=provider?.m_liPossessables;if(pool==null)continue;
            if(pool.Count>TrapProtocol.MaxSlots)throw new InvalidOperationException("Native trap pool exceeds wire limit");
            string source=GuestMove.Identity(actor);var rows=new List<string[]>();
            if(!hostSlots.TryGetValue(source,out var slots))hostSlots.Add(source,slots=new());
            var live=new HashSet<IntPtr>();
            for(int i=0;i<pool.Count;i++)
            {
                var item=pool[i]?.possessable;
                if(item==null||!live.Add(item.Pointer))throw new InvalidOperationException("Trap pool incomplete or duplicated");
            }
            foreach(var pair in new Dictionary<IntPtr,int>(slots))if(!live.Contains(pair.Key))
            {SkillObjectRegistry.Active.Remove(source,Code,pair.Value);slots.Remove(pair.Key);}
            var allocated=new HashSet<int>(slots.Values);
            foreach(var pointer in live)if(!slots.ContainsKey(pointer))
            {int index=1;while(allocated.Contains(index))index++;allocated.Add(index);slots.Add(pointer,index);}
            for(int i=0;i<pool.Count;i++)
            {
                var data=pool[i];var trap=data?.possessable?.TryCast<MiPossessableLineTrap>();
                if(data?.possessable!=null&&trap==null)throw new InvalidOperationException("Unexpected trap pool item type");
                var holder=Holder(trap,crew);
                string state=holder!=null?"I":trap!=null&&data.bPlaced&&trap.gameObject.activeInHierarchy?"W":"N";
                var options=state=="W"?trap.m_lineTrapOptionsCurrent:null;
                if(state=="W"&&options==null)throw new InvalidOperationException("Trap has no native endpoints");
                bool armed=state=="W"&&trap.m_bActive;
                int slotNumber=slots[trap.Pointer];
                string slot=source+":"+slotNumber.ToString(CultureInfo.InvariantCulture);
                if(!stamps.TryGetValue(slot,out var stamp))stamps.Add(slot,stamp=new Stamp());
                IntPtr pointer=trap!=null?trap.Pointer:IntPtr.Zero;
                // Rearming a pooled object creates a new incarnation even if
                // the native pointer stayed the same. Stale retrieval targets
                // must never refer to this new placement.
                if(pointer!=stamp.Pointer||state!="N"&&(stamp.Generation==0||stamp.State=="N"||armed&&!stamp.Armed))stamp.Generation=checked(stamp.Generation+1);
                stamp.Pointer=pointer;stamp.State=state;stamp.Armed=armed;
                string owner=holder==null?"-":InteractionSync.EntityIdentity(holder);if(owner==null)throw new InvalidOperationException("Unknown trap holder");
                Vector3 origin=options?.v3Origin??default,target=options?.v3Target??default;
                string row=string.Format(CultureInfo.InvariantCulture,"{0} {1} {2} {3} {4} {5} {6:0.###} {7:0.###} {8:0.###} {9:0.###} {10:0.###} {11:0.###}",
                    slotNumber,stamp.Generation,state,options==null?0:(int)options.eImpactType,armed?1:0,owner,origin.x,origin.y,origin.z,target.x,target.y,target.z);
                var fields=row.Split(' ');if(!TrapProtocol.Row(fields))throw new InvalidOperationException("Invalid native trap state");
                if(state=="N")SkillObjectRegistry.Active.Remove(source,Code,slotNumber);
                else SkillObjectRegistry.Active.Bind(source,Code,stamp.Generation,trap,slotNumber);
                rows.Add(fields);
            }
            groups.Add(new TrapProtocol.Group(source,Casting(actor),rows.ToArray()));
        }
        catch(Exception ex){Report(ex);}
        return groups;
    }
    private static bool Matches(MiPossessableLineTrap item,string[] row)
    {
        var options=item?.m_lineTrapOptionsCurrent;
        return options!=null&&(int)options.eImpactType==int.Parse(row[3],CultureInfo.InvariantCulture)&&
            Vector3.Distance(options.v3Origin,CrewSync.ReadPosition(row,6))<0.08f&&Vector3.Distance(options.v3Target,CrewSync.ReadPosition(row,9))<0.08f;
    }
    private static void Retire(SkillLineTrapProvider provider,SkillLineTrapProvider.LineTrapData data,IReadOnlyList<MiCharacter> crew)
    {
        var item=data?.possessable?.TryCast<MiPossessableLineTrap>();if(item==null)return;
        var holder=Holder(item,crew);
        if(holder?.inventory?.bHasPossessable(item)==true)holder.inventory.removeAndDeactivate(item);
        else if(item.charNPCOwner!=null)item.free(item.charNPCOwner);
        item.m_bActive=false;if(item.m_colActivator!=null)item.m_colActivator.enabled=false;
        if(item.gameObject.activeInHierarchy)item.setActive(false);
        data.bPlaced=false;provider.updateUIDisableOffscreenMarker(data);
    }
    private static SkillLineTrapProvider.LineTrapData Find(SkillLineTrapProvider provider,MiPossessableLineTrap item)
    {
        if(item==null)return null;var pool=provider.m_liPossessables;
        for(int i=0;i<pool.Count;i++)if(pool[i]?.possessable==item)return pool[i];return null;
    }
    internal void Apply(TrapProtocol.Group[] groups,IReadOnlyList<MiCharacter> crew)
    {
        if(groups==null||Time.realtimeSinceStartup<nextApply)return;nextApply=Time.realtimeSinceStartup+.1f;
        foreach(var group in groups)try
        {
            var actor=MoveProbe.CrewById(group.Source);var provider=(Ability(actor)??Ability(actor,true))?.m_skillLineTrapProvider;
            if(provider?.m_liPossessables==null||actor.inventory==null||group.Pending)continue;
            if(Casting(actor))
            {
                if(!wait.TryGetValue(group.Source,out float started))wait[group.Source]=started=Time.realtimeSinceStartup;
                if(Time.realtimeSinceStartup-started<3f)continue;
            }
            else wait.Remove(group.Source);
            // Resolve prerequisites before mutating any member of this pool.
            bool missing=false;
            foreach(var row in group.Rows)if(row[2]=="I"&&InteractionSync.Resolve(row[5])?.TryCast<MiCharacter>()==null||
                row[2]=="W"&&Ability(actor,row[3]=="1")==null)missing=true;
            if(missing)continue;
            if(!replicas.TryGetValue(group.Source,out var bound))replicas.Add(group.Source,bound=new());
            var assigned=new Dictionary<int,MiPossessableLineTrap>();var used=new HashSet<IntPtr>();
            // Keep established bindings first. Match native casts by geometry
            // only for currently unbound slots; never steal an assigned item.
            foreach(var row in group.Rows)
            {
                int slot=int.Parse(row[0],CultureInfo.InvariantCulture);
                if(row[2]!="N"&&bound.TryGetValue(slot,out var item)&&Find(provider,item)!=null&&used.Add(item.Pointer))assigned[slot]=item;
            }
            foreach(var row in group.Rows)
            {
                int slot=int.Parse(row[0],CultureInfo.InvariantCulture);if(row[2]=="N"||assigned.ContainsKey(slot))continue;
                var pool=provider.m_liPossessables;
                for(int i=0;i<pool.Count;i++)
                {
                    var item=pool[i]?.possessable?.TryCast<MiPossessableLineTrap>();if(item==null||used.Contains(item.Pointer))continue;
                    bool match=row[2]=="W"?pool[i].bPlaced&&Matches(item,row):Holder(item,crew)==InteractionSync.Resolve(row[5]);
                    if(!match)continue;assigned[slot]=item;used.Add(item.Pointer);break;
                }
            }
            // The group is complete. Retire local extra placements before
            // asking the native provider for a free item; it may reuse pools.
            for(int i=0;i<provider.m_liPossessables.Count;i++)
            {
                var data=provider.m_liPossessables[i];if(data?.possessable!=null&&!used.Contains(data.possessable.Pointer))Retire(provider,data,crew);
            }
            foreach(int slot in bound.Keys)if(!assigned.ContainsKey(slot))SkillObjectRegistry.Active.Remove(group.Source,Code,slot);
            bound.Clear();foreach(var pair in assigned)bound[pair.Key]=pair.Value;
            foreach(var row in group.Rows)
            {
                int slot=int.Parse(row[0],CultureInfo.InvariantCulture);
                if(row[2]=="N"){SkillObjectRegistry.Active.Remove(group.Source,Code,slot);continue;}
                assigned.TryGetValue(slot,out var item);var data=Find(provider,item);
                if(data==null)
                {
                    // Native getFreeOrPooledPossessable rotates the pool and
                    // steals the oldest placed trap when full. Use a verified
                    // unassigned pooled item instead. init() has already built
                    // this pool; an empty pool is not ready for reconciliation.
                    for(int i=0;i<provider.m_liPossessables.Count;i++)
                    {
                        var candidate=provider.m_liPossessables[i];var native=candidate?.possessable?.TryCast<MiPossessableLineTrap>();
                        if(native!=null&&!used.Contains(native.Pointer)){data=candidate;item=native;break;}
                    }
                    if(item==null||used.Contains(item.Pointer))continue;
                    used.Add(item.Pointer);bound[slot]=item;
                }
                var holder=Holder(item,crew);
                if(row[2]=="I")
                {
                    var desired=InteractionSync.Resolve(row[5])?.TryCast<MiCharacter>();
                    if(holder!=desired)
                    {
                        Retire(provider,data,crew);
                        if(desired.bControllerAI())item.onPossess(desired.controllerAI);else desired.inventory.insert(item,true);
                    }
                    data.bPlaced=false;item.m_bActive=false;
                }
                else
                {
                    var skill=Ability(actor,row[3]=="1");bool armed=row[4]=="1";
                    bool setup=!data.bPlaced||holder!=null||!item.gameObject.activeInHierarchy||!Matches(item,row)||armed&&!item.m_bActive;
                    if(setup)
                    {
                        var options=new MiCharacterInventory.ThrowOptions{m_v3Position=CrewSync.ReadPosition(row,6),m_qRotation=Quaternion.identity,m_bMoveToNavMesh=false,m_bTransfer=false};
                        if(item.charNPCOwner!=null)item.free(item.charNPCOwner);
                        if(holder?.inventory?.bHasPossessable(item)==true)holder.inventory.throwPossessable(item,options);else actor.inventory.unparentAndActivate(item,options);
                        item.init(new MiPossessableLineTrap.LineTrapOptions{v3Origin=CrewSync.ReadPosition(row,6),v3Target=CrewSync.ReadPosition(row,9),
                            eImpactType=(MiPossessableLineTrap.ImpactType)int.Parse(row[3],CultureInfo.InvariantCulture),skillLineTrap=skill});
                        item.onFlyingHit();
                        data.bPlaced=true;
                    }
                    if(!armed&&(setup||item.m_bActive))
                    {
                        // Only settle visuals. Calling tryImpact here would
                        // execute damage and noise a second time on the guest.
                        item.m_objectAnimationFirst?.playAction(item.m_actionTriggeredState);
                        item.m_objectAnimationSecond?.playAction(item.m_actionTriggeredState);
                    }
                    item.m_bActive=armed;
                }
                // Guest traps display host results; collision outcomes belong
                // to the host. The Prefix also covers the initial cast window.
                if(item.m_colActivator!=null)item.m_colActivator.enabled=false;
                SkillObjectRegistry.Active.Bind(group.Source,Code,int.Parse(row[1],CultureInfo.InvariantCulture),item,slot);
            }
            provider.reloadUI();
        }
        catch(Exception ex){Report(ex);}
    }
    [HarmonyPatch(typeof(MiPossessableLineTrap),"tryImpact")]
    internal static class HostImpact
    {
        private static bool Prefix(MiPossessableLineTrap __instance,ref bool __result)
        {
            if(NativeInput.Probe?.NativeGuest!=true||__instance?.m_lineTrapOptionsCurrent?.skillLineTrap==null)return true;
            __result=false;return false;
        }
    }
    private void Report(Exception ex)
    {
        if(Time.realtimeSinceStartup<nextError)return;nextError=Time.realtimeSinceStartup+2f;
        MoveProbePlugin.ProbeLog.LogWarning("Trap results: "+ex.GetType().Name+": "+ex.Message);
    }
}
