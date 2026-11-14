using System;
using System.Globalization;
using System.Text.RegularExpressions;

// Wire validation only. No Unity API and no game side effects.
internal static class SyncProtocol
{
    internal static bool Session(string value) => value != null && Regex.IsMatch(value, "\\A[0-9a-f]{32}\\z");
    internal static bool Identity(string value) => value != null && Regex.IsMatch(value, "\\A[0-9A-F]{64}\\z");
    internal static bool Number(string value, out float number) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && float.IsFinite(number) && Math.Abs(number) <= 1000000f;
    internal static bool Position(string[] fields, int start) => Number(fields[start], out _) && Number(fields[start+1], out _) && Number(fields[start+2], out _);
    internal static bool SightPose(string token)
    {
        if(token==null||token.Length>600)return false;
        var parts=token.Split(':');if(parts.Length!=1&&parts.Length!=2&&parts.Length!=4||parts[0]!="x"&&!Number(parts[0],out _))return false;
        if(parts.Length==4&&(!Number(parts[2],out float range)||range<0||!int.TryParse(parts[3],out int focus)||focus< -2||focus>63))return false;
        if(parts.Length==1||parts[1]=="-")return true;
        var f=parts[1].Split('~');
        if(f.Length!=11||!int.TryParse(f[0],NumberStyles.None,CultureInfo.InvariantCulture,out int preset)||preset>3||
            !int.TryParse(f[8],NumberStyles.None,CultureInfo.InvariantCulture,out int height)||height>65535||!Number(f[9],out _)||!Number(f[10],out _))return false;
        for(int i=1;i<=7;i++)if(f[i]!="-"&&!Number(f[i],out _))return false;
        return true;
    }
    internal static bool Snapshot(string body, out string[] fields, out int sequence)
        => Snapshot(body, out fields, out sequence, out _, out _, out _);

    internal static bool Snapshot(string body, out string[] fields, out int sequence, out bool attacking, out string[] outcomes, out string ticket)
        => Snapshot(body, out fields, out sequence, out attacking, out outcomes, out ticket, out _);

    internal static bool Snapshot(string body, out string[] fields, out int sequence, out bool attacking, out string[] outcomes, out string ticket, out string[] combat)
        => Snapshot(body,out fields,out sequence,out attacking,out outcomes,out ticket,out combat,out _,out _);

    internal static bool Snapshot(string body, out string[] fields, out int sequence, out bool attacking, out string[] outcomes, out string ticket, out string[] combat, out int contact, out string contactGuard)
        => Snapshot(body,out fields,out sequence,out attacking,out outcomes,out ticket,out combat,out contact,out contactGuard,out _);

    internal static bool Snapshot(string body, out string[] fields, out int sequence, out bool attacking, out string[] outcomes, out string ticket, out string[] combat, out int contact, out string contactGuard,out string[] routePoint)
        => Snapshot(body,out fields,out sequence,out attacking,out outcomes,out ticket,out combat,out contact,out contactGuard,out routePoint,out _);

    internal static bool Snapshot(string body, out string[] fields, out int sequence, out bool attacking, out string[] outcomes, out string ticket, out string[] combat, out int contact, out string contactGuard,out string[] routePoint,out string[] bodies)
        => Snapshot(body,out fields,out sequence,out attacking,out outcomes,out ticket,out combat,out contact,out contactGuard,out routePoint,out bodies,out _);

    internal static bool Snapshot(string body, out string[] fields, out int sequence, out bool attacking, out string[] outcomes, out string ticket, out string[] combat, out int contact, out string contactGuard,out string[] routePoint,out string[] bodies,out string[] interactions)
        => Snapshot(body,out fields,out sequence,out attacking,out outcomes,out ticket,out combat,out contact,out contactGuard,out routePoint,out bodies,out interactions,out _);

    internal static bool Snapshot(string body, out string[] fields, out int sequence, out bool attacking, out string[] outcomes, out string ticket, out string[] combat, out int contact, out string contactGuard,out string[] routePoint,out string[] bodies,out string[] interactions,out string[] coverResults,string[] extraCrew=null,bool spectator=false)
    {
        coverResults=null;
        interactions=null;
        bodies=null;
        routePoint=null;
        contact = 0; contactGuard = null;
        fields = null; sequence = 0; attacking = false; outcomes = Array.Empty<string>(); ticket = null; combat = null;
        if (body == null || body.Length > 2048 || extraCrew!=null && (extraCrew.Length>62 || Array.Exists(extraCrew,id=>!Identity(id)))) return false;
        var f = body.Split(' ');
        if(f[0]=="S18")
        {
            int v=Array.IndexOf(f,"V");
            if(v<19 || f.Length<v+3 || !Regex.IsMatch(f[v+1],@"\A[1x]{2}\z") ||
                !int.TryParse(f[v+2],NumberStyles.None,CultureInfo.InvariantCulture,out int count) || count>8 || f.Length!=v+3+count*6)return false;
            var seen=new System.Collections.Generic.HashSet<string>();int a=0,b=0;
            for(int i=v+3;i<f.Length;i+=6)
            {
                if(!In(f[i],"A","B") || f[v+1][f[i]=="A"?0:1]!='1' || !GuardId(f[i+1]) ||
                    !seen.Add(f[i]+":"+f[i+1]) || !Position(f,i+2) || !Number(f[i+5],out float yaw) || yaw<0 || yaw>=360)return false;
                if(f[i]=="A")a++;else b++;
            }
            if(a>4 || b>4)return false;
            coverResults=f[(v+1)..];f=f[..v];
        }
        bool coverState = f[0] == "S11" || f[0] == "S12" || (f[0]=="S13" || (f[0]=="S14" || (f[0]=="S15" || (f[0]=="S16" || (f[0]=="S17" || f[0]=="S18")))));
        bool pairedContact = coverState || f[0] == "S9" || f[0] == "S10";
        if (pairedContact || In(f[0],"S4","S5","S6","S7","S8"))
        {
            int x = Array.IndexOf(f, "X");
            if (x < 0 || f.Length < x+20) return false;
            if (pairedContact || In(f[0],"S5","S6","S7","S8"))
            {
                int stride = (f[0]=="S17" || f[0]=="S18") ? 20 : f[0]=="S5" ? 14 : f[0]=="S6" ? 15 : 16;
                int header = stride>=15 ? 6 : 5;
                int p = x+20;
                if (f.Length < p+header || f[p] != "P" || !Health(f[p+1],f[p+2]) || !Health(f[p+3],f[p+4]) || (f.Length-p-header)%stride != 0 || f.Length > p+header+4*stride) return false;
                var seen = new System.Collections.Generic.HashSet<string>();
                if (header==6 && !Regex.IsMatch(f[p+5],@"\A(?:-|g-?[0-9]{1,10}|p[0-9A-F]{8})\z")) return false;
                for (int i=p+header; i<f.Length; i+=stride)
                    if (stride==20 && !GuardMotion(f,i+16) || stride>=16 && !Sight(f[i+15],extraCrew) || stride>=15 && !SightPose(f[i+14]) || !GuardId(f[i]) || !seen.Add(f[i]) || !In(f[i+1],"Idle","Alert","Suspicious","Distracted") ||
                        !CrewTarget(f[i+2],extraCrew) || !Position(f,i+3) || !Number(f[i+6],out float yaw) || yaw<0 || yaw>360 ||
                        !Health(f[i+7],"Alive") || !Position(f,i+8) || !In(f[i+11],"-","Search","Gun","GunGatling","Kill","Knockout","CounterMeleeKill","SnipeKill") ||
                        !int.TryParse(f[i+12],NumberStyles.None,CultureInfo.InvariantCulture,out int serial) || !CrewTarget(f[i+13],extraCrew) ||
                        (In(f[i+11],"-","Search") ? serial!=0 || f[i+13]!="-" : serial==0 || f[i+13]=="-")) return false;
            }
            else if (f.Length != x+20) return false;
            combat = f[(x+1)..];
            for (int i=0; i<=6; i+=6)
            {
                if (!int.TryParse(combat[i], NumberStyles.None,CultureInfo.InvariantCulture,out int serial) ||
                    (serial == 0 ? combat[i+1] != "-" || combat[i+2] != "0" :
                    !(EntitySkill(combat[i+1]) && GuardId(combat[i+2]) || PointSkill(combat[i+1]) && combat[i+2]=="0")) || !Position(combat,i+3)) return false;
            }
            if (!In(combat[12],"Unknown","None","Alarm","AlarmBellPrepare","AlarmWithReinforcements") ||
                !Position(combat,13) || !Position(combat,16)) return false;
            if ((f[0]=="S14" || (f[0]=="S15" || (f[0]=="S16" || (f[0]=="S17" || f[0]=="S18")))) && x>=4 && f[x-4]=="N")
            {
                if (!Position(f,x-3)) return false;
                routePoint=f[(x-3)..x];
                x-=4;
            }
            if((f[0]=="S16" || (f[0]=="S17" || f[0]=="S18")))
            {
                int ix=x-17;
                if(ix<19 || f[ix]!="I" || !InteractionProtocol.Record(f,ix+1) || !InteractionProtocol.Record(f,ix+9)) return false;
                interactions=f[(ix+1)..x];x=ix;
            }
            if ((f[0]=="S15" || (f[0]=="S16" || (f[0]=="S17" || f[0]=="S18"))))
            {
                // A's crouched style is also the token C. Extension tags are
                // only legal after the fixed crew/guard header and M section.
                int c=Array.IndexOf(f,"C",Math.Min(19,f.Length));
                if(c<0 || c+4>x || !int.TryParse(f[c+3],NumberStyles.None,CultureInfo.InvariantCulture,out int count) ||
                    count>2 || x!=c+4+count*5 || !CarryRoster(f[c+1]) || !CarryRoster(f[c+2])) return false;
                var owners=new System.Collections.Generic.HashSet<string>();
                foreach(string roster in new[]{f[c+1],f[c+2]})
                    if(roster!="-") foreach(string id in roster.Split(',')) if(!owners.Add(id)) return false;
                var ids=new System.Collections.Generic.HashSet<string>();
                for(int i=c+4;i<x;i+=5)
                    if(!GuardId(f[i]) || !ids.Add(f[i]) || !In(f[i+1],"K","D","HK","HD","KT","HKT","CD","CK","CKT") || !Position(f,i+2)) return false;
                bodies=f[(c+1)..x];
                x=c;
            }
            f = f[..x];
        }
        if (pairedContact || In(f[0],"S3","S4","S5","S6","S7","S8"))
        {
            int start = Array.IndexOf(f, "M");
            int size = pairedContact ? 5 : 3;
            if ((start != 14 && start != 21) || f.Length < start+size || f.Length > start+size+16 || !In(f[start+1],"0","1") || !Session(f[start+2])) return false;
            attacking = f[start+1] == "1";
            if (attacking && routePoint!=null) return false;
            ticket = f[start+2];
            if (pairedContact)
            {
                if (!int.TryParse(f[start+3],NumberStyles.None,CultureInfo.InvariantCulture,out contact) ||
                    (contact == 0 ? f[start+4] != "-" && !((coverState || f[0]=="S10") && !attacking && f[start+4]=="!") : !attacking || !GuardId(f[start+4]))) return false;
                if (contact > 0 || f[start+4]=="!") contactGuard = f[start+4];
            }
            outcomes = f[(start+size)..];
            var ids = new System.Collections.Generic.HashSet<string>();
            foreach (string item in outcomes)
                if (item.Length < 2 || (item[0] != 'K' && item[0] != 'D' && item[0] != 'A') || !GuardId(item.Substring(1)) || !ids.Add(item.Substring(1))) return false;
            f = f[..start];
        }
        if ((f.Length != 14 && f.Length != 21) || !(pairedContact || In(f[0], "S1", "S2", "S3","S4","S5","S6","S7","S8")) || !Session(f[1]) ||
            (f[2] != "RUN" && f[2] != "WAIT" && !(f[0]=="S18" && f[2]=="SCENE") && !((f[0]=="S13" || (f[0]=="S14" || (f[0]=="S15" || (f[0]=="S16" || (f[0]=="S17" || f[0]=="S18"))))) && f[2]=="HOLD")) || !int.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out sequence) || sequence <= 0 ||
            !Identity(f[4]) || !Identity(f[9]) || (spectator ? f[4]!=f[9] : f[4]==f[9]) || !Position(f,5) || !Position(f,10) ||
            !SnapshotStyle(f[0], f[8]) || !GuestStyle(f[0],f[13],pairedContact,coverState)) return false;
        if (f.Length == 21 && (f[14] != "G" || !GuardId(f[15]) || !Position(f,16) ||
            !In(f[19], "Idle", "Suspicious", "Alert", "Distracted") ||
            !In(f[20], "Alive", "Knockout", "Dying", "Dead", "Exterminated", "InProcessOfBeingKnockedOut", "PreDying", "PreKnockout", "Catatonic"))) return false;
        fields = f;
        return true;
    }
    private static bool CrewTarget(string token,string[] extraCrew) => In(token,"A","B","-") || extraCrew!=null && Array.IndexOf(extraCrew,token)>=0;
    private static bool Sight(string value,string[] extraCrew)
    {
        if(value.Length!=2+(extraCrew?.Length??0))return false;
        foreach(char c in value)if("012345x".IndexOf(c)<0)return false;
        return true;
    }
    private static bool GuardMotion(string[] f,int i) =>
        In(f[i],"0","1","2") &&
        int.TryParse(f[i+1],NumberStyles.None,CultureInfo.InvariantCulture,out int modifier) && modifier<=12 &&
        In(f[i+2],"0","1") &&
        int.TryParse(f[i+3],NumberStyles.None,CultureInfo.InvariantCulture,out int icons) && icons<=15;
    internal static bool Health(string hp,string state) =>
        int.TryParse(hp,NumberStyles.None,CultureInfo.InvariantCulture,out int n) && n <= 1000000 &&
        In(state,"Alive","Knockout","Dying","Dead","Exterminated","InProcessOfBeingKnockedOut","PreDying","PreKnockout","Catatonic");
    private static bool In(string value, params string[] allowed) => Array.IndexOf(allowed, value) >= 0;
    private static bool SnapshotStyle(string version, string value) =>
        version == "S1" ? In(value, "0", "1") : (version=="S16" || (version=="S17" || version=="S18")) ? value.Length==2 && "WCR".IndexOf(value[0])>=0 && "lsdx".IndexOf(value[1])>=0 : In(value, "W", "C", "R");
    private static bool GuestStyle(string version, string value, bool pairedContact, bool coverState)
    {
        if (version=="S12" || version=="S13" || (version=="S14" || (version=="S15" || (version=="S16" || (version=="S17" || version=="S18")))))
        {
            if (value.Length != 4 || "lsdx".IndexOf(value[3]) < 0) return false;
            value = value.Substring(0,3);
        }
        if (coverState)
        {
            if (value.Length != 3 || "ohix".IndexOf(value[2]) < 0) return false;
            value = value.Substring(0,2);
        }
        return pairedContact || version=="S8" ? In(value,"CC","CW","WC","WW","RC","RW") : SnapshotStyle(version,value);
    }
    internal static bool CarryRoster(string value)
    {
        if(value=="-") return true;
        var ids=value.Split(',');
        var seen=new System.Collections.Generic.HashSet<string>();
        if(ids.Length>4) return false;
        foreach(string id in ids) if(!GuardId(id) || !seen.Add(id)) return false;
        return true;
    }
    internal static bool PointSkill(string key) => In(key,"ThrowNoise","ThrowBush");
    internal static bool EntitySkill(string key) => In(key,"K","L","BlinkKill","BlinkKillLong","BlinkKnockout","BlinkKnockoutLong","SnipeKill","SnipeKnockout","Gun");
    internal static bool GroundMode(string mode) => In(mode, "GW", "GC", "RW", "RC");
    internal static bool GuardId(string value) => value != null && Regex.IsMatch(value, "\\A-?[0-9]{1,10}\\z") &&
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
    private static bool CrewSet(string text)
    {
        var ids=text.Split(',');if(ids.Length<1||ids.Length>64)return false;
        var seen=new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach(string id in ids)if(!Identity(id)||!seen.Add(id))return false;
        return true;
    }
    // A wheel result is actor-scoped and revisioned; never accept legacy
    // unversioned selection after a load or ownership transfer.
    internal static bool MimicForm(string text)=>text!=null&&text.Length<=10&&
        int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out int form)&&form>=0&&
        text==form.ToString(CultureInfo.InvariantCulture);
    internal const int CommandMaxBytes=4608;
    internal const int ControlMessageMaxBytes=CommandMaxBytes+40;
    internal static bool Command(string payload,out string[] fields)=>Command(payload,out fields,out _,out _);
    internal static bool Command(string payload, out string[] fields,out long revision,out long sequence)
    {
        fields = null;
        revision=sequence=0;
        if (payload == null || payload.Length > CommandMaxBytes) return false;
        var f = payload.Split(' ');
        if(f.Length>=6 && f[^3]=="Q")
        {
            if(f[^2].Length>19||f[^1].Length>19||!long.TryParse(f[^2],NumberStyles.None,CultureInfo.InvariantCulture,out revision)||revision<=0||
                !long.TryParse(f[^1],NumberStyles.None,CultureInfo.InvariantCulture,out sequence)||sequence<=0)return false;
            f=f[..^3];
        }
        if (f.Length < 3 || !Identity(f[0]) || !Session(f[1])) return false;
        bool group=f.Length==4 && In(f[2],"SELECT","RELEASE") && revision>0 && CrewSet(f[3]);
        if(!group && payload.Length>(revision>0?512:384))return false;
        bool valid = f.Length==4 && f[2]=="MIMIC" && revision>0 && MimicForm(f[3]) || group || f.Length == 3 && In(f[2], "CROUCH", "WALK", "CLAIM") ||
            f.Length == 9 && f[2]=="USE" && InteractionProtocol.Descriptor(f,3) ||
            f.Length == 5 && f[2]=="BODY" && (In(f[3],"P","PR","M","MR") && GuardId(f[4]) || f[3]=="D" && f[4]=="0") ||
            f.Length == 9 && f[2]=="HIT" && In(f[3],"K","L","KR","LR") && GuardId(f[4]) &&
            int.TryParse(f[5],NumberStyles.None,CultureInfo.InvariantCulture,out int view) && view > 0 && Position(f,6) ||
            f.Length == 4 && f[2] == "READY" && int.TryParse(f[3],NumberStyles.None,CultureInfo.InvariantCulture,out int challenge) && challenge > 0 ||
            f.Length == 5 && (Position(f,2) || f[2] == "HIT" && (EntitySkill(f[3]) || In(f[3],"KR","LR")) && GuardId(f[4]))
            || f.Length == 6 && (GroundMode(f[2]) || PointSkill(f[2])) && Position(f,3)
            || f.Length == 7 && GroundMode(f[2]) && Position(f,3) && In(f[6],"SHALLOW","DEEP","LAND");
        if (valid) fields = f;
        return valid;
    }
}

internal sealed class SnapshotOrder
{
    internal string Session { get; private set; }
    internal int LastSequence { get; private set; }
    internal bool Accept(string session, int sequence)
    {
        if (!SyncProtocol.Session(session) || sequence <= 0 || Session != null && Session != session || sequence <= LastSequence) return false;
        Session = session;
        LastSequence = sequence;
        return true;
    }
}

// Each uninterrupted arrival at attack range gets a new challenge. A reply
// from a cancelled approach, even against the same guard, cannot authorize it.
internal sealed class MeleeContactGate
{
    private int generation;
    private float expires, confirmedUntil;
    internal int Current { get; private set; }
    internal void Open(float now)
    {
        if (Current != 0 && now < expires) return;
        generation = generation == int.MaxValue ? 1 : generation + 1;
        Current = generation;
        expires = now + 0.45f;
        confirmedUntil = float.NegativeInfinity;
    }
    internal bool Confirm(int value,float now)
    {
        if (value == 0 || Current != value || now >= expires) return false;
        confirmedUntil = Math.Min(expires,now + 0.18f);
        return true;
    }
    internal bool Consume(float now)
    {
        if (Current == 0 || now >= confirmedUntil) return false;
        Clear();
        return true;
    }
    internal void Clear() { Current = 0; confirmedUntil = float.NegativeInfinity; }
}
