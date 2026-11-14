using System;
using System.Collections.Generic;
using System.Globalization;

internal static class CannonFlightProtocol
{
    internal const int Stride=29,MaxFlights=64;
    private static bool Float(string[] r,int i,float min=0,float max=1000000)=>SyncProtocol.Number(r[i],out float v)&&v>=min&&v<=max;
    private static bool Int(string[] r,int i,int min,int max)=>int.TryParse(r[i],NumberStyles.None,CultureInfo.InvariantCulture,out int n)&&n>=min&&n<=max;
    internal static bool Row(string[] r)
    {
        if(r==null||r.Length!=Stride||!FreezeProtocol.Character(r[0])||!FreezeProtocol.Character(r[2])||r[0]==r[2]||!Int(r,1,1,int.MaxValue)||
            (r[3]!="A"&&r[3]!="E"&&r[3]!="C")||(r[4]!="P"&&r[4]!="S")||(r[5]!="0"&&r[5]!="1")||
            (r[17]!="H"&&r[17]!="L")||!Int(r,28,0,15)||!SyncProtocol.Position(r,18)||!SyncProtocol.Position(r,21)||!SyncProtocol.Position(r,25))return false;
        foreach(int i in new[]{6,7,9,10,13,14,15,16})if(!Float(r,i))return false;
        if(!Float(r,8,0,1))return false;
        if(r[4]=="S")return r[24]=="-"&&r[11]=="0"&&r[12]=="0";
        return FishingPullProtocol.Points(r[24],out var points)&&Int(r,11,0,points.Length-1)&&Int(r,12,0,points.Length-1);
    }
    internal static bool Split(string body,out string core,out string[][] rows)
    {
        core=body;rows=null;if(body==null)return false;int at=body.LastIndexOf('\n');
        if(at<0||!body.AsSpan(at+1).StartsWith("K3 ".AsSpan(),StringComparison.Ordinal))return true;
        var f=body.Substring(at+1).Split(' ');
        if(f.Length<2||!int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n>MaxFlights||f.Length!=2+n*Stride)return false;
        var parsed=new string[n][];var ids=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<n;i++){var row=f[(2+i*Stride)..(2+(i+1)*Stride)];if(!Row(row)||!ids.Add(row[0]+":"+row[1]))return false;parsed[i]=row;}
        rows=parsed;core=body.Substring(0,at);return true;
    }
    internal static string Attach(string body,IReadOnlyList<string[]> rows)
    {
        if(rows==null)return body;var text=new System.Text.StringBuilder(body);text.Append("\nK3 ").Append(rows.Count.ToString(CultureInfo.InvariantCulture));
        foreach(var row in rows)text.Append(' ').Append(string.Join(" ",row));string result=text.ToString();
        if(!CrewRosterProtocol.Read(result,out _,out _))throw new InvalidOperationException("Invalid cannon flight manifest");return result;
    }
}
