using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

// Generated entity IDs are caster + skill slot + host generation. Scene
// sibling indices and per-process Unity instance IDs are not network keys.
internal sealed class SkillObjectRegistry
{
    internal static readonly SkillObjectRegistry Active=new();
    private sealed class Binding{internal string Id,Slot;internal Entity Entity;}
    private readonly Dictionary<string,Binding> slots=new(StringComparer.Ordinal);
    private readonly Dictionary<string,Binding> ids=new(StringComparer.Ordinal);
    private readonly Dictionary<IntPtr,Binding> objects=new();
    internal static string Identity(string source,string skill,int generation,int instance=0)
    {
        if(!SyncProtocol.Identity(source)||InteractionProtocol.Skill(skill)==null||generation<=0||instance<0||instance>64)return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("skill-object-v1\n"+source+"\n"+skill+"\n"+
            generation.ToString(CultureInfo.InvariantCulture)+(instance==0?"":"\n"+instance.ToString(CultureInfo.InvariantCulture)))));
    }
    internal void Clear(){slots.Clear();ids.Clear();objects.Clear();}
    internal void Remove(string source,string skill,int instance=0)
    {
        string slot=Slot(source,skill,instance);
        if(!slots.TryGetValue(slot,out var old))return;
        slots.Remove(slot);ids.Remove(old.Id);
        // The native object may have been destroyed since the last sample;
        // remove by the stored dictionary entry without dereferencing it.
        var remove=new List<IntPtr>();
        foreach(var pair in objects)if(pair.Value==old)remove.Add(pair.Key);
        foreach(var pointer in remove)objects.Remove(pointer);
    }
    private static string Slot(string source,string skill,int instance)=>source+":"+skill+":"+instance.ToString(CultureInfo.InvariantCulture);
    internal bool Bind(string source,string skill,int generation,Entity entity,int instance=0)
    {
        string id=Identity(source,skill,generation,instance),slot=Slot(source,skill,instance);
        if(id==null||entity==null)return false;
        if(objects.TryGetValue(entity.Pointer,out var other)&&other.Slot!=slot)return false;
        Remove(source,skill,instance);
        var binding=new Binding{Id=id,Slot=slot,Entity=entity};
        slots.Add(slot,binding);ids.Add(id,binding);objects.Add(entity.Pointer,binding);return true;
    }
    internal string Key(Entity entity)=>entity!=null&&objects.TryGetValue(entity.Pointer,out var binding)&&binding.Entity==entity?"o"+binding.Id:null;
    internal Entity Resolve(string id)=>id!=null&&ids.TryGetValue(id,out var binding)?binding.Entity:null;
    internal IEnumerable<Entity> Entities {get{foreach(var binding in ids.Values)yield return binding.Entity;}}
}
