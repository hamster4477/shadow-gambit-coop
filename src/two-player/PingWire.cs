using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

// Cosmetic marker messages only. No actor identity, command or game API.
internal static class PingWire
{
    internal const int MaxBytes=8192, MaxMarks=1, LifetimeMs=5000;
    internal sealed class Mark
    {
        public string id {get;set;}
        public float x {get;set;}
        public float y {get;set;}
        public float z {get;set;}
        public int ageMs {get;set;}
    }
    internal sealed class Message
    {
        public string operation {get;set;}="ping";
        public string world {get;set;}
        public string client {get;set;}
        public long revision {get;set;}
        public Mark[] marks {get;set;}=Array.Empty<Mark>();
    }
    internal static bool Identity(string value)
    {
        if(value==null||value.Length!=32)return false;
        foreach(char c in value)if(!(c>='0'&&c<='9'||c>='a'&&c<='f'))return false;
        return true;
    }
    internal static bool Coordinate(float value)=>float.IsFinite(value)&&Math.Abs(value)<=1000000f;
    internal static bool Valid(Message value)
    {
        if(value==null||value.operation!="ping"||!Identity(value.world)||!Identity(value.client)||value.revision<=0||value.marks==null||value.marks.Length>MaxMarks)return false;
        var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var m in value.marks)
            if(m==null||!Identity(m.id)||!ids.Add(m.id)||!Coordinate(m.x)||!Coordinate(m.y)||!Coordinate(m.z)||m.ageMs<0||m.ageMs>LifetimeMs)return false;
        return true;
    }
    // Exact shape rejects missing numeric fields, duplicate JSON keys and accidental
    // protocol mixing. JsonSerializer defaults alone would silently accept them.
    private static bool Shape(JsonElement element,params string[] names)
    {
        if(element.ValueKind!=JsonValueKind.Object)return false;
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var property in element.EnumerateObject())
            if(Array.IndexOf(names,property.Name)<0||!seen.Add(property.Name))return false;
        return seen.Count==names.Length;
    }
    internal static Message Read(string json)
    {
        if(json==null||json.Length>MaxBytes||Encoding.UTF8.GetByteCount(json)>MaxBytes)return null;
        try
        {
            using var document=JsonDocument.Parse(json);
            var root=document.RootElement;
            if(!Shape(root,"operation","world","client","revision","marks")||root.GetProperty("marks").ValueKind!=JsonValueKind.Array)return null;
            foreach(var mark in root.GetProperty("marks").EnumerateArray())
                if(!Shape(mark,"id","x","y","z","ageMs"))return null;
            var value=JsonSerializer.Deserialize<Message>(json);
            return Valid(value)?value:null;
        }
        catch(JsonException){return null;}
    }
    internal static string Serialize(Message value)
    {
        if(!Valid(value))throw new ArgumentException("Invalid ping message.");
        string json=JsonSerializer.Serialize(value);
        if(Encoding.UTF8.GetByteCount(json)>MaxBytes)throw new ArgumentException("Ping message too large.");
        return json;
    }
}

// Local monotonic clocks only: remote age plus local elapsed time is used for TTL.
// Repeated snapshots cannot restart animation, sound or the five-second lifetime.
internal sealed class PingLedger
{
    internal const double Lifetime=5;
    private const int MaxSeen=512, MaxRetiredClients=16;
    internal sealed class Entry
    {
        internal string Id {get;}
        internal string Client {get;}
        internal bool Local {get;}
        internal float X {get;}
        internal float Y {get;}
        internal float Z {get;}
        internal double Expires {get;}
        internal double Created {get;}
        internal Entry(string id,string client,bool local,float x,float y,float z,double created,double expires)
        {Id=id;Client=client;Local=local;X=x;Y=y;Z=z;Created=created;Expires=expires;}
    }
    private readonly List<Entry> entries=new List<Entry>();
    // A repeated ID refreshes only its tombstone, never its displayed lifetime.
    // When full, new markers are dropped rather than evicting recent tombstones.
    private readonly Dictionary<string,double> seen=new Dictionary<string,double>(StringComparer.Ordinal);
    private readonly HashSet<string> retired=new HashSet<string>(StringComparer.Ordinal);
    private string world,client,peerClient;
    private long revision,peerRevision;
    private static bool Clock(double now)=>double.IsFinite(now)&&now>=0;
    internal void SetContext(string epoch,string localClient)
    {
        if(!PingWire.Identity(epoch)||!PingWire.Identity(localClient))throw new ArgumentException("Invalid ping context.");
        if(world==epoch&&client==localClient)return;
        Clear();world=epoch;client=localClient;
    }
    internal void Clear()
    {entries.Clear();seen.Clear();retired.Clear();world=client=peerClient=null;revision=peerRevision=0;}
    private void Prune(double now)
    {
        entries.RemoveAll(e=>e.Expires<=now);
        if(seen.Count>0){var expired=new List<string>();foreach(var pair in seen)if(pair.Value<=now)expired.Add(pair.Key);foreach(var id in expired)seen.Remove(id);}
    }
    internal string AddLocal(float x,float y,float z,double now)
    {
        if(world==null||!Clock(now)||!PingWire.Coordinate(x)||!PingWire.Coordinate(y)||!PingWire.Coordinate(z))return null;
        Prune(now);entries.RemoveAll(e=>e.Local);
        string id=Guid.NewGuid().ToString("N");entries.Add(new Entry(id,client,true,x,y,z,now,now+Lifetime));return id;
    }
    internal PingWire.Message Capture(double now)
    {
        if(world==null||!Clock(now))return null;
        Prune(now);var marks=new List<PingWire.Mark>();
        foreach(var e in entries)if(e.Local)marks.Add(new PingWire.Mark{id=e.Id,x=e.X,y=e.Y,z=e.Z,ageMs=(int)Math.Clamp((now-e.Created)*1000,0,PingWire.LifetimeMs)});
        if(revision==long.MaxValue)throw new InvalidOperationException("Ping revision exhausted; recreate context.");
        return new PingWire.Message{world=world,client=client,revision=++revision,marks=marks.ToArray()};
    }
    internal IReadOnlyList<Entry> Observe(PingWire.Message incoming,double now,double extraAgeSeconds=0)
    {
        var added=new List<Entry>();
        if(world==null||!Clock(now)||!double.IsFinite(extraAgeSeconds)||extraAgeSeconds<0||!PingWire.Valid(incoming)||incoming.world!=world||incoming.client==client||retired.Contains(incoming.client))return added;
        Prune(now);
        if(peerClient!=incoming.client)
        {
            // Bound reconnect state too; an excessive client rotation cannot grow it.
            if(peerClient!=null){if(retired.Count>=MaxRetiredClients)return added;retired.Add(peerClient);}
            peerClient=incoming.client;peerRevision=0;entries.RemoveAll(e=>!e.Local);seen.Clear();
        }
        if(incoming.revision<=peerRevision)return added;
        peerRevision=incoming.revision;
        // The newer snapshot is authoritative for this peer only. Replacing or
        // clearing their marker must never remove the local player's marker.
        entries.RemoveAll(e=>!e.Local&&(incoming.marks.Length==0||e.Id!=incoming.marks[0].id||incoming.marks[0].ageMs/1000d+extraAgeSeconds>=Lifetime));
        foreach(var mark in incoming.marks)
        {
            if(seen.ContainsKey(mark.id)){seen[mark.id]=now+Lifetime*2;continue;}
            if(seen.Count>=MaxSeen)continue;
            seen.Add(mark.id,now+Lifetime*2);
            double age=mark.ageMs/1000d+extraAgeSeconds;
            if(age>=Lifetime)continue;
            int count=0;foreach(var e in entries)if(!e.Local)count++;
            if(count>=PingWire.MaxMarks)continue;
            var entry=new Entry(mark.id,incoming.client,false,mark.x,mark.y,mark.z,now-age,now+Lifetime-age);
            entries.Add(entry);added.Add(entry);
        }
        return added;
    }
    internal IReadOnlyList<Entry> All(double now)
    {
        if(!Clock(now))return Array.Empty<Entry>();
        Prune(now);return entries.ToArray();
    }
}
