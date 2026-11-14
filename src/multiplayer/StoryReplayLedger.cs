using System;
using System.Collections.Generic;

// Locally executed native commands consume their matching host event once.
internal sealed class StoryReplayLedger
{
    private readonly Dictionary<string,int> pending=new(StringComparer.Ordinal);
    internal void Record(string kind,string key)
    {
        string token=kind+":"+key;
        if(!pending.ContainsKey(token)&&pending.Count>=256)return;
        pending.TryGetValue(token,out int count);pending[token]=Math.Min(64,count+1);
    }
    internal bool Consume(string kind,string key)
    {
        string token=kind+":"+key;
        if(!pending.TryGetValue(token,out int count))return false;
        if(count<=1)pending.Remove(token);else pending[token]=count-1;
        return true;
    }
    internal void Clear()=>pending.Clear();
}
