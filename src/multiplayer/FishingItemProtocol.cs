using System;
using System.Collections.Generic;
using System.Globalization;

internal static class FishingItemProtocol
{
    internal const int Stride=8,MaxRows=32;
    internal static bool Row(string[] r)=>r!=null&&r.Length==Stride&&FreezeProtocol.Character(r[0])&&
        int.TryParse(r[1],NumberStyles.None,CultureInfo.InvariantCulture,out int serial)&&serial>0&&r[1]==serial.ToString(CultureInfo.InvariantCulture)&&
        r[2]!="-"&&InteractionProtocol.EntityKey(r[2])&&(r[3]=="A"||r[3]=="E"||r[3]=="C")&&SyncProtocol.Position(r,4)&&(r[7]=="I"||r[7]=="P");
    internal static string Key(string[] r)=>r[0]+":"+r[1]+":"+r[2];
    internal static bool Split(string body,out string core,out string[][] rows)
    {
        core=body;rows=null;if(body==null)return false;int at=body.LastIndexOf('\n');
        if(at<0||!body.AsSpan(at+1).StartsWith("FI1 ".AsSpan(),StringComparison.Ordinal))return true;
        var f=body.Substring(at+1).Split(' ');
        if(f.Length<2||!int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n>MaxRows||f[1]!=n.ToString(CultureInfo.InvariantCulture)||f.Length!=2+n*Stride)return false;
        rows=new string[n][];var keys=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<n;i++){var r=f[(2+i*Stride)..(2+(i+1)*Stride)];if(!Row(r)||!keys.Add(Key(r)))return false;rows[i]=r;}
        core=body.Substring(0,at);return true;
    }
    internal static string Attach(string body,IReadOnlyList<string[]> rows)
    {
        if(rows==null)return body;if(rows.Count>MaxRows)throw new InvalidOperationException("Too many item pulls");
        var text=new System.Text.StringBuilder(body).Append("\nFI1 ").Append(rows.Count.ToString(CultureInfo.InvariantCulture));
        var keys=new HashSet<string>(StringComparer.Ordinal);
        foreach(var row in rows){if(!Row(row)||!keys.Add(Key(row)))throw new InvalidOperationException("Invalid item pull");text.Append(' ').Append(string.Join(" ",row));}
        return text.ToString();
    }
}
