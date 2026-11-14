using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

// A snapshot owns the four alert markers only while its NPC decision lease is
// live. Native local perception must not undo it between network snapshots.
internal sealed class GuardIcons
{
    private sealed class Entry
    {
        internal GuardIcons owner;
        internal MiCharacter actor;
        internal int mask;
        internal float until;
    }
    private static readonly Dictionary<IntPtr,Entry> entries = new Dictionary<IntPtr,Entry>();
    private readonly Func<MiCharacter,bool> owns;
    private readonly List<IntPtr> remove = new List<IntPtr>();
    internal GuardIcons(Func<MiCharacter,bool> owns) { this.owns=owns; }
    internal void Record(MiCharacter actor,int mask)
    {
        if(actor==null) return;
        if(!owns(actor)) { entries.Remove(actor.Pointer); return; }
        if(!entries.TryGetValue(actor.Pointer,out var entry) || entry.owner!=this)
            entries[actor.Pointer]=entry=new Entry {owner=this,actor=actor};
        entry.mask=mask;entry.until=Time.realtimeSinceStartup+2f;
    }
    internal void Release()
    {
        remove.Clear();
        foreach(var pair in entries) if(pair.Value.owner==this) remove.Add(pair.Key);
        foreach(var key in remove) entries.Remove(key);
    }
    internal void Tick()
    {
        remove.Clear();
        foreach(var pair in entries)
            if(pair.Value.owner==this && !Valid(pair.Value)) remove.Add(pair.Key);
        foreach(var key in remove) entries.Remove(key);
    }
    private static bool Valid(Entry entry) => entry.actor!=null &&
        entry.actor.health?.eHealthState==MiCharacterHealth.HealthState.Alive &&
        Time.realtimeSinceStartup<=entry.until && entry.owner.owns(entry.actor);
    private static bool Wanted(MiCharacterWorldInfoBatchHandler handler,UIWorldInfoBatchIconID icon,out bool wanted)
    {
        wanted=false;
        if(entries.Count==0)return false;
        int bit=icon switch {
            UIWorldInfoBatchIconID.Alarm=>0, UIWorldInfoBatchIconID.Investigate=>1,
            UIWorldInfoBatchIconID.AlarmingAction=>2, UIWorldInfoBatchIconID.AlarmBell=>3, _=>-1
        };
        if(bit<0)return false;
        var actor=handler?.m_character;
        if(actor==null || !entries.TryGetValue(actor.Pointer,out var entry) || !Valid(entry)) return false;
        wanted=(entry.mask&(1<<bit))!=0; return true;
    }
    [HarmonyPatch(typeof(MiCharacterWorldInfoBatchHandler),"showIcon")]
    internal static class Show
    {
        internal static bool Prefix(MiCharacterWorldInfoBatchHandler __instance,UIWorldInfoBatchIconID __0,ref UIWorldInfoBatchIconData __result)
        {
            if(!Wanted(__instance,__0,out bool wanted)) return true;
            var data=__instance.getIconData(__0);
            if(wanted && data?.eState!=UIData.State.Connected) return true;
            // Native callers may configure the returned object. Preserve the
            // non-null contract even when the host says to keep it hidden.
            __result=data ?? __instance.getOrCreateIconData(__0);
            return false;
        }
    }
    [HarmonyPatch(typeof(MiCharacterWorldInfoBatchHandler),"hideIcon")]
    internal static class Hide
    {
        internal static bool Prefix(MiCharacterWorldInfoBatchHandler __instance,UIWorldInfoBatchIconID __0)
        {
            if(!Wanted(__instance,__0,out bool wanted)) return true;
            return !wanted && __instance.getIconData(__0)?.eState==UIData.State.Connected;
        }
    }
}
