using System;
using System.Collections.Generic;
using System.Globalization;

internal sealed class GuestOrderVersions
{
    private readonly Dictionary<string,long> sequences=new Dictionary<string,long>(StringComparer.Ordinal);
    private string session;
    internal string Encode(string generation,string actor,string command,long revision,long expectedRevision=0)
    {
        if(!SyncProtocol.Session(generation)||!SyncProtocol.Identity(actor)||command==null||revision<0||expectedRevision<0||
            expectedRevision>0&&expectedRevision!=revision)return null;
        if(session!=generation){session=generation;sequences.Clear();}
        if(revision==0 || command=="CLAIM")return SyncProtocol.Command(actor+" "+generation+" "+command,out _)?command:null;
        sequences.TryGetValue(actor,out long sequence);if(sequence==long.MaxValue)return null;
        string wrapped=command+" Q "+revision.ToString(CultureInfo.InvariantCulture)+" "+(sequence+1).ToString(CultureInfo.InvariantCulture);
        if(!SyncProtocol.Command(actor+" "+generation+" "+wrapped,out _))return null;
        sequences[actor]=sequence+1;return wrapped;
    }
}
