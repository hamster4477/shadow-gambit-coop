using System;
using System.Collections.Generic;
using System.Globalization;

// Last optional snapshot tail, separate from playable ownership and enemy AI.
internal static class ShipRoutineProtocol
{
    internal const int MaxRows=64,MaxLength=14000,Stride=6;
    internal static bool Split(string body,out string core,out string[][] rows)
    {
        core=body;rows=Array.Empty<string[]>();if(body==null)return false;
        int at=body.LastIndexOf('\n');
        if(at<0||!body.Substring(at+1).StartsWith("SH1 ",StringComparison.Ordinal))return true;
        string line=body.Substring(at+1);if(line.Length>MaxLength)return false;
        var f=line.Split(' ');
        if(f.Length<2||!int.TryParse(f[1],NumberStyles.None,CultureInfo.InvariantCulture,out int count)||count>MaxRows||f.Length!=2+count*Stride)return false;
        var result=new string[count][];var seen=new HashSet<string>();
        for(int i=0;i<count;i++)
        {
            var r=new string[Stride];Array.Copy(f,2+i*Stride,r,0,Stride);
            if(!SyncProtocol.Identity(r[0])||!seen.Add(r[0])||!SyncProtocol.Position(r,1)||
                !SyncProtocol.Number(r[4],out float yaw)||yaw<0||yaw>=360||(r[5]!="0"&&r[5]!="1"))return false;
            result[i]=r;
        }
        core=body.Substring(0,at);rows=result;return true;
    }
}
