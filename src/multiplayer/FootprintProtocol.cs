using System;
using System.Collections.Generic;
using System.Globalization;

// Optional S19 tail. A complete ID manifest removes vanished prints immediately;
// bounded detail pages carry new prints first and refresh older lifetimes.
internal static class FootprintProtocol
{
    internal const int Stride=12, MaxActive=512, MaxPage=16;
    internal sealed class Page
    {
        internal bool Complete;
        internal HashSet<int> Active=new();
        internal string[][] Rows;
    }
    internal static bool Read(string line,out Page page)
    {
        page=null;
        if(line==null||line.Length>12000)return false;
        var f=line.Split(' ');
        if(f.Length<4||f[0]!="F1"||(f[1]!="0"&&f[1]!="1")||
            !int.TryParse(f[3],NumberStyles.None,CultureInfo.InvariantCulture,out int n)||n>MaxPage||f.Length!=4+n*Stride)return false;
        var p=new Page{Complete=f[1]=="1",Rows=new string[n][]};
        if(f[2]!="-")foreach(string token in f[2].Split(','))
            if(!int.TryParse(token,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int id)||!p.Active.Add(id)||p.Active.Count>MaxActive)return false;
        var seen=new HashSet<int>();
        for(int i=0;i<n;i++)
        {
            var row=f[(4+i*Stride)..(4+(i+1)*Stride)];
            if(!int.TryParse(row[0],NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int id)||!p.Active.Contains(id)||!seen.Add(id)||
                !SyncProtocol.Identity(row[1])||(row[2]!="0"&&row[2]!="1")||!SyncProtocol.Position(row,3)||
                !SyncProtocol.Number(row[10],out float life)||life<=0||life>600||(row[11]!="0"&&row[11]!="1"))return false;
            float length=0;
            for(int q=6;q<10;q++){if(!SyncProtocol.Number(row[q],out float v))return false;length+=v*v;}
            if(length<0.98f||length>1.02f)return false;
            p.Rows[i]=row;
        }
        page=p;return true;
    }
    internal static bool Split(string body,out string core,out Page page)
    {
        core=body;page=null;if(body==null)return false;
        int start=body.LastIndexOf('\n');
        if(start<0||!body.AsSpan(start+1).StartsWith("F1 ".AsSpan(),StringComparison.Ordinal))return true;
        if(!Read(body.Substring(start+1),out page))return false;
        core=body.Substring(0,start);return true;
    }
}
