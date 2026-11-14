using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

// S19 carries the existing shared-world frame plus every other deployed/ship
// actor. The wire bound is a memory limit, not the eight-person mission limit.
internal static class CrewRosterProtocol
{
    internal const int MaxRosterOnWire=64;
    internal static string Write(string core,IReadOnlyList<string> rows,long revision=0,string owners=null)
    {
        if(owners!=null && !Ownership(revision,owners,rows.Count+2))throw new InvalidOperationException("Invalid crew ownership");
        string body="S19 "+rows.Count.ToString(CultureInfo.InvariantCulture)+(owners==null?"":" "+revision.ToString(CultureInfo.InvariantCulture)+" "+owners)+"\n"+core;
        foreach(string row in rows)body+="\n"+row;
        if(body.Length>SnapshotTransport.MaxBytes)throw new InvalidOperationException("Roster snapshot exceeds transport bound");
        return body;
    }
    internal static bool Read(string body,out string core,out string[][] rows)
        =>Read(body,out core,out rows,out _,out _);
    internal static bool Read(string body,out string core,out string[][] rows,out long revision,out string owners)
        =>Read(body,out core,out rows,out revision,out owners,out _);
    internal static bool Read(string body,out string core,out string[][] rows,out long revision,out string owners,out ControlledCrewProtocol.Entry[] controlled)
        =>Read(body,out core,out rows,out revision,out owners,out controlled,out _);
    internal static bool Read(string body,out string core,out string[][] rows,out long revision,out string owners,out ControlledCrewProtocol.Entry[] controlled,out string[][] objects)
        =>Read(body,out core,out rows,out revision,out owners,out controlled,out objects,out _);
    internal static bool Read(string body,out string core,out string[][] rows,out long revision,out string owners,out ControlledCrewProtocol.Entry[] controlled,out string[][] objects,out TrapProtocol.Group[] traps)
        =>Read(body,out core,out rows,out revision,out owners,out controlled,out objects,out traps,out _);
    internal static bool Read(string body,out string core,out string[][] rows,out long revision,out string owners,out ControlledCrewProtocol.Entry[] controlled,out string[][] objects,out TrapProtocol.Group[] traps,out string[][] ghosts)
        =>Read(body,out core,out rows,out revision,out owners,out controlled,out objects,out traps,out ghosts,out _);
    internal static bool Read(string body,out string core,out string[][] rows,out long revision,out string owners,out ControlledCrewProtocol.Entry[] controlled,out string[][] objects,out TrapProtocol.Group[] traps,out string[][] ghosts,out ResourceProtocol.Group[] resources)
        =>Read(body,out core,out rows,out revision,out owners,out controlled,out objects,out traps,out ghosts,out resources,out _);
    internal static bool Read(string body,out string core,out string[][] rows,out long revision,out string owners,out ControlledCrewProtocol.Entry[] controlled,out string[][] objects,out TrapProtocol.Group[] traps,out string[][] ghosts,out ResourceProtocol.Group[] resources,out string[][] freezes)
        =>Read(body,out core,out rows,out revision,out owners,out controlled,out objects,out traps,out ghosts,out resources,out freezes,out _);
    internal static bool Read(string body,out string core,out string[][] rows,out long revision,out string owners,out ControlledCrewProtocol.Entry[] controlled,out string[][] objects,out TrapProtocol.Group[] traps,out string[][] ghosts,out ResourceProtocol.Group[] resources,out string[][] freezes,out string[][] blinds)
        =>Read(body,out core,out rows,out revision,out owners,out controlled,out objects,out traps,out ghosts,out resources,out freezes,out blinds,out _);
    internal static bool Read(string body,out string core,out string[][] rows,out long revision,out string owners,out ControlledCrewProtocol.Entry[] controlled,out string[][] objects,out TrapProtocol.Group[] traps,out string[][] ghosts,out ResourceProtocol.Group[] resources,out string[][] freezes,out string[][] blinds,out string[][] frozen)
        =>Read(body,out core,out rows,out revision,out owners,out controlled,out objects,out traps,out ghosts,out resources,out freezes,out blinds,out frozen,out _);
    internal static bool Read(string body,out string core,out string[][] rows,out long revision,out string owners,out ControlledCrewProtocol.Entry[] controlled,out string[][] objects,out TrapProtocol.Group[] traps,out string[][] ghosts,out ResourceProtocol.Group[] resources,out string[][] freezes,out string[][] blinds,out string[][] frozen,out string[][] duels)
        =>Read(body,out core,out rows,out revision,out owners,out controlled,out objects,out traps,out ghosts,out resources,out freezes,out blinds,out frozen,out duels,out _);
    internal static bool Read(string body,out string core,out string[][] rows,out long revision,out string owners,out ControlledCrewProtocol.Entry[] controlled,out string[][] objects,out TrapProtocol.Group[] traps,out string[][] ghosts,out ResourceProtocol.Group[] resources,out string[][] freezes,out string[][] blinds,out string[][] frozen,out string[][] duels,out string[][] digs)
        =>Read(body,out core,out rows,out revision,out owners,out controlled,out objects,out traps,out ghosts,out resources,out freezes,out blinds,out frozen,out duels,out digs,out _);
    internal static bool Read(string body,out string core,out string[][] rows,out long revision,out string owners,out ControlledCrewProtocol.Entry[] controlled,out string[][] objects,out TrapProtocol.Group[] traps,out string[][] ghosts,out ResourceProtocol.Group[] resources,out string[][] freezes,out string[][] blinds,out string[][] frozen,out string[][] duels,out string[][] digs,out string[][] cannons)
        =>Read(body,out core,out rows,out revision,out owners,out controlled,out objects,out traps,out ghosts,out resources,out freezes,out blinds,out frozen,out duels,out digs,out cannons,out _);
    internal static bool Read(string body,out string core,out string[][] rows,out long revision,out string owners,out ControlledCrewProtocol.Entry[] controlled,out string[][] objects,out TrapProtocol.Group[] traps,out string[][] ghosts,out ResourceProtocol.Group[] resources,out string[][] freezes,out string[][] blinds,out string[][] frozen,out string[][] duels,out string[][] digs,out string[][] cannons,out string[][] fishing)
        =>Read(body,out core,out rows,out revision,out owners,out controlled,out objects,out traps,out ghosts,out resources,out freezes,out blinds,out frozen,out duels,out digs,out cannons,out fishing,out _);
    internal static bool Read(string body,out string core,out string[][] rows,out long revision,out string owners,out ControlledCrewProtocol.Entry[] controlled,out string[][] objects,out TrapProtocol.Group[] traps,out string[][] ghosts,out ResourceProtocol.Group[] resources,out string[][] freezes,out string[][] blinds,out string[][] frozen,out string[][] duels,out string[][] digs,out string[][] cannons,out string[][] fishing,out string[][] launches)
        =>Read(body,out core,out rows,out revision,out owners,out controlled,out objects,out traps,out ghosts,out resources,out freezes,out blinds,out frozen,out duels,out digs,out cannons,out fishing,out launches,out _);
    internal static bool Read(string body,out string core,out string[][] rows,out long revision,out string owners,out ControlledCrewProtocol.Entry[] controlled,out string[][] objects,out TrapProtocol.Group[] traps,out string[][] ghosts,out ResourceProtocol.Group[] resources,out string[][] freezes,out string[][] blinds,out string[][] frozen,out string[][] duels,out string[][] digs,out string[][] cannons,out string[][] fishing,out string[][] launches,out string[][] cannonFlights)
        =>Read(body,out core,out rows,out revision,out owners,out controlled,out objects,out traps,out ghosts,out resources,out freezes,out blinds,out frozen,out duels,out digs,out cannons,out fishing,out launches,out cannonFlights,out _);
    internal static bool Read(string body,out string core,out string[][] rows,out long revision,out string owners,out ControlledCrewProtocol.Entry[] controlled,out string[][] objects,out TrapProtocol.Group[] traps,out string[][] ghosts,out ResourceProtocol.Group[] resources,out string[][] freezes,out string[][] blinds,out string[][] frozen,out string[][] duels,out string[][] digs,out string[][] cannons,out string[][] fishing,out string[][] launches,out string[][] cannonFlights,out string[][] mimics)
    {
        core=null;rows=null;revision=0;owners=null;controlled=null;objects=null;traps=null;ghosts=null;resources=null;freezes=null;blinds=null;frozen=null;duels=null;digs=null;cannons=null;fishing=null;launches=null;cannonFlights=null;mimics=null;
        if(body==null || body.Length>SnapshotTransport.MaxBytes)return false;
        if(!FishingItemProtocol.Split(body,out body,out _)||!CharmingGhostProtocol.Split(body,out body,out var charming)||!ControlProtocol.Split(body,out body,out var controls)||!MimicProtocol.Split(body,out body,out mimics)||!FootprintProtocol.Split(body,out body,out _)||!FishingPullProtocol.Split(body,out body,out fishing)||!CannonFlightProtocol.Split(body,out body,out cannonFlights)||!CannonLaunchProtocol.Split(body,out body,out launches)||!CannonProtocol.Split(body,out body,out cannons)||!DigProtocol.Split(body,out body,out digs)||!DuelProtocol.Split(body,out body,out duels)||!FrozenProtocol.Split(body,out body,out frozen)||!BlindProtocol.Split(body,out body,out blinds)||!FreezeProtocol.Split(body,out body,out freezes)||!ResourceProtocol.Split(body,out body,out resources)||!GhostProtocol.Split(body,out body,out ghosts)||!TrapProtocol.Split(body,out body,out traps)||!SkillObjectProtocol.Split(body,out body,out objects)||
            !ControlledCrewProtocol.Split(body,out body,out controlled))return false;
        foreach(char c in body)if(c>127 || c=='\r')return false;
        var lines=body.Split('\n');
        var header=lines[0].Split(' ');
        if(lines.Length<2 || (header.Length!=2 && header.Length!=4) || header[0]!="S19" ||
            !int.TryParse(header[1],NumberStyles.None,CultureInfo.InvariantCulture,out int n) ||
            n>MaxRosterOnWire-2 || lines.Length!=n+2 || !lines[1].StartsWith("S18 ",StringComparison.Ordinal))return false;
        if(header.Length==4)
        {
            if(!long.TryParse(header[2],NumberStyles.None,CultureInfo.InvariantCulture,out revision)||!Ownership(revision,header[3],n+2))return false;
            owners=header[3];
        }
        var ids=new HashSet<string>(StringComparer.Ordinal);
        var carried=new HashSet<string>(StringComparer.Ordinal);
        var parsed=new string[n][];var extraIds=new string[n];
        for(int i=0;i<n;i++)
        {
            var f=lines[i+2].Split(' ');
            if(!Actor(f) || !ids.Add(f[0]))return false;
            if(f[21]!="-")foreach(string id in f[21].Split(','))if(!carried.Add(id))return false;
            parsed[i]=f;extraIds[i]=f[0];
        }
        if(!SyncProtocol.Snapshot(lines[1],out var shared,out _,out _,out _,out _,out _,out _,out _,out _,out var bodies,out _,out _,extraIds,owners!=null&&owners[1]=='-') ||
            !ids.Add(shared[4]) || (owners!=null&&owners[1]=='-'?shared[9]!=shared[4]:!ids.Add(shared[9])))return false;
        if(bodies!=null)foreach(string list in new[]{bodies[0],bodies[1]})if(list!="-")foreach(string id in list.Split(','))if(!carried.Add(id))return false;
        var ordered=new List<string>{shared[4],shared[9]};ordered.AddRange(extraIds);
        if(!ControlledCrewProtocol.Validate(controlled,ordered,owners))return false;
        foreach(var row in objects)if(!ids.Contains(row[0]))return false;
        foreach(var group in traps)if(!ids.Contains(group.Source))return false;
        foreach(var group in resources)if(!ids.Contains(group.Source))return false;
        foreach(var ghost in ghosts)
        {
            if(!ids.Contains(ghost[0]))return false;
            if(ghost[3]=="P")
            {
                if(!ControlledCrewProtocol.HasSkillActor(controlled,ghost[0],"G"))return false;
            }
        }
        if(mimics!=null)foreach(var mimic in mimics)
        {
            if(!ids.Contains(mimic[0]))return false;

            foreach(var entry in controlled)if(entry.Source==mimic[0]&&entry.Kind=="M")
            {if(mimic[3]=="O"||mimic[3]=="C"&&entry.Target!=mimic[2])return false;}
            // Dead copies can be absent from the controllable roster. Their
            // explicit result still survives; absence alone never means return.
        }
        if(controls!=null)foreach(var control in controls)if(!ids.Contains(control[0]))return false;
        if(charming!=null)foreach(var row in charming)if(!ids.Contains(row[0]))return false;
        core=lines[1];rows=parsed;return true;
    }
    private static bool Ownership(long revision,string owners,int count)
    {
        if(revision<=0||owners==null||owners.Length!=count||owners[0]!='H'||(owners[1]!='G'&&owners[1]!='-'))return false;
        for(int i=0;i<owners.Length;i++)if(i==1&&owners[i]=='-')continue;else if("HGU".IndexOf(owners[i])<0||owners[1]=='-'&&owners[i]=='G')return false;
        return true;
    }
    internal static int CoverEnd(string[] row)=>25+int.Parse(row[24],CultureInfo.InvariantCulture)*6;
    internal static string DestinationSurface(string[] row)
    {int end=CoverEnd(row);return row.Length==end+9&&row[end+8]!="-"?row[end+8]:null;}
    internal static char Cover(string[] row)=>row[4].Length==3?row[4][2]:'x';
    internal static bool Order(string[] f,int i)=>(f.Length==i+8 || f.Length==i+9 &&
        (f[i+8]=="-" || f[i+4]=="1" && (f[i+8]=="LAND"||f[i+8]=="SHALLOW"||f[i+8]=="DEEP")))&&f[i]=="O"&&f[i+1].Length<=19&&
        long.TryParse(f[i+1],NumberStyles.None,CultureInfo.InvariantCulture,out _) &&
        (f[i+2]=="W"||f[i+2]=="C")&&(f[i+3]=="0"||f[i+3]=="1")&&(f[i+4]=="0"||f[i+4]=="1")&&SyncProtocol.Position(f,i+5);
    internal static bool Actor(string[] f)
    {
        if(f==null || f.Length<25 || !SyncProtocol.Identity(f[0]) || !SyncProtocol.Position(f,1) ||
            !Regex.IsMatch(f[4],"\\A[WCR][lsdx][oihx]?\\z") || !SyncProtocol.Health(f[5],f[6]) ||
            !int.TryParse(f[7],NumberStyles.None,CultureInfo.InvariantCulture,out int serial) ||
            (serial==0 ? f[8]!="-" || f[9]!="0" :
                !(SyncProtocol.EntitySkill(f[8]) && SyncProtocol.GuardId(f[9]) || SyncProtocol.PointSkill(f[8]) && f[9]=="0")) ||
            !SyncProtocol.Position(f,10) || !InteractionProtocol.Record(f,13) || !SyncProtocol.CarryRoster(f[21]) ||
            f[22]!="V" || !Regex.IsMatch(f[23],"\\A[1x]x\\z") ||
            !int.TryParse(f[24],NumberStyles.None,CultureInfo.InvariantCulture,out int count) || count>4 ||
            (f.Length!=25+count*6 && f.Length!=33+count*6 && f.Length!=34+count*6) || count>0 && f[23][0]!='1')return false;
        var seen=new HashSet<string>(StringComparer.Ordinal);
        int end=25+count*6;
        if(f.Length>end && !Order(f,end))return false;
        for(int i=25;i<end;i+=6)
            if(f[i]!="A" || !SyncProtocol.GuardId(f[i+1]) || !seen.Add(f[i+1]) || !SyncProtocol.Position(f,i+2) ||
                !SyncProtocol.Number(f[i+5],out float yaw) || yaw<0 || yaw>=360)return false;
        return true;
    }
}

// Blind processes have a sight override and independent animation/movement
// durations. All sampled processes have passed their original impact delay.
// Frozen capture/execution processes; distinct from the TimeFreeze status.
// Includes the native handler owner, which need not be the affected target.
// Known actor samples: omitted actors remain unknown, explicit active=0 ends
// that actor's state. Renderer flag is only ParallelState, never global hiding.
internal static class CannonLaunchProtocol
{
    internal const int Stride=4,MaxActors=64;
    internal static bool Row(string[] f)=>f!=null&&f.Length==Stride&&FreezeProtocol.Character(f[0])&&FreezeProtocol.Character(f[1])&&f[0]!=f[1]&&
        (f[2]=="CannonLoad"||f[2]=="CannonAttack")&&int.TryParse(f[3],NumberStyles.None,CultureInfo.InvariantCulture,out int serial)&&serial>0;
    internal static bool Split(string body,out string core,out string[][] rows)
    {
        core=body;rows=null;if(body==null)return false;int at=body.LastIndexOf('\n');
        if(at<0||!body.AsSpan(at+1).StartsWith("K2 ".AsSpan(),StringComparison.Ordinal))return true;
        var f=body.Substring(at+1).Split(' ');
        if(f.Length<2||!int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n>MaxActors||f.Length!=2+n*Stride)return false;
        var result=new string[n][];var owners=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<n;i++){var row=f[(2+i*Stride)..(2+(i+1)*Stride)];if(!Row(row)||!owners.Add(row[0]))return false;result[i]=row;}
        rows=result;core=body.Substring(0,at);return true;
    }
    internal static string Attach(string body,IReadOnlyList<string[]> rows)
    {
        if(rows==null)return body;var text=new System.Text.StringBuilder(body);text.Append("\nK2 ").Append(rows.Count.ToString(CultureInfo.InvariantCulture));
        foreach(var row in rows)text.Append(' ').Append(string.Join(" ",row));string result=text.ToString();
        if(!CrewRosterProtocol.Read(result,out _,out _))throw new InvalidOperationException("Invalid cannon launch manifest");return result;
    }
}

internal static class CannonProtocol
{
    internal const int Stride=4,MaxActors=512;
    internal static bool Row(string[] f)=>f!=null&&(f.Length==Stride||f.Length==8&&SyncProtocol.Position(f,4)&&(f[7]=="0"||f[7]=="1"))&&FreezeProtocol.Character(f[0])&&
        (f[1]=="-"?f[2]=="-":FreezeProtocol.Character(f[1])&&f[0]!=f[1]&&(f[2]=="CannonLoad"||f[2]=="CannonAttack"))&&(f[3]=="0"||f[3]=="1"||f[3]=="2"||f[3]=="3");
    internal static bool Split(string body,out string core,out string[][] rows)
    {
        core=body;rows=null;if(body==null)return false;int at=body.LastIndexOf('\n');
        if(at<0||!body.AsSpan(at+1).StartsWith("K1 ".AsSpan(),StringComparison.Ordinal))return true;
        var f=body.Substring(at+1).Split(' ');
        if(f.Length<2||!int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n<0||n>MaxActors)return false;
        int stride=f.Length==2+n*8?8:Stride;if(f.Length!=2+n*stride)return false;
        var parsed=new string[n][];var ids=new HashSet<string>(StringComparer.Ordinal);var owners=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<n;i++){var row=f[(2+i*stride)..(2+(i+1)*stride)];if(!Row(row)||!ids.Add(row[0])||row[1]!="-"&&!owners.Add(row[1]))return false;parsed[i]=row;}
        rows=parsed;core=body.Substring(0,at);return true;
    }
    internal static string Attach(string body,IReadOnlyList<string[]> rows)
    {
        if(rows==null)return body;var text=new System.Text.StringBuilder(body);text.Append("\nK1 ").Append(rows.Count.ToString(CultureInfo.InvariantCulture));
        foreach(var row in rows)text.Append(' ').Append(string.Join(" ",row));string result=text.ToString();
        if(!CrewRosterProtocol.Read(result,out _,out _))throw new InvalidOperationException("Invalid cannon attachment manifest");return result;
    }
}

internal static class DigProtocol
{
    internal const int Stride=14,MaxActors=128;
    private static bool Bit(string s)=>s=="0"||s=="1";
    internal static bool Row(string[] f)=>f!=null&&f.Length==Stride&&FreezeProtocol.Character(f[0])&&(f[1]=="D"||f[1]=="P")&&
        Bit(f[2])&&Bit(f[3])&&(f[1]=="P"?f[4]=="-":Bit(f[4]))&&SyncProtocol.Position(f,5)&&Bit(f[8])&&SyncProtocol.Position(f,9)&&
        SyncProtocol.Number(f[12],out float markerYaw)&&markerYaw>=0&&markerYaw<=360&&SyncProtocol.Number(f[13],out float yaw)&&yaw>=0&&yaw<=360;
    internal static bool Split(string body,out string core,out string[][] rows)
    {
        core=body;rows=null;if(body==null)return false;int at=body.LastIndexOf('\n');
        if(at<0||!body.AsSpan(at+1).StartsWith("D1 ".AsSpan(),StringComparison.Ordinal))return true;
        var f=body.Substring(at+1).Split(' ');
        if(f.Length<2||!int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n>MaxActors||f.Length!=2+n*Stride)return false;
        var parsed=new string[n][];var ids=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<n;i++){var row=f[(2+i*Stride)..(2+(i+1)*Stride)];if(!Row(row)||!ids.Add(row[0]))return false;parsed[i]=row;}
        rows=parsed;core=body.Substring(0,at);return true;
    }
    internal static string Attach(string body,IReadOnlyList<string[]> rows)
    {
        if(rows==null)return body;var text=new System.Text.StringBuilder(body);text.Append("\nD1 ").Append(rows.Count.ToString(CultureInfo.InvariantCulture));
        foreach(var row in rows)text.Append(' ').Append(string.Join(" ",row));string result=text.ToString();
        if(!CrewRosterProtocol.Read(result,out _,out _))throw new InvalidOperationException("Invalid dig state manifest");return result;
    }
}

internal static class DuelProtocol
{
    internal const int Stride=6,MaxEffects=128;
    internal static bool Row(string[] f)=>f!=null&&f.Length==Stride&&FreezeProtocol.Character(f[0])&&
        int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int generation)&&generation>0&&FreezeProtocol.Character(f[2])&&
        int.TryParse(f[3],NumberStyles.None,CultureInfo.InvariantCulture,out int skill)&&(skill==0||Array.IndexOf(ResourceProtocol.SkillIds,skill)>=0)&&
        FreezeProtocol.Character(f[4])&&(f[5]=="1"||f[5]=="2");
    internal static bool Split(string body,out string core,out string[][] rows)
    {
        core=body;rows=null;if(body==null)return false;int at=body.LastIndexOf('\n');
        if(at<0||!body.AsSpan(at+1).StartsWith("E4 ".AsSpan(),StringComparison.Ordinal))return true;
        var f=body.Substring(at+1).Split(' ');
        if(f.Length<2||!int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n>MaxEffects||f.Length!=2+n*Stride)return false;
        var parsed=new string[n][];var targets=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<n;i++){var row=f[(2+i*Stride)..(2+(i+1)*Stride)];if(!Row(row)||!targets.Add(row[0]))return false;parsed[i]=row;}
        rows=parsed;core=body.Substring(0,at);return true;
    }
    internal static string Attach(string body,IReadOnlyList<string[]> rows)
    {
        if(rows==null)return body;var text=new System.Text.StringBuilder(body);text.Append("\nE4 ").Append(rows.Count.ToString(CultureInfo.InvariantCulture));
        foreach(var row in rows)text.Append(' ').Append(string.Join(" ",row));string result=text.ToString();
        if(!CrewRosterProtocol.Read(result,out _,out _))throw new InvalidOperationException("Invalid duel manifest");return result;
    }
}

internal static class FrozenProtocol
{
    internal const int Stride=17,MaxEffects=128;
    private static bool Asset(string v)=>v=="-"||SyncProtocol.Identity(v);
    private static bool Skill(string v)=>int.TryParse(v,NumberStyles.None,CultureInfo.InvariantCulture,out int id)&&(id==0||Array.IndexOf(ResourceProtocol.SkillIds,id)>=0);
    internal static bool Row(string[] f)=>f!=null&&f.Length==Stride&&FreezeProtocol.Character(f[0])&&
        int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int generation)&&generation>0&&FreezeProtocol.Character(f[2])&&Skill(f[3])&&
        SyncProtocol.Number(f[4],out float duration)&&(duration==-1||duration>0&&duration<=86400)&&
        SyncProtocol.Number(f[5],out float elapsed)&&elapsed>=0&&(duration==-1||elapsed<=duration)&&
        int.TryParse(f[6],NumberStyles.None,CultureInfo.InvariantCulture,out int flags)&&flags<=1023&&
        SyncProtocol.Number(f[7],out float staging)&&staging>=0&&staging<=86400&&SyncProtocol.Number(f[8],out _)&&
        Asset(f[9])&&Asset(f[10])&&Asset(f[11])&&Asset(f[12])&&(f[13]=="0"||f[13]=="1")&&Skill(f[14])&&FreezeProtocol.Character(f[15])&&
        (f[16]=="1"||f[16]=="2"||f[16]=="3");
    internal static bool Split(string body,out string core,out string[][] rows)
    {
        core=body;rows=null;if(body==null)return false;int at=body.LastIndexOf('\n');
        if(at<0||!body.AsSpan(at+1).StartsWith("E3 ".AsSpan(),StringComparison.Ordinal))return true;
        var f=body.Substring(at+1).Split(' ');
        if(f.Length<2||!int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n>MaxEffects||f.Length!=2+n*Stride)return false;
        var parsed=new string[n][];var targets=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<n;i++){var row=f[(2+i*Stride)..(2+(i+1)*Stride)];if(!Row(row)||!targets.Add(row[0]))return false;parsed[i]=row;}
        rows=parsed;core=body.Substring(0,at);return true;
    }
    internal static string Attach(string body,IReadOnlyList<string[]> rows)
    {
        if(rows==null)return body;var text=new System.Text.StringBuilder(body);text.Append("\nE3 ").Append(rows.Count.ToString(CultureInfo.InvariantCulture));
        foreach(var row in rows)text.Append(' ').Append(string.Join(" ",row));string result=text.ToString();
        if(!CrewRosterProtocol.Read(result,out _,out _))throw new InvalidOperationException("Invalid frozen execution manifest");return result;
    }
}

internal static class BlindProtocol
{
    internal const int Stride=14,MaxEffects=128;
    private static bool Asset(string value)=>value=="-"||SyncProtocol.Identity(value);
    private static bool Span(string value)=>SyncProtocol.Number(value,out float seconds)&&(seconds==-1||seconds>=0&&seconds<=86400);
    internal static bool Row(string[] f)=>f!=null&&f.Length==Stride&&FreezeProtocol.Character(f[0])&&
        int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int generation)&&generation>0&&
        (f[2]=="-"||FreezeProtocol.Character(f[2]))&&int.TryParse(f[3],NumberStyles.None,CultureInfo.InvariantCulture,out int skill)&&(skill==0||Array.IndexOf(ResourceProtocol.SkillIds,skill)>=0)&&
        SyncProtocol.Number(f[4],out float duration)&&(duration==-1||duration>0&&duration<=86400)&&
        SyncProtocol.Number(f[5],out float elapsed)&&elapsed>=0&&(duration==-1||elapsed<=duration)&&
        int.TryParse(f[6],NumberStyles.None,CultureInfo.InvariantCulture,out int flags)&&flags>=0&&flags<=15&&
        SyncProtocol.Number(f[7],out float wait)&&wait>=0&&wait<=86400&&Span(f[8])&&Span(f[9])&&
        Asset(f[10])&&Asset(f[11])&&Asset(f[12])&&DetectionProfileProtocol.Valid(f[13]);
    internal static bool Split(string body,out string core,out string[][] rows)
    {
        core=body;rows=null;if(body==null)return false;int at=body.LastIndexOf('\n');
        if(at<0||!body.AsSpan(at+1).StartsWith("E2 ".AsSpan(),StringComparison.Ordinal))return true;
        var f=body.Substring(at+1).Split(' ');
        if(f.Length<2||!int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n>MaxEffects||f.Length!=2+n*Stride)return false;
        var parsed=new string[n][];var targets=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<n;i++){var row=f[(2+i*Stride)..(2+(i+1)*Stride)];if(!Row(row)||!targets.Add(row[0]))return false;parsed[i]=row;}
        rows=parsed;core=body.Substring(0,at);return true;
    }
    internal static string Attach(string body,IReadOnlyList<string[]> rows)
    {
        if(rows==null)return body;var text=new System.Text.StringBuilder(body);text.Append("\nE2 ").Append(rows.Count.ToString(CultureInfo.InvariantCulture));
        foreach(var row in rows)text.Append(' ').Append(string.Join(" ",row));string result=text.ToString();
        if(!CrewRosterProtocol.Read(result,out _,out _))throw new InvalidOperationException("Invalid blind process manifest");return result;
    }
}

// Complete active TimeFreeze process manifest. Null means no sample; zero rows
// means the host has no active processes. Native infinity is encoded as -1.
internal static class FreezeProtocol
{
    internal const int Stride=11,MaxEffects=128;
    internal static bool Character(string value)=>value!=null&&value.Length>1&&
        (value[0]=='e'&&SyncProtocol.GuardId(value.Substring(1))||(value[0]=='p'||value[0]=='o')&&SyncProtocol.Identity(value.Substring(1)));
    private static bool Asset(string value)=>value=="-"||SyncProtocol.Identity(value);
    internal static bool Row(string[] f)=>f!=null&&f.Length==Stride&&Character(f[0])&&
        int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int generation)&&generation>0&&
        (f[2]=="-"||Character(f[2]))&&int.TryParse(f[3],NumberStyles.None,CultureInfo.InvariantCulture,out int skill)&&(skill==0||Array.IndexOf(ResourceProtocol.SkillIds,skill)>=0)&&
        SyncProtocol.Number(f[4],out float duration)&&(duration==-2||duration==-1||duration>0&&duration<=86400)&&
        SyncProtocol.Number(f[5],out float elapsed)&&elapsed>=0&&(duration<0?elapsed==0:elapsed<=duration)&&
        int.TryParse(f[6],NumberStyles.None,CultureInfo.InvariantCulture,out int flags)&&flags>=0&&flags<=15&&
        int.TryParse(f[7],NumberStyles.None,CultureInfo.InvariantCulture,out int icon)&&icon>=0&&icon<=37&&Asset(f[8])&&Asset(f[9])&&Asset(f[10]);
    internal static bool Split(string body,out string core,out string[][] rows)
    {
        core=body;rows=null;if(body==null)return false;int at=body.LastIndexOf('\n');
        if(at<0||!body.AsSpan(at+1).StartsWith("E1 ".AsSpan(),StringComparison.Ordinal))return true;
        var f=body.Substring(at+1).Split(' ');
        if(f.Length<2||!int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n>MaxEffects||f.Length!=2+n*Stride)return false;
        var parsed=new string[n][];var targets=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<n;i++){var row=f[(2+i*Stride)..(2+(i+1)*Stride)];if(!Row(row)||!targets.Add(row[0]))return false;parsed[i]=row;}
        rows=parsed;core=body.Substring(0,at);return true;
    }
    internal static string Attach(string body,IReadOnlyList<string[]> rows)
    {
        if(rows==null)return body;var text=new System.Text.StringBuilder(body);text.Append("\nE1 ").Append(rows.Count.ToString(CultureInfo.InvariantCulture));
        foreach(var row in rows)text.Append(' ').Append(string.Join(" ",row));string result=text.ToString();
        if(!CrewRosterProtocol.Read(result,out _,out _))throw new InvalidOperationException("Invalid freeze process manifest");return result;
    }
}

// Native DetectionData's seven resolved BalancedFloat values and raw height
// overrides. A null value stays distinct from zero; shared presets are never edited.
internal static class DetectionProfileProtocol
{
    internal static bool Valid(string token)
    {
        if(token=="-")return true;if(token==null||token.Length>512)return false;
        var f=token.Split('~');
        if(f.Length!=11||!int.TryParse(f[0],NumberStyles.None,CultureInfo.InvariantCulture,out int preset)||preset>3||
            !int.TryParse(f[8],NumberStyles.None,CultureInfo.InvariantCulture,out int height)||height>65535||
            !SyncProtocol.Number(f[9],out _)||!SyncProtocol.Number(f[10],out _))return false;
        for(int i=1;i<=7;i++)if(f[i]!="-"&&!SyncProtocol.Number(f[i],out _))return false;
        return true;
    }
}

// Complete resource sample per actor. Only active cooldowns are on the wire;
// their absence inside a present group means ready. Omitted actors are unknown.
internal static class ResourceProtocol
{
    internal const int MaxGroups=64,MaxSkills=157,MaxAmmo=1000000;
    internal const float MaxDuration=86400;
    internal static readonly int[] SkillIds={ 1100,1200,1250,1300,1350,1355,1400,1500,1505,1510,1600,1610,1620,1650,1700,1710,1711,1712,1713,1715,1720,1730,1739,1740,1750,1760,1770,1771,1772,1773,1774,1780,1781,1785,1786,1790,1791,1795,1800,1850,1900,2100,3300,7050,7100,7150,7200,7225,7250,7300,7350,7400,7450,7500,7550,7600,7650,7675,7700,7750,7800,7850,7900,7950,8000,8050,8060,8100,8150,8160,8170,8500,8505,9100,9150,9200,9250,9300,9400,9500,10098,10099,10100,10101,10102,10104,10106,10110,10150,10151,10152,10153,10154,10310,10311,10312,10313,10314,10315,10320,10340,10341,10360,10361,10410,10411,10412,10413,10414,10415,10416,10417,10420,10425,10430,10440,10450,10460,10470,10480,10481,10482,10483,10504,10505,10506,10508,10525,10526,10527,10530,10550,10590,10591,10592,10593,10594,10595,10596,10597,10610,10650,10655,10710,10711,10712,10713,10730,10731,10735,10740,10760,10900,10901,10910,10920 };
    internal sealed class Entry
    {
        internal int Skill;internal float Duration,Remaining;
        internal Entry(int skill,float duration,float remaining){Skill=skill;Duration=duration;Remaining=remaining;}
    }
    internal sealed class Group
    {
        internal string Source;internal int[] Ammo;internal Entry[] Cooldowns;
        internal Group(string source,int[] ammo,Entry[] cooldowns){Source=source;Ammo=ammo;Cooldowns=cooldowns;}
    }
    internal static bool Split(string body,out string core,out Group[] groups)
    {
        core=body;groups=Array.Empty<Group>();if(body==null)return false;int at=body.LastIndexOf('\n');
        if(at<0||!body.AsSpan(at+1).StartsWith("R1 ".AsSpan(),StringComparison.Ordinal))return true;
        var f=body.Substring(at+1).Split(' ');
        if(f.Length<2||!Int(f[1],MaxGroups,out int n))return false;
        int p=2;var parsed=new Group[n];var sources=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<n;i++)
        {
            if(p+6>f.Length||!SyncProtocol.Identity(f[p])||!sources.Add(f[p]))return false;
            string source=f[p++];var ammo=new int[4];
            for(int j=0;j<4;j++)if(!Int(f[p++],MaxAmmo,out ammo[j]))return false;
            if(!Int(f[p++],MaxSkills,out int count)||p+count*3>f.Length)return false;
            var entries=new Entry[count];var skills=new HashSet<int>();
            for(int j=0;j<count;j++,p+=3)
            {
                if(!Int(f[p],65535,out int skill)||Array.IndexOf(SkillIds,skill)<0||!skills.Add(skill)||
                    !SyncProtocol.Number(f[p+1],out float duration)||duration<=0||duration>MaxDuration||
                    !SyncProtocol.Number(f[p+2],out float remaining)||remaining<=0||remaining>duration)return false;
                entries[j]=new(skill,duration,remaining);
            }
            parsed[i]=new(source,ammo,entries);
        }
        if(p!=f.Length)return false;groups=parsed;core=body.Substring(0,at);return true;
    }
    private static bool Int(string value,int max,out int result)=>int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out result)&&result>=0&&result<=max;
    internal static string Attach(string body,IReadOnlyList<Group> groups)
    {
        if(groups==null||groups.Count==0)return body;
        var text=new System.Text.StringBuilder(body);text.Append("\nR1 ").Append(groups.Count.ToString(CultureInfo.InvariantCulture));
        foreach(var group in groups)
        {
            if(group.Ammo==null||group.Ammo.Length!=4||group.Cooldowns==null)throw new InvalidOperationException("Incomplete resource sample");
            text.Append(' ').Append(group.Source);foreach(int count in group.Ammo)text.Append(' ').Append(count.ToString(CultureInfo.InvariantCulture));
            text.Append(' ').Append(group.Cooldowns.Length.ToString(CultureInfo.InvariantCulture));
            foreach(var entry in group.Cooldowns)text.Append(' ').Append(entry.Skill.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(entry.Duration.ToString("G9",CultureInfo.InvariantCulture)).Append(' ').Append(entry.Remaining.ToString("G9",CultureInfo.InvariantCulture));
        }
        string result=text.ToString();if(!CrewRosterProtocol.Read(result,out _,out _))throw new InvalidOperationException("Invalid crew resource sample");return result;
    }
}

// Persistent GhostPlace lifecycle; position/goal are independent of the short
// cast cue. P means the live actor is controlled through the C1 crew manifest.
internal static class GhostProtocol
{
    internal const int Stride=19,MaxGhosts=64;
    internal static bool Row(string[] f)=>f!=null&&f.Length==Stride&&SyncProtocol.Identity(f[0])&&
        int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int generation)&&generation>=0&&
        (f[2]=="N"&&f[3]=="-"||generation>0&&(f[2]=="0"||f[2]=="1"||f[2]=="2"||f[2]=="3")&&(f[3]=="A"||f[3]=="P"))&&
        SyncProtocol.Position(f,4)&&SyncProtocol.Number(f[7],out float yaw)&&yaw>=0&&yaw<360&&SyncProtocol.Position(f,8)&&
        Character(f[11])&&Cute(f[12])&&Followers(f[13])&&(f[14]=="0"||f[14]=="1")&&SyncProtocol.Health(f[15],f[16])&&Cute(f[17])&&Followers(f[18]);
    private static bool Cute(string value)=>value=="0"||value=="1"||value=="2";
    private static bool Character(string key)=>key=="-"||key!=null&&key.Length>1&&
        (key[0]=='e'&&SyncProtocol.GuardId(key.Substring(1))||(key[0]=='p'||key[0]=='o')&&SyncProtocol.Identity(key.Substring(1)));
    private static bool Followers(string value)
    {
        if(value=="-")return true;if(value==null)return false;var fields=value.Split(',');if(fields.Length>64)return false;
        var unique=new HashSet<string>(StringComparer.Ordinal);foreach(string id in fields)if(!SyncProtocol.GuardId(id)||!unique.Add(id))return false;return true;
    }
    internal static bool Split(string body,out string core,out string[][] rows)
    {
        core=body;rows=Array.Empty<string[]>();if(body==null)return false;int at=body.LastIndexOf('\n');
        if(at<0||!body.AsSpan(at+1).StartsWith("G1 ".AsSpan(),StringComparison.Ordinal))return true;
        var fields=body.Substring(at+1).Split(' ');
        if(fields.Length<2||!int.TryParse(fields[1],NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n>MaxGhosts||fields.Length!=2+n*Stride)return false;
        var parsed=new string[n][];var unique=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<n;i++){var row=fields[(2+i*Stride)..(2+(i+1)*Stride)];if(!Row(row)||!unique.Add(row[0]))return false;parsed[i]=row;}
        rows=parsed;core=body.Substring(0,at);return true;
    }
    internal static string Attach(string body,IReadOnlyList<string[]> rows)
    {
        if(rows==null||rows.Count==0)return body;string text="G1 "+rows.Count.ToString(CultureInfo.InvariantCulture);
        foreach(var row in rows)text+=" "+string.Join(" ",row);string result=body+"\n"+text;
        if(!CrewRosterProtocol.Read(result,out _,out _))throw new InvalidOperationException("Invalid persistent ghost result");return result;
    }
}

// Complete per-caster trap pool. Slot indices are host identities, never local
// pool indices. An omitted group is unknown; an empty group explicitly clears.
internal static class TrapProtocol
{
    internal const int Stride=12,MaxSlots=64,MaxGroups=64,MaxTotal=256;
    internal sealed class Group
    {
        internal string Source;internal bool Pending;internal string[][] Rows;
        internal Group(string source,bool pending,string[][] rows){Source=source;Pending=pending;Rows=rows;}
    }
    internal static bool Row(string[] f)=>f!=null&&f.Length==Stride&&
        int.TryParse(f[0],NumberStyles.None,CultureInfo.InvariantCulture,out int slot)&&slot>=1&&slot<=MaxSlots&&
        int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int generation)&&generation>=0&&
        (f[2]=="N"&&f[4]=="0"&&f[5]=="-"||generation>0&&(f[2]=="W"&&f[5]=="-"||f[2]=="I"&&f[4]=="0"&&f[5]!="-"))&&
        (f[3]=="0"||f[3]=="1")&&(f[4]=="0"||f[4]=="1")&&Character(f[5])&&SyncProtocol.Position(f,6)&&SyncProtocol.Position(f,9);
    private static bool Character(string key)=>key=="-"||key!=null&&key.Length>1&&
        (key[0]=='e'&&SyncProtocol.GuardId(key.Substring(1))||key[0]=='p'&&SyncProtocol.Identity(key.Substring(1)));
    internal static bool Split(string body,out string core,out Group[] groups)
    {
        core=body;groups=Array.Empty<Group>();if(body==null)return false;
        int at=body.LastIndexOf('\n');
        if(at<0||!body.AsSpan(at+1).StartsWith("T1 ".AsSpan(),StringComparison.Ordinal))return true;
        var f=body.Substring(at+1).Split(' ');
        if(f.Length<2||!int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int count)||count>MaxGroups)return false;
        int p=2,total=0;var parsed=new Group[count];var sources=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<count;i++)
        {
            if(p+3>f.Length||!SyncProtocol.Identity(f[p])||!sources.Add(f[p])||(f[p+1]!="0"&&f[p+1]!="1")||
                !int.TryParse(f[p+2],NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n>MaxSlots||
                (total+=n)>MaxTotal||p+3+n*Stride>f.Length)return false;
            string source=f[p];bool pending=f[p+1]=="1";p+=3;
            var rows=new string[n][];var slots=new HashSet<string>(StringComparer.Ordinal);
            for(int j=0;j<n;j++,p+=Stride)
            {
                var row=f[p..(p+Stride)];if(!Row(row)||!slots.Add(int.Parse(row[0],CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)))return false;
                rows[j]=row;
            }
            parsed[i]=new Group(source,pending,rows);
        }
        if(p!=f.Length)return false;
        core=body.Substring(0,at);groups=parsed;return true;
    }
    internal static string Attach(string body,IReadOnlyList<Group> groups)
    {
        if(groups==null||groups.Count==0)return body;
        var text=new System.Text.StringBuilder(body).Append("\nT1 ").Append(groups.Count.ToString(CultureInfo.InvariantCulture));
        foreach(var group in groups)
        {
            text.Append(' ').Append(group.Source).Append(group.Pending?" 1 ":" 0 ").Append(group.Rows.Length.ToString(CultureInfo.InvariantCulture));
            foreach(var row in group.Rows)text.Append(' ').Append(string.Join(" ",row));
        }
        string result=text.ToString();
        if(!CrewRosterProtocol.Read(result,out _,out _))throw new InvalidOperationException("Invalid trap results");
        return result;
    }
}

// Persistent native skill results. Each row describes one caster's skill slot,
// including explicit absence. Unreported/unknown slots are never deleted.
internal static class SkillObjectProtocol
{
    internal const int Stride=10,MaxSlots=256;
    internal static bool Row(string[] f)=>f!=null&&f.Length==Stride&&SyncProtocol.Identity(f[0])&&(f[1]=="TM"||f[1]=="BA"||f[1]=="BN")&&
        int.TryParse(f[2],NumberStyles.None,CultureInfo.InvariantCulture,out int generation)&&generation>=0&&
        (f[3]=="A"&&generation>0||f[3]=="I"&&f[1]!="TM"&&generation>0&&f[4]!="-"||f[3]=="N"&&f[4]=="-"&&f[5]=="-")&&
        CharacterKey(f[4])&&CharacterKey(f[5])&&SyncProtocol.Position(f,6)&&
        int.TryParse(f[9],NumberStyles.None,CultureInfo.InvariantCulture,out int flags)&&flags<=7&&
        (f[1]=="TM"||f[5]=="-"&&(f[3]!="A"||f[4]=="-")&&(flags==0||flags==4));
    private static bool CharacterKey(string key)=>key=="-"||key!=null&&key.Length>1&&
        (key[0]=='e'&&SyncProtocol.GuardId(key.Substring(1))||key[0]=='p'&&SyncProtocol.Identity(key.Substring(1)));
    internal static bool Split(string body,out string core,out string[][] rows)
    {
        core=body;rows=Array.Empty<string[]>();if(body==null)return false;
        int at=body.LastIndexOf('\n');
        if(at<0||!body.AsSpan(at+1).StartsWith("O1 ".AsSpan(),StringComparison.Ordinal))return true;
        var fields=body.Substring(at+1).Split(' ');
        if(fields.Length<2||!int.TryParse(fields[1],NumberStyles.None,CultureInfo.InvariantCulture,out int n)||
            n>MaxSlots||fields.Length!=2+n*Stride)return false;
        var parsed=new string[n][];var seen=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<n;i++)
        {var row=fields[(2+i*Stride)..(2+(i+1)*Stride)];if(!Row(row)||!seen.Add(row[0]+":"+row[1]))return false;parsed[i]=row;}
        rows=parsed;core=body.Substring(0,at);return true;
    }
    internal static string Attach(string body,IReadOnlyList<string[]> rows)
    {
        if(rows==null||rows.Count==0)return body;
        string tail="O1 "+rows.Count.ToString(CultureInfo.InvariantCulture);
        foreach(var row in rows)tail+=" "+string.Join(" ",row);
        string result=body+"\n"+tail;
        if(!CrewRosterProtocol.Read(result,out _,out _))throw new InvalidOperationException("Invalid persistent skill result");
        return result;
    }
}

// Explicit spawn/control manifest. A missing proxy can wait for its caster's
// native action; a missing ordinary/DLC crew member still rejects the frame.
internal static class ControlledCrewProtocol
{
    internal sealed class Entry
    {
        internal string Id,Source,Kind,Target;
        internal Entry(string id,string source,string kind,string target)
        {Id=id;Source=source;Kind=kind;Target=target;}
    }
    internal static bool Kind(string value)=>value is "C" or "O" or "H" or "G" or "M";
    // C1 ownership is flattened to the original roster member, while skill
    // result rows name the actual caster (which can be its current mimic).
    // Called after Validate: only an explicit M binding may bridge the two.
    internal static bool HasSkillActor(Entry[] entries,string caster,string kind)
    {
        string owner=caster;
        foreach(var entry in entries)if(entry.Id==caster)
        {if(entry.Kind!="M")return false;owner=entry.Source;break;}
        foreach(var entry in entries)if(entry.Source==owner&&entry.Kind==kind)return true;
        return false;
    }
    internal static string Identity(string source,string kind)
    {
        if(!SyncProtocol.Identity(source)||!Kind(kind))return null;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("controlled-crew-v1\n"+source+"\n"+kind)));
    }
    private static bool Valid(Entry e)=>e!=null&&SyncProtocol.Identity(e.Id)&&Kind(e.Kind)&&e.Id==Identity(e.Source,e.Kind)&&
        (e.Kind is "C" or "O" ? SyncProtocol.GuardId(e.Target):e.Kind=="M"?int.TryParse(e.Target,out int mimic)&&mimic>=0&&e.Target==mimic.ToString(CultureInfo.InvariantCulture):e.Target=="-");
    internal static bool Split(string body,out string core,out Entry[] entries)
    {
        core=body;entries=Array.Empty<Entry>();if(body==null)return false;
        int at=body.LastIndexOf('\n');
        if(at<0||!body.AsSpan(at+1).StartsWith("C1 ".AsSpan(),StringComparison.Ordinal))return true;
        var fields=body.Substring(at+1).Split(' ');
        if(fields.Length<2||!int.TryParse(fields[1],NumberStyles.None,CultureInfo.InvariantCulture,out int n)||
            n>62||fields.Length!=2+n*4)return false;
        var result=new Entry[n];var unique=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<n;i++)
        {
            int p=2+i*4;var e=new Entry(fields[p],fields[p+1],fields[p+2],fields[p+3]);
            if(!Valid(e)||!unique.Add(e.Id))return false;result[i]=e;
        }
        entries=result;core=body.Substring(0,at);return true;
    }
    internal static bool Validate(Entry[] entries,IReadOnlyList<string> roster,string owners)
    {
        if(entries==null||roster==null)return false;
        if(entries.Length==0)return true;
        if(owners==null||owners.Length!=roster.Count)return false;
        var slots=new Dictionary<string,int>(StringComparer.Ordinal);
        for(int i=0;i<roster.Count;i++){if(i==1&&owners[1]=='-'&&roster[1]==roster[0])continue;if(!slots.TryAdd(roster[i],i))return false;}
        var proxies=new HashSet<string>(StringComparer.Ordinal);
        foreach(var e in entries)if(!Valid(e)||!proxies.Add(e.Id))return false;
        foreach(var e in entries)
            if(!slots.TryGetValue(e.Id,out int proxy)||proxy<2||!slots.TryGetValue(e.Source,out int source)||
                proxies.Contains(e.Source)||owners[source]=='U'||owners[proxy]!=owners[source])return false;
        return true;
    }
    internal static string Attach(string body,IReadOnlyList<Entry> entries)
    {
        if(entries==null||entries.Count==0)return body;
        string tail="C1 "+entries.Count.ToString(CultureInfo.InvariantCulture);
        foreach(var e in entries)tail+=" "+e.Id+" "+e.Source+" "+e.Kind+" "+e.Target;
        string result=body+"\n"+tail;
        if(!CrewRosterProtocol.Read(result,out _,out _))throw new InvalidOperationException("Invalid controlled crew snapshot");
        return result;
    }
}

// Persistent mimic result; omission is unknown, O is an explicit original
// form, C a settled copy and P a native asynchronous transition in progress.
internal static class MimicProtocol
{
    internal const int Stride=4,MaxActors=64;
    internal static bool Row(string[] r)=>r!=null&&r.Length==Stride&&SyncProtocol.Identity(r[0])&&
        int.TryParse(r[1],NumberStyles.None,CultureInfo.InvariantCulture,out int gen)&&gen>0&&r[1]==gen.ToString(CultureInfo.InvariantCulture)&&
        SyncProtocol.MimicForm(r[2])&&(r[3]=="O"||r[3]=="C"||r[3]=="P");
    internal static bool Split(string body,out string core,out string[][] rows)
    {
        core=body;rows=null;if(body==null)return false;int at=body.LastIndexOf('\n');
        if(at<0||!body.AsSpan(at+1).StartsWith("M1 ".AsSpan(),StringComparison.Ordinal))return true;
        var f=body.Substring(at+1).Split(' ');
        if(f.Length<2||!int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n>MaxActors||f.Length!=2+n*Stride)return false;
        var parsed=new string[n][];var ids=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<n;i++){var row=f[(2+i*Stride)..(2+(i+1)*Stride)];if(!Row(row)||!ids.Add(row[0]))return false;parsed[i]=row;}
        rows=parsed;core=body.Substring(0,at);return true;
    }
    internal static string Attach(string body,IReadOnlyList<string[]> rows)
    {
        if(rows==null)return body;var text=new System.Text.StringBuilder(body);text.Append("\nM1 ").Append(rows.Count.ToString(CultureInfo.InvariantCulture));
        foreach(var row in rows)text.Append(' ').Append(string.Join(" ",row));var result=text.ToString();
        if(!CrewRosterProtocol.Read(result,out _,out _))throw new InvalidOperationException("Invalid mimic results");return result;
    }
}

// Persistent control wire and ordering.

internal static class ControlProtocol
{
    internal const int Stride=6,MaxRows=320;
    private static readonly HashSet<string> skills=new(StringComparer.Ordinal){"Control","Overtake","KillOvertake","KnockoutOvertake","OvertakeByOvertaken"};
    internal static bool Row(string[] r)=>r!=null&&r.Length==Stride&&SyncProtocol.Identity(r[0])&&skills.Contains(r[1])&&
        int.TryParse(r[2],NumberStyles.None,CultureInfo.InvariantCulture,out int gen)&&gen>0&&r[2]==gen.ToString(CultureInfo.InvariantCulture)&&
        (r[3]=="0"||r[3]=="1")&&(r[4]=="0"||r[4]=="1")&&
        (r[3]=="1"?SyncProtocol.GuardId(r[5]):r[4]=="1"?(r[5]=="-"||SyncProtocol.GuardId(r[5])):r[5]=="-");
    internal static bool Split(string body,out string core,out string[][] rows)
    {
        core=body;rows=null;if(body==null)return false;
        int at=body.LastIndexOf('\n');if(at<0||!body.AsSpan(at+1).StartsWith("PC1 ".AsSpan(),StringComparison.Ordinal))return true;
        var f=body.Substring(at+1).Split(' ');
        if(f.Length<2||!int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n<0||n>MaxRows||f[1]!=n.ToString(CultureInfo.InvariantCulture)||f.Length!=2+n*Stride)return false;
        var parsed=new string[n][];var keys=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<n;i++){var r=f[(2+i*Stride)..(2+(i+1)*Stride)];if(!Row(r)||!keys.Add(r[0]+"/"+r[1]))return false;parsed[i]=r;}
        core=body.Substring(0,at);rows=parsed;return true;
    }
    internal static string Attach(string body,IReadOnlyList<string[]> rows)
    {
        if(rows==null)return body;
        if(body==null||rows.Count>MaxRows)throw new InvalidOperationException("Invalid control snapshot");
        var s=new System.Text.StringBuilder(body).Append("\nPC1 ").Append(rows.Count.ToString(CultureInfo.InvariantCulture));
        foreach(var row in rows){if(!Row(row))throw new InvalidOperationException("Invalid control row");s.Append(' ').Append(string.Join(" ",row));}
        string result=s.ToString();if(!Split(result,out _,out _))throw new InvalidOperationException("Duplicate control row");return result;
    }
}
// Retain terminal generations even when a source is absent in later frames.
internal sealed class ControlResultOrder
{
    private readonly Dictionary<string,string[]> latest=new(StringComparer.Ordinal);
    internal IEnumerable<string[]> Current
    {get{foreach(var row in latest.Values)yield return (string[])row.Clone();}}
    internal void Observe(string[][] rows)
    {if(rows!=null)foreach(var row in rows)Accept(row);}

    internal void Clear()=>latest.Clear();
    internal bool Accept(string[] row)
    {
        if(!ControlProtocol.Row(row))return false;
        string key=row[0]+"/"+row[1];
        if(latest.TryGetValue(key,out var old))
        {
            int a=int.Parse(row[2],CultureInfo.InvariantCulture),b=int.Parse(old[2],CultureInfo.InvariantCulture);
            if(a<b)return false;
            if(a==b)return row[3]==old[3]&&row[4]==old[4]&&row[5]==old[5];
        }
        latest[key]=(string[])row.Clone();return true;
    }
}
// A failed native application is not completion. Repeated host results retry,
// while omitted sources are never resurrected from cached observations.
internal sealed class ControlRecovery
{
    private readonly ControlResultOrder order=new();
    private readonly Dictionary<string,(int generation,float retry)> retries=new(StringComparer.Ordinal);
    internal void Clear(){order.Clear();retries.Clear();}
    internal void Apply(string[][] rows,float now,Func<string,bool> sourceAllowed,Func<string[],bool> matches,Func<string[],bool> apply)
    {
        if(rows==null||!float.IsFinite(now))return;
        foreach(var row in rows)
        {
            if(!order.Accept(row)||!sourceAllowed(row[0]))continue;
            // A preparation cue is not authority to create a controlled actor.
            if(row[3]=="0"&&row[4]=="1")continue;
            string key=row[0]+"/"+row[1];int generation=int.Parse(row[2],CultureInfo.InvariantCulture);
            if(matches(row)){retries.Remove(key);continue;}
            if(retries.TryGetValue(key,out var retry)&&retry.generation==generation&&now<retry.retry)continue;
            retries[key]=(generation,now+1f);
            // Do not latch success: the local controller may later drift, or
            // the native call may return before its asynchronous completion.
            apply((string[])row.Clone());
        }
    }
}

internal static class CharmingGhostProtocol
{
    internal const int Stride=9,MaxActors=64;
    internal static bool Row(string[] r)=>r!=null&&r.Length==Stride&&SyncProtocol.Identity(r[0])&&
        int.TryParse(r[1],NumberStyles.None,CultureInfo.InvariantCulture,out int gen)&&gen>0&&r[1]==gen.ToString(CultureInfo.InvariantCulture)&&
        (r[2]=="N"||r[2]=="R"||r[2]=="D")&&SyncProtocol.Position(r,3)&&
        (r[8]=="0"||r[8]=="1")&&(r[2]=="N"?r[3]=="0"&&r[4]=="0"&&r[5]=="0"&&r[6]=="0"&&r[7]=="-"&&r[8]=="0":SyncProtocol.Health(r[6],r[7]));
    internal static bool Split(string body,out string core,out string[][] rows)
    {
        core=body;rows=null;if(body==null)return false;int at=body.LastIndexOf('\n');
        if(at<0||!body.AsSpan(at+1).StartsWith("CG1 ".AsSpan(),StringComparison.Ordinal))return true;
        var f=body.Substring(at+1).Split(' ');
        if(f.Length<2||!int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n<0||n>MaxActors||f[1]!=n.ToString(CultureInfo.InvariantCulture)||f.Length!=2+n*Stride)return false;
        var parsed=new string[n][];var seen=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<n;i++){var row=f[(2+i*Stride)..(2+(i+1)*Stride)];if(!Row(row)||!seen.Add(row[0]))return false;parsed[i]=row;}
        core=body.Substring(0,at);rows=parsed;return true;
    }
    internal static string Attach(string body,IReadOnlyList<string[]> rows)
    {
        if(rows==null)return body;if(rows.Count>MaxActors)throw new InvalidOperationException("Too many charming ghosts");
        var text=new System.Text.StringBuilder(body).Append("\nCG1 ").Append(rows.Count.ToString(CultureInfo.InvariantCulture));
        foreach(var row in rows)text.Append(' ').Append(string.Join(" ",row));
        string result=text.ToString();if(!CrewRosterProtocol.Read(result,out _,out _))throw new InvalidOperationException("Invalid charming ghost snapshot");return result;
    }
}

internal sealed class CharmingGhostResults
{
    private readonly Dictionary<string,string[]> latest=new(StringComparer.Ordinal);
    internal void Clear()=>latest.Clear();
    internal bool Accept(string[] row)
    {
        if(!CharmingGhostProtocol.Row(row))return false;
        if(latest.TryGetValue(row[0],out var old))
        {
            int next=int.Parse(row[1],CultureInfo.InvariantCulture),prior=int.Parse(old[1],CultureInfo.InvariantCulture);
            if(next<prior||next==prior&&row[2]!=old[2])return false;
        }
        latest[row[0]]=(string[])row.Clone();return true;
    }
    internal void Observe(string[][] rows){if(rows!=null)foreach(var row in rows)Accept(row);}
    internal string[] Get(string source)=>latest.TryGetValue(source,out var row)?(string[])row.Clone():null;
}
