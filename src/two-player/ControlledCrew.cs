using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

// Read native skill-owned controllers, not every player-controller in the
// scene. Ghost names/GUIDs may differ between machines. Their network slot is
// derived from their caster and native skill family; the local pointer is only
// an incarnation token used by the host to invalidate stale authority.
internal sealed class ControlledCrew
{
    internal static readonly ControlledCrew Active=new();
    internal sealed class Binding
    {
        internal string Id,Source,Kind,Incarnation,Parent;
        internal MiCharacter Actor,Caster;
        internal CrewOwnership.ControlledActor Authority=>new(Id,Source,Incarnation);
        internal ControlledCrewProtocol.Entry Manifest=>new(Id,Source,Kind,Kind is "C" or "O"?Melee.GuardId(Actor):Kind=="M"?MimicNative.Form(MimicNative.Ability(Caster),Actor).ToString(CultureInfo.InvariantCulture):"-");
    }
    private Dictionary<string,MiCharacter> sourcesById=new(StringComparer.Ordinal);
    internal MiCharacter Source(string id)=>id!=null&&sourcesById.TryGetValue(id,out var actor)?actor:null;
    private Dictionary<string,Binding> bindings=new(StringComparer.Ordinal);
    private Dictionary<IntPtr,Binding> byActor=new();
    internal IEnumerable<Binding> Bindings=>bindings.Values;
    internal Binding Find(string id)=>id!=null&&bindings.TryGetValue(id,out var found)?found:null;
    internal Binding Find(MiCharacter actor)=>actor!=null&&byActor.TryGetValue(actor.Pointer,out var found)&&found.Actor==actor?found:null;
    internal bool Releasable(MiCharacter actor)
    {
        if(Find(actor)!=null)return false;
        foreach(var binding in bindings.Values)if(binding.Caster==actor)return false;
        return true;
    }
    internal void Clear(){bindings.Clear();byActor.Clear();sourcesById.Clear();}
    internal static string Identity(string source,string kind)=>ControlledCrewProtocol.Identity(source,kind);
    internal static bool Kind(string value)=>ControlledCrewProtocol.Kind(value);
    // Guest aliases are authorized only by the current host manifest. A local
    // old possession of a different NPC cannot satisfy a new binding.
    internal void Restrict(ControlledCrewProtocol.Entry[] manifest)
    {
        var next=new Dictionary<string,Binding>(StringComparer.Ordinal);var actors=new Dictionary<IntPtr,Binding>();
        foreach(var entry in manifest)
            if(bindings.TryGetValue(entry.Id,out var binding)&&binding.Source==entry.Source&&binding.Kind==entry.Kind&&
                binding.Manifest.Target==entry.Target)
            {next.Add(entry.Id,binding);actors.Add(binding.Actor.Pointer,binding);}
        // A copied Pinkus may possess an NPC. Its ownership stays with the
        // original clown, but the local caster must be the confirmed copy,
        // including its form. An old copy cannot authorize a nested target.
        foreach(var binding in new List<Binding>(next.Values))
            if(binding.Parent!=null&&!next.ContainsKey(binding.Parent))
            {next.Remove(binding.Id);actors.Remove(binding.Actor.Pointer);}
        bindings=next;byActor=actors;
    }
    internal void Restrict(Func<Binding,bool> keep)
    {
        var next=new Dictionary<string,Binding>(StringComparer.Ordinal);var actors=new Dictionary<IntPtr,Binding>();
        foreach(var binding in bindings.Values)if(keep(binding)){next.Add(binding.Id,binding);actors.Add(binding.Actor.Pointer,binding);}
        bindings=next;byActor=actors;
    }

    // A complete, unambiguous sample replaces the previous one. No controller
    // changes, spawning, warping or native startControl calls happen here.
    internal bool Sample(IReadOnlyList<MiCharacter> sources,Func<MiCharacter,string> identity)
    {
        if(sources==null||identity==null||sources.Count>64)return false;
        var originals=new HashSet<IntPtr>();var sourceIds=new HashSet<string>(StringComparer.Ordinal);
        foreach(var source in sources)
        {
            if(source==null||!originals.Add(source.Pointer))return false;
            string id=identity(source);
            if(!SyncProtocol.Identity(id)||!sourceIds.Add(id))return false;
        }
        var next=new Dictionary<string,Binding>(StringComparer.Ordinal);
        var actors=new Dictionary<IntPtr,Binding>();
        foreach(var source in sources)
        {
            string id=identity(source);
            if(!SampleCaster(source,id,null))return false;
            var mimic=next.TryGetValue(Identity(id,"M"),out var parent)?parent:null;
            // Only the current native mimic can supply a second skill set.
            // Flatten ownership to the real roster member; retain the actual
            // caster for possession recovery and handoff back after release.
            if(mimic!=null&&mimic.Actor.health.eHealthState==MiCharacterHealth.HealthState.Alive&&
                !SampleCaster(mimic.Actor,id,mimic.Id))return false;
        }
        bool SampleCaster(MiCharacter source,string id,string parent)
        {
            foreach(string kind in families)
            {
                if(parent!=null&&kind=="M")continue;
                var actor=NativeActor(source,kind);
                if(actor==null||!actor.bControllerPlayer()||actor.health==null||
                    (kind!="M"&&actor.health.eHealthState!=MiCharacterHealth.HealthState.Alive)||!actor.gameObject.activeInHierarchy)continue;
                // Keep the current mimic in snapshots while dead: otherwise
                // neither its death nor its subsequent revival can reconcile.
                // Gameplay admission independently requires a living actor.
                // A skill acting on an actual party member must retain that
                // party member's ownership, never turn it into a borrowed slot.
                if(originals.Contains(actor.Pointer))continue;
                if(kind=="M"&&MimicNative.Form(MimicNative.Ability(source),actor)<0)continue;
                string alias=Identity(id,kind);
                if(actors.ContainsKey(actor.Pointer)||next.ContainsKey(alias)||next.Count+sources.Count>=64)return false;
                var binding=new Binding{Source=id,Kind=kind,Id=alias,Actor=actor,Caster=source,Parent=parent,
                    Incarnation=(parent==null?"":source.Pointer.ToInt64().ToString("X",CultureInfo.InvariantCulture)+"/")+
                        actor.Pointer.ToInt64().ToString("X",CultureInfo.InvariantCulture)};
                next.Add(binding.Id,binding);actors.Add(actor.Pointer,binding);
            }
            return true;
        }
        var sourceMap=new Dictionary<string,MiCharacter>(StringComparer.Ordinal);
        foreach(var source in sources)sourceMap.Add(identity(source),source);
        sourcesById=sourceMap;bindings=next;byActor=actors;return true;
    }
    internal static bool Transferred(MiCharacter source)
    {
        if(NativeActor(source,"O")!=null||NativeActor(source,"C")!=null)return true;
        var mimic=NativeActor(source,"M");
        return mimic!=null&&mimic!=source&&mimic.bControllerPlayer()&&mimic.gameObject.activeInHierarchy;
    }
    internal static bool NativeProxy(MiCharacter actor,MiCharacter source)
    {
        if(actor==null||source==null)return false;
        foreach(var kind in families)if(NativeActor(source,kind)==actor)return true;
        var mimic=NativeActor(source,"M");
        if(mimic!=null&&mimic!=source&&mimic.gameObject.activeInHierarchy&&
            MimicNative.Form(MimicNative.Ability(source),mimic)>=0)
            foreach(var kind in families)if(kind!="M"&&NativeActor(mimic,kind)==actor)return true;
        return false;
    }
    private static readonly string[] families={"C","O","H","G","M"};
    private static MiCharacter NativeActor(MiCharacter source,string kind)
    {
        var controller=source?.controller;if(controller==null)return null;
        if(kind=="C"||kind=="O")
        {
            var types=kind=="C"?new[]{Skill.SkillType.Control}:new[]{Skill.SkillType.Overtake,
                Skill.SkillType.KillOvertake,Skill.SkillType.KnockoutOvertake,Skill.SkillType.OvertakeByOvertaken};
            MiCharacter found=null;
            foreach(var type in types)
            {
                var skill=controller.getSkill(type)?.TryCast<SkillControl>();
                if(skill?.bInMindControl!=true||skill.m_charControlled==null)continue;
                // Variants can refer to the same native skill. Distinct live
                // targets under one family are ambiguous, never pick one.
                if(found!=null&&found!=skill.m_charControlled)throw new InvalidOperationException("Ambiguous native controlled target");
                found=skill.m_charControlled;
            }
            return found;
        }
        if(kind=="M")return controller.getSkill(Skill.SkillType.BeingATrickster)?.TryCast<SkillBeingATrickster>()?.charCurrentMimicry;
        if(kind=="H")
        {
            var skill=controller.getSkill(Skill.SkillType.CharmingGhost)?.TryCast<SkillCharmingGhost>();
            // Native return keeps the pooled object; m_bGhostAtChar is the
            // authoritative distinction between deployed and recalled ghost.
            return skill!=null&&!skill.m_bGhostAtChar?skill.m_charGhost:null;
        }
        if(kind=="G")return controller.getSkill(Skill.SkillType.GhostPlace)?.TryCast<SkillPlacePossessableGhost>()?.m_charGhost;
        return null;
    }
}
