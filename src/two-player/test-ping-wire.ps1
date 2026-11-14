$ErrorActionPreference='Stop'
$source=Get-Content (Join-Path $PSScriptRoot 'PingWire.cs') -Raw
$fixture=@'
public static class PingTests
{
 static int checks;
 static void Check(bool ok,string name){checks++;if(!ok)throw new System.Exception(name);}
 static string World=new string('a',32),Local=new string('b',32),Remote=new string('c',32),Id=new string('d',32);
 static PingWire.Message Message(long revision=1,int age=0,string id=null)=>new PingWire.Message{world=World,client=Remote,revision=revision,marks=new[]{new PingWire.Mark{id=id??Id,x=1,y=2,z=3,ageMs=age}}};
 static PingLedger Ledger(){var l=new PingLedger();l.SetContext(World,Local);return l;}
 public static int Run()
 {
  var wire=Message();string json=PingWire.Serialize(wire);Check(PingWire.Read(json)!=null,"Wire roundtrip");
  Check(PingWire.Read(json.Replace("\"ageMs\":0", "\"ageMs\":0,\"extra\":1"))==null,"Unknown marker field rejected");
  Check(PingWire.Read(json.Replace("\"ageMs\":0", "\"ageMs\":0,\"ageMs\":0"))==null,"Duplicate marker field rejected");
  Check(PingWire.Read(json.Replace("\"x\":1,", ""))==null,"Missing coordinate rejected");
  Check(PingWire.Read(json.Replace("\"marks\":", "\"extra\":0,\"marks\":"))==null,"Unknown envelope field rejected");
  Check(PingWire.Read(json.Replace("\"revision\":1", "\"revision\":1,\"revision\":2"))==null,"Duplicate revision rejected");
  Check(PingWire.Read("[]")==null&&PingWire.Read("null")==null&&PingWire.Read(new string(' ',8193))==null,"Malformed and oversized rejected");
  wire.client=Remote.ToUpperInvariant();Check(!PingWire.Valid(wire),"Uppercase identity rejected");wire.client=Remote;
  wire.marks[0].x=float.NaN;Check(!PingWire.Valid(wire),"NaN rejected");wire.marks[0].x=float.PositiveInfinity;Check(!PingWire.Valid(wire),"Infinity rejected");wire.marks[0].x=1000001;Check(!PingWire.Valid(wire),"Oversized coordinate rejected");wire.marks[0].x=-1000000;Check(PingWire.Valid(wire),"Finite coordinate boundary accepted");
  wire.marks[0].ageMs=-1;Check(!PingWire.Valid(wire),"Negative age rejected");wire.marks[0].ageMs=5001;Check(!PingWire.Valid(wire),"Expired age beyond bound rejected");wire.marks[0].ageMs=5000;Check(PingWire.Valid(wire),"Exactly expired wire age accepted for safe dropping");
  wire=Message();wire.marks=new[]{wire.marks[0],wire.marks[0]};Check(!PingWire.Valid(wire),"Duplicate IDs rejected");wire.marks=new PingWire.Mark[2];Check(!PingWire.Valid(wire),"More than one marker rejected");wire=Message();wire.revision=0;Check(!PingWire.Valid(wire),"Zero revision rejected");
  var local=Ledger();string first=local.AddLocal(1,2,3,0);Check(first!=null,"First local ping accepted");string second=local.AddLocal(4,5,6,0);Check(second!=null&&second!=first,"Same-frame replacement has no cooldown");Check(local.All(0).Count==1&&local.All(0)[0].Id==second,"Replacement removes only previous local marker");
  var capture=local.Capture(1);Check(capture.marks.Length==1&&capture.marks[0].ageMs==1000&&capture.marks[0].id==second&&capture.client==Local,"Capture latest local marker only");Check(local.Capture(1).revision>capture.revision,"Publish revision monotonic");Check(local.All(4.99).Count==1&&local.All(5).Count==0,"Latest local marker expires after five seconds");
  for(int i=0;i<8;i++)Check(local.AddLocal(i,2,3,10)!=null,"Unlimited local replacement "+i);Check(local.All(10).Count==1&&local.All(10)[0].X==7,"Rapid clicks retain only latest local position");Check(local.AddLocal(float.NaN,2,3,20)==null,"Invalid local coordinate rejected");Check(local.AddLocal(1,2,3,double.NaN)==null,"Invalid clock rejected");
  var guest=Ledger();var newMarks=guest.Observe(Message(),100);Check(newMarks.Count==1&&!newMarks[0].Local&&newMarks[0].Expires==105,"Peer clock offset does not matter");
  var duplicate=Message(2);Check(guest.Observe(duplicate,101).Count==0,"Duplicate never repeats sound");Check(guest.All(101)[0].Expires==105,"Duplicate never extends lifetime");Check(guest.Observe(Message(1,id:new string('e',32)),102).Count==0,"Old revision ignored");Check(guest.All(105).Count==0,"Remote expires exactly on original local deadline");Check(guest.Observe(Message(3),106).Count==0,"Repeated retired marker does not resurrect");
  var both=Ledger();string own=both.AddLocal(1,2,3,10);both.Observe(Message(),10);Check(both.All(10).Count==2,"Each player may have one marker");
  own=both.AddLocal(9,8,7,10);Check(both.All(10).Count==2&&both.All(10)[0].Id==Id,"Local replacement preserves remote marker");
  var replacement=Message(2,id:new string('e',32));var replaced=both.Observe(replacement,11);Check(replaced.Count==1&&both.All(11).Count==2,"Remote replacement creates one new marker without touching local");Check(!System.Linq.Enumerable.Any(both.All(11),e=>e.Id==Id),"Remote old marker removed immediately");
  var empty=Message(3);empty.marks=System.Array.Empty<PingWire.Mark>();Check(both.Observe(empty,12).Count==0&&both.All(12).Count==1&&both.All(12)[0].Id==own,"Peer empty snapshot clears peer only");Check(both.Observe(Message(4),12).Count==0&&both.All(12).Count==1,"Old remote ID does not revive after replacement");
  var aged=Ledger();var ageMarks=aged.Observe(Message(age:2000),200,1);Check(ageMarks.Count==1&&ageMarks[0].Expires==202,"Source age and local file age both subtracted");var stale=Ledger();Check(stale.Observe(Message(age:5000),200).Count==0,"Already expired marker not shown");Check(stale.Observe(Message(2),201,6).Count==0,"Overaged message ignored");
  var reflected=Message();reflected.client=Local;Check(aged.Observe(reflected,201).Count==0,"Local reflection rejected");var foreign=Message(3);foreign.world=new string('f',32);Check(aged.Observe(foreign,201).Count==0,"Wrong scene rejected");Check(aged.Observe(Message(4),201,-1).Count==0,"Negative transport age rejected");
  var reconnect=Ledger();reconnect.Observe(Message(),1);var joined=Message();joined.client=new string('e',32);Check(reconnect.Observe(joined,2).Count==1&&reconnect.All(2).Count==1,"New client replaces old peer markers");Check(reconnect.Observe(Message(10),3).Count==0,"Old disconnected client cannot return via delayed packet");
  reconnect.SetContext(new string('f',32),Local);Check(reconnect.All(3).Count==0,"Scene context clears markers");Check(reconnect.Observe(joined,3).Count==0,"Previous scene packets do not reappear");reconnect.Clear();Check(reconnect.Capture(4)==null&&reconnect.AddLocal(1,2,3,4)==null,"Disconnected ledger inert");
  var max=Ledger();for(int i=0;i<520;i++){var m=Message(i+1,id:i.ToString("x32"));max.Observe(m,0);}
  Check(max.All(0).Count<=1,"Remote render count bounded under flood");Check(max.Observe(Message(600,id:"00000000000000000000000000000000"),5.1).Count==0,"Flood cannot evict recent tombstones");
  return checks;
 }
}
'@
Add-Type -TypeDefinition ($source+"`n"+$fixture)
"Ping protocol and lifetime checks passed: $([PingTests]::Run()). Pure .NET, no game actions."
