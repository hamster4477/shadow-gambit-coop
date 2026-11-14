using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;

// K1 describes the present attachment. K2 preserves the victim consumed by
// one particular shot, even after the host's cannon has become empty.
internal static class CannonLaunchSync
{
    private sealed class Shot
    {
        internal MiCharacter Owner,Target;
        internal SkillCommand Command;
        internal string Variant;
        internal int Serial;
    }
    private static readonly Dictionary<IntPtr,Shot> observed=new();
    private static readonly Dictionary<string,string[]> confirmed=new(StringComparer.Ordinal);
    private static readonly Queue<string> history=new();
    private static readonly Dictionary<IntPtr,Shot> pending=new();
    private static readonly HashSet<string> consumed=new(StringComparer.Ordinal);
    private static string Key(string owner,int serial)=>owner+":"+serial.ToString(CultureInfo.InvariantCulture);
    internal static void Clear(){observed.Clear();confirmed.Clear();history.Clear();pending.Clear();consumed.Clear();}
    internal static int CapturedSerial(SkillCommand command)
    {
        if(command==null)return 0;
        foreach(var shot in observed.Values)if(shot.Command?.Pointer==command.Pointer)return shot.Serial;
        return 0;
    }
    private static SkillCannonLoad Cannon(MiCharacter owner)=>owner?.controller?.getSkill(Skill.SkillType.CannonLoad)?.TryCast<SkillCannonLoad>();
    internal static void Observe(SkillCannonShoot skill)
    {
        if(NativeInput.Probe?.NativeGuest==true)return;
        var target=skill?.m_skillCannonLoad?.charLoaded;
        var command=skill?.m_skillCommandThrow;
        if(target?.cannonInfo?.m_skillCannonLoad==null||command==null||skill.charOrigin==null)return;
        observed[skill.charOrigin.Pointer]=new Shot{Owner=skill.charOrigin,Target=target,Command=command,
            Variant=target.cannonInfo.m_skillCannonLoad.eSkillType.ToString()};
    }
    internal static List<string[]> Capture()
    {
        var result=new List<string[]>();
        foreach(var shot in observed.Values)
        {
            int serial=shot.Serial>0?shot.Serial:shot.Serial=InteractionSync.CommandSerial(shot.Command);if(serial<=0)continue;
            var row=new[]{InteractionSync.EntityIdentity(shot.Owner),InteractionSync.EntityIdentity(shot.Target),shot.Variant,serial.ToString(CultureInfo.InvariantCulture)};
            if(CannonLaunchProtocol.Row(row))result.Add(row);
        }
        return result;
    }
    internal static void Apply(string[][] rows)
    {
        if(rows==null)return;
        foreach(var row in rows)
        {
            string key=Key(row[0],int.Parse(row[3],CultureInfo.InvariantCulture));
            // A command's victim is immutable. Repeated snapshots must not
            // change the shot that a locally queued command will consume.
            if(confirmed.ContainsKey(key))continue;
            confirmed[key]=(string[])row.Clone();history.Enqueue(key);
        }
        while(history.Count>128){string key=history.Dequeue();confirmed.Remove(key);consumed.Remove(key);}
    }
    internal static bool Prepare(MiCharacter owner,Skill.SkillType kind,int serial)
    {
        if(kind!=Skill.SkillType.CannonShoot||NativeInput.Probe?.NativeGuest!=true)return true;
        string identity=InteractionSync.EntityIdentity(owner);
        // A whole missed shot is reconciled by current state snapshots. Never
        // reload its historical victim after its terminal record has arrived.
        if(CannonFlightSync.Terminal(identity,serial))return false;
        string key=Key(identity,serial);
        if(serial<=0||consumed.Contains(key)||!confirmed.TryGetValue(key,out var row))return false;
        var target=InteractionSync.Resolve(row[1])?.TryCast<MiCharacter>();
        return target!=null&&CannonStateSync.RestoreShot(owner,target,row[2]);
    }
    internal static void Arm(MiCharacter owner,SkillCommand command,int serial)
    {
        if(NativeInput.Probe?.NativeGuest!=true||command?.m_skill?.eSkillType!=Skill.SkillType.CannonShoot)return;
        if(!confirmed.TryGetValue(Key(InteractionSync.EntityIdentity(owner),serial),out var row))return;
        var target=InteractionSync.Resolve(row[1])?.TryCast<MiCharacter>();if(target==null)return;
        pending[owner.Pointer]=new Shot{Owner=owner,Target=target,Command=command,Variant=row[2],Serial=serial};
    }
    private static bool Same(SkillCommand a,SkillCommand b)=>a!=null&&b!=null&&a.Pointer==b.Pointer;
    private static bool Queued(Shot shot)=>Same(shot.Owner.controllerPlayer?.skillCommand,shot.Command)||
        Same(shot.Owner.controller?.skillCommandActive,shot.Command)||Same(shot.Owner.controller?.skillCommandProcess,shot.Command);
    internal static bool AlreadyQueued(MiCharacter owner,int serial)=>owner!=null&&pending.TryGetValue(owner.Pointer,out var shot)&&shot.Serial==serial&&Queued(shot);
    internal static bool Deferred(MiCharacter target)
    {
        if(NativeInput.Probe?.NativeGuest!=true||target==null)return false;
        foreach(var shot in pending.Values)if(shot.Target.Pointer==target.Pointer&&Queued(shot)&&
            !CannonFlightSync.Terminal(InteractionSync.EntityIdentity(shot.Owner),shot.Serial))return true;
        return false;
    }
    internal static void CompleteVictim(MiCharacter target)
    {
        foreach(var pair in new List<KeyValuePair<IntPtr,Shot>>(pending))
            if(pair.Value.Target.Pointer==target.Pointer)
            {consumed.Add(Key(InteractionSync.EntityIdentity(pair.Value.Owner),pair.Value.Serial));pending.Remove(pair.Key);}
    }
    internal static bool Consume(SkillCannonLoad cannon)
    {
        if(cannon?.charOrigin==null||!pending.TryGetValue(cannon.charOrigin.Pointer,out var shot))return false;
        var shoot=cannon.charOrigin.controller.getSkill(Skill.SkillType.CannonShoot)?.TryCast<SkillCannonShoot>();
        if(!Same(shoot?.m_skillCommandThrow,shot.Command)||cannon.charLoaded?.Pointer!=shot.Target.Pointer)return false;
        consumed.Add(Key(InteractionSync.EntityIdentity(shot.Owner),shot.Serial));
        pending.Remove(cannon.charOrigin.Pointer);return true;
    }
    internal static bool BeforeThrow(SkillCannonShoot skill)
    {
        bool guest=NativeInput.Probe?.NativeGuest==true;
        try
        {
            if(!guest){Observe(skill);return true;}
            int serial=InteractionSync.CommandSerial(skill?.m_skillCommandThrow);
            if(skill?.charOrigin==null||!Prepare(skill.charOrigin,Skill.SkillType.CannonShoot,serial))return false;
            Arm(skill.charOrigin,skill.m_skillCommandThrow,serial);return true;
        }
        catch(Exception ex)
        {
            // An invalid late native reference must not escape the detour.
            MoveProbePlugin.ProbeLog.LogWarning("Cannon launch: "+ex.GetType().Name+" "+ex.Message);
            return !guest;
        }
    }
    [HarmonyPatch(typeof(SkillCannonShoot),"executeThrow")]
    internal static class ThrowHook
    {private static bool Prefix(SkillCannonShoot __instance)=>BeforeThrow(__instance);}
}
