using System;
using System.Collections.Generic;
using System.Text.Json;

// Bounded, scene-scoped full queues. No native pointers or callbacks on wire.
internal static class PlanningWire
{
    internal sealed class Command
    {
        public string skill{get;set;} public int flags{get;set;}
        public bool target{get;set;} public string entity{get;set;} public string transform{get;set;}
        public string transformEntity{get;set;}
        public float[] point{get;set;} public float[] skillPoint{get;set;} public float[] offset{get;set;} public float[] alternate{get;set;}
        public int movement{get;set;} public float stopping{get;set;} public int modifier{get;set;} public bool update{get;set;}
        public bool data{get;set;} public bool value{get;set;} public int type{get;set;}
        public string origin{get;set;} public float[] dataPoint{get;set;}
    }
    internal sealed class Step
    {
        public Command command{get;set;} public Command[] chain{get;set;}=Array.Empty<Command>();
        public bool crouch{get;set;} public bool execute{get;set;} public int variant{get;set;}
        public int mouse{get;set;} public int cancel{get;set;} public int controller{get;set;} public float[] executionTarget{get;set;}
    }
    internal sealed class Queue
    {public string actor{get;set;} public Step[] steps{get;set;}=Array.Empty<Step>();}
    internal sealed class Message
    {
        public string operation{get;set;}="planning";public string client{get;set;}public string world{get;set;} public long revision{get;set;}
        public bool active{get;set;} public Queue[] queues{get;set;}=Array.Empty<Queue>();
        public string requestActor{get;set;} public string executedActor{get;set;} public string request{get;set;} public string barrier{get;set;} public string executed{get;set;}
    }
    internal static bool Token(string s)=>s==null||SyncProtocol.Session(s);
    private static bool Vector(float[] p)=>p!=null&&p.Length==3&&Array.TrueForAll(p,n=>float.IsFinite(n)&&Math.Abs(n)<=100000);
    private static bool Key(string s)=>s==null||s.Length<=160&&s.Length>0&&Array.TrueForAll(s.ToCharArray(),c=>(c>='a'&&c<='z'||c>='A'&&c<='Z'||c>='0'&&c<='9')||c=='-');
    private static bool Valid(Command c)=>c!=null&&c.skill!=null&&c.skill.Length<=80&&c.skill.Length>0&&
        Array.TrueForAll(c.skill.ToCharArray(),c=>(c>='a'&&c<='z'||c>='A'&&c<='Z'||c>='0'&&c<='9')||c=='_')&&
        Key(c.entity)&&Key(c.origin)&&Key(c.transformEntity)&&(c.transform==null||SyncProtocol.Identity(c.transform))&&
        (!c.target||Vector(c.point)&&Vector(c.skillPoint)&&Vector(c.offset)&&Vector(c.alternate))&&
        (!c.data||c.target&&Vector(c.dataPoint))&&float.IsFinite(c.stopping)&&Math.Abs(c.stopping)<=100000;
    internal static bool Valid(Message m)
    {
        if(m==null||m.requestActor!=null&&!SyncProtocol.Identity(m.requestActor)||m.executedActor!=null&&!SyncProtocol.Identity(m.executedActor)||m.operation!="planning"||!SyncProtocol.Session(m.client)||!SyncProtocol.Session(m.world)||m.revision<1||!Token(m.request)||!Token(m.barrier)||!Token(m.executed)||m.queues==null||m.queues.Length>16)return false;
        var ids=new HashSet<string>();int count=0;
        foreach(var q in m.queues)
        {
            if(q==null||!SyncProtocol.Identity(q.actor)||!ids.Add(q.actor)||q.steps==null||q.steps.Length>16)return false;
            foreach(var s in q.steps)
            {
                if(s==null||s.executionTarget!=null&&!Vector(s.executionTarget)||!Valid(s.command)||s.chain==null||s.chain.Length>8)return false;
                count+=1+s.chain.Length;if(count>32)return false;
                foreach(var c in s.chain)if(!Valid(c))return false;
            }
        }
        return JsonSerializer.Serialize(m).Length<=30000;
    }
}
