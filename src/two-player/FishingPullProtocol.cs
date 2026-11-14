using System;
using System.Collections.Generic;
using System.Globalization;

internal static class FishingPullProtocol
{
    internal const int Stride=22,MaxFlights=64,MaxPoints=512;
    internal readonly struct Point { internal readonly float x,y,z;internal Point(float x,float y,float z){this.x=x;this.y=y;this.z=z;} }
    private static bool Float(string[] r,int i,float min,float max)=>SyncProtocol.Number(r[i],out float v)&&v>=min&&v<=max;
    private static bool Int(string[] r,int i,int min,int max)=>int.TryParse(r[i],NumberStyles.None,CultureInfo.InvariantCulture,out int v)&&v>=min&&v<=max;
    // Fixed little-endian IEEE754 coordinates retain native path precision.
    // Even the maximum 512-point route fits in one 12KB trajectory page.
    internal static string Encode(IReadOnlyList<Point> points)
    {
        if(points==null||points.Count<2||points.Count>MaxPoints)throw new ArgumentException("Invalid fishing point count");
        var bytes=new byte[points.Count*12];
        for(int i=0;i<points.Count;i++)
        {
            var p=points[i];if(!Coordinate(p.x)||!Coordinate(p.y)||!Coordinate(p.z))throw new ArgumentException("Invalid fishing coordinate");
            Write(bytes,i*12,p.x);Write(bytes,i*12+4,p.y);Write(bytes,i*12+8,p.z);
        }
        return "b"+Convert.ToBase64String(bytes);
    }
    private static bool Coordinate(float n)=>float.IsFinite(n)&&Math.Abs(n)<=1000000f;
    private static void Write(byte[] bytes,int offset,float value)
    {uint bits=unchecked((uint)BitConverter.SingleToInt32Bits(value));for(int j=0;j<4;j++)bytes[offset+j]=(byte)(bits>>(j*8));}
    private static float Read(byte[] bytes,int offset)
    {uint bits=0;for(int j=0;j<4;j++)bits|=(uint)bytes[offset+j]<<(j*8);return BitConverter.Int32BitsToSingle(unchecked((int)bits));}
    internal static bool Points(string wire,out Point[] points)
    {
        points=null;if(wire==null||wire.Length>24000)return false;
        if(wire.StartsWith("b",StringComparison.Ordinal))
        {
            if(wire.Length>1+MaxPoints*16)return false;
            byte[] bytes;try{bytes=Convert.FromBase64String(wire.Substring(1));}catch(FormatException){return false;}
            if(bytes.Length<24||bytes.Length%12!=0||wire.Substring(1)!=Convert.ToBase64String(bytes))return false;
            var decoded=new Point[bytes.Length/12];
            for(int i=0;i<decoded.Length;i++)
            {float x=Read(bytes,i*12),y=Read(bytes,i*12+4),z=Read(bytes,i*12+8);if(!Coordinate(x)||!Coordinate(y)||!Coordinate(z))return false;decoded[i]=new(x,y,z);}
            points=decoded;return true;
        }
        var parts=wire.Split(';');if(parts.Length<2||parts.Length>MaxPoints)return false;
        var values=new Point[parts.Length];
        for(int i=0;i<parts.Length;i++)
        {var p=parts[i].Split(',');if(p.Length!=3||!SyncProtocol.Position(p,0))return false;values[i]=new(float.Parse(p[0],CultureInfo.InvariantCulture),float.Parse(p[1],CultureInfo.InvariantCulture),float.Parse(p[2],CultureInfo.InvariantCulture));}
        points=values;return true;
    }
    internal static bool Row(string[] r)
    {
        if(r==null||r.Length!=Stride||!FreezeProtocol.Character(r[0])||!Int(r,1,1,int.MaxValue)||!FreezeProtocol.Character(r[2])||
            r[0]==r[2]||(r[3]!="A"&&r[3]!="E"&&r[3]!="C")||(r[4]!="0"&&r[4]!="1")||r[19]!="C"||!Int(r,21,1,int.MaxValue))return false;
        bool terminal=r[3]!="A"&&r[20]=="-";
        int length=MaxPoints;if(!terminal){if(!Points(r[20],out var points))return false;length=points.Length;}
        if(!Float(r,5,.00001f,3600)||!Float(r,6,0,86400)||!Float(r,7,0,1000000)||!Float(r,8,0,1)||
            !Float(r,11,0,1000000)||!Int(r,12,2,length)||!SyncProtocol.Position(r,13)||!SyncProtocol.Position(r,16))return false;
        int count=int.Parse(r[12],CultureInfo.InvariantCulture);
        // A terminal record at the last index never traverses intermediate
        // points again. Retain native count/indices and endpoint, omit the
        // already-traversed route to keep retransmission small.
        if(terminal&&((r[3]=="E"&&r[9]!=(count-1).ToString(CultureInfo.InvariantCulture))||r[10]!=r[9]||
            r[13]!=r[16]||r[14]!=r[17]||r[15]!=r[18]))return false;
        return Int(r,9,0,count-1)&&Int(r,10,0,count-1)&&(r[3]=="A"?int.Parse(r[9],CultureInfo.InvariantCulture)<count-1:true);
    }
    internal static bool Split(string body,out string core,out string[][] rows)
    {
        core=body;rows=null;if(body==null)return false;int at=body.LastIndexOf('\n');
        if(at<0||!body.AsSpan(at+1).StartsWith("Y1 ".AsSpan(),StringComparison.Ordinal))return true;
        var f=body.Substring(at+1).Split(' ');
        if(f.Length<2||!int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n>MaxFlights||f.Length!=2+n*Stride)return false;
        // One caster may have a new pull while an earlier completion is still
        // being retransmitted. Identity is caster + generation, not caster.
        var parsed=new string[n][];var casts=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<n;i++){var row=f[(2+i*Stride)..(2+(i+1)*Stride)];if(!Row(row)||!casts.Add(row[0]+":"+row[1]))return false;parsed[i]=row;}
        rows=parsed;core=body.Substring(0,at);return true;
    }
    internal static string Attach(string body,IReadOnlyList<string[]> rows)
    {
        if(rows==null)return body;var text=new System.Text.StringBuilder(body);text.Append("\nY1 ").Append(rows.Count.ToString(CultureInfo.InvariantCulture));
        foreach(var row in rows)text.Append(' ').Append(string.Join(" ",row));string result=text.ToString();
        if(!CrewRosterProtocol.Read(result,out _,out _))throw new InvalidOperationException("Invalid fishing trajectory page");return result;
    }
}
