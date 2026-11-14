using System;
using System.Collections.Generic;

// Host ownership arrives atomically with a validated full roster. Native
// enumeration order is not ownership: compare identities, never slot vectors.
internal sealed class CrewOwnershipReplica
{
    private readonly Dictionary<string,char> owners=new Dictionary<string,char>(StringComparer.Ordinal);
    internal long Revision{get;private set;}
    internal char Owner(string id)=>id!=null&&owners.TryGetValue(id,out var code)?code:'U';
    internal bool CanApply(long revision,IReadOnlyList<string> actors,string codes,bool spectator=false)
    {
        if(codes==null)return revision==0&&Revision==0;
        if(revision<=0||revision<Revision||actors==null||actors.Count<1||actors.Count>64||codes.Length!=actors.Count||codes[0]!='H'||(spectator?codes.IndexOf('G')>=0:actors.Count<2||codes[1]!='G'))return false;
        if(codes.Length==1&&codes!="H")return false;
        if(revision==Revision && actors.Count!=owners.Count)return false;
        var unique=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<actors.Count;i++)
            if(!SyncProtocol.Identity(actors[i])||!unique.Add(actors[i])||"HGU".IndexOf(codes[i])<0||
                revision==Revision&&(!owners.TryGetValue(actors[i],out var old)||old!=codes[i]))return false;
        return true;
    }
    internal bool Apply(long revision,IReadOnlyList<string> actors,string codes,bool spectator=false)
    {
        if(!CanApply(revision,actors,codes,spectator))return false;
        if(codes==null)return true;
        owners.Clear();for(int i=0;i<actors.Count;i++)owners.Add(actors[i],codes[i]);Revision=revision;return true;
    }
}
