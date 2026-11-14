$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$source="using System;`nusing System.Collections.Generic;`nusing System.Net;`nusing System.Net.Sockets;`nusing System.Text;`n"
foreach($file in 'MultiPeerContext.cs','LoopbackProtocol.cs','LoopbackMove.cs','PeerPace.cs'){$source+=([IO.File]::ReadAllText((Join-Path $root $file))-replace '(?m)^using [^;]+;\r?\n','')+"`n"}
$source+=@'
internal static class SyncProtocol{internal const int ControlMessageMaxBytes=4096;internal static bool Session(string s)=>MultiPeerContext.Valid(s);internal static bool Command(string s,out string[] f){f=s.Split(' ');return true;}}
internal static class SnapshotTransport{internal static bool State(string s)=>false;}
public static class MultiTransportTests{
 static int checks;static void Check(bool b,string message){if(!b)throw new Exception(message);checks++;}
 static string Id(int n)=>n.ToString("x32");
 public static int Run(){
  Check(MultiPeerContext.Current=="guest","default legacy scope");using(MultiPeerContext.Scope(Id(1))){Check(MultiPeerContext.Current==Id(1),"scope entered");try{using(MultiPeerContext.Scope(Id(2))){Check(MultiPeerContext.Current==Id(2),"nested scope entered");throw new Exception();}}catch{}Check(MultiPeerContext.Current==Id(1),"nested exceptional scope restores caller");}Check(MultiPeerContext.Current=="guest","scope disposed");
  string child=null;using(MultiPeerContext.Scope(Id(1))){var thread=new System.Threading.Thread(()=>child=MultiPeerContext.Current);thread.Start();thread.Join();}Check(child=="guest","thread-local peer cannot leak across worker threads");
  foreach(string bad in new[]{"", "host",new string('g',32),new string('A',32),new string('0',31),new string('0',33)})Check(!MultiPeerContext.Valid(bad),"malformed gateway peer rejected");
  using(var host=new LoopbackMove(0))using(var client=new UdpClient(AddressFamily.InterNetwork)){
   host.Arm();client.Client.ReceiveTimeout=1000;var address=new IPEndPoint(IPAddress.Loopback,host.BoundPort);int executed=0;string scope=null,seenPeer=null;
   string Exchange(string peer,string request){var bytes=Encoding.ASCII.GetBytes((peer==null?"":"PEER "+peer+" ")+request);client.Send(bytes,bytes.Length,address);for(int i=0;i<50&&!client.Client.Poll(0,SelectMode.SelectRead);i++){host.Poll(()=>{scope=MultiPeerContext.Current;executed++;return true;},d=>{scope=MultiPeerContext.Current;executed++;return true;},p=>{scope=MultiPeerContext.Current;executed++;return true;},()=>"state-"+MultiPeerContext.Current,(s,n,p)=>seenPeer=MultiPeerContext.Current);System.Threading.Thread.Sleep(1);}var endpoint=new IPEndPoint(IPAddress.Any,0);try{return Encoding.ASCII.GetString(client.Receive(ref endpoint));}catch(Exception ex){throw new Exception("Request "+peer+" "+request+" failed",ex);}}
   var tokens=new Dictionary<string,string>();for(int i=1;i<=64;i++){string peer=Id(i);tokens[peer]=Exchange(peer,"CONTROL").Substring(8);Check(Exchange(peer,"STATE")=="state-"+peer,"state callback receives exact peer context");}
   Check(new HashSet<string>(tokens.Values).Count==64,"tickets isolated for 64 simultaneous peers");
   string order="STEP "+tokens[Id(1)]+" up";Check(Exchange(Id(2),order)=="NOT_READY"&&executed==0,"another peer cannot spend first peer token");
   Check(Exchange(Id(1),order)=="ACCEPTED"&&scope==Id(1)&&executed==1,"original token owner executes exactly once");
   Check(Exchange(Id(1),order)=="ACCEPTED"&&executed==1,"retry returns prior receipt without duplicate action");
   for(int i=2;i<=64;i++)Check(Exchange(Id(i),"STEP "+tokens[Id(i)]+" left")=="ACCEPTED"&&scope==Id(i),"other peer queues unaffected by first peer ticket rotation");
   Check(executed==64,"one execution per independent peer");
   Check(Exchange(Id(3),"STATE "+Id(99)+" 8 P")=="state-"+Id(3)&&seenPeer==Id(3),"receipt uses sending peer scope rather than supplied session string");
   Check(Exchange(null,"CONTROL").StartsWith("CONTROL ")&&MultiPeerContext.Current=="guest","legacy path supported and native scope restored after poll");
   host.Arm();Check(Exchange(Id(1),order)=="NOT_READY","rearm invalidates every peer old ticket");
  }
  var pace=new MultiPeerPace();void Publish(int peer,int seq,float at,bool hold=false){using(MultiPeerContext.Scope(Id(peer)))pace.Publish(seq,at,hold);}void Observe(int peer,int seq,char phase,float at){using(MultiPeerContext.Scope(Id(peer)))pace.Observe(seq,phase,at);}
  for(int i=0;i<4;i++)foreach(int peer in new[]{1,2}){Publish(peer,i+1,i*.3f);Observe(peer,i+1,'R',i*.3f+.01f);}
  Publish(1,5,2.7f);Observe(1,5,'R',2.71f);pace.Tick(2.72f,true);Check(pace.Crew&&!pace.World,"one stalled peer holds crew while fast peer remains fresh");
  pace.Tick(2.93f,true);Check(pace.World,"fast peer receipts cannot prevent another peer world hold");
  Publish(1,6,3f);Observe(1,6,'P',3.01f);Publish(2,5,3.1f,true);Observe(2,5,'H',3.11f);Publish(2,6,3.2f,true);Observe(2,6,'H',3.21f);pace.Tick(3.22f,true);
  Check(!pace.World&&pace.PeerPaused,"slow peer recovery cannot clear another peer explicit pause");
  Publish(1,7,3.3f);Observe(1,7,'R',3.31f);pace.Tick(3.32f,true);Check(!pace.PeerPaused,"only pausing peer receipt resumes its pause");
  for(int i=3;i<=64;i++){Publish(i,1,4);Observe(i,1,i==64?'P':'R',4.01f);}pace.Tick(4.02f,true);Check(pace.PeerPaused,"64th peer participates in pause aggregation");pace.Tick(35f,true);Check(!pace.PeerPaused&&!pace.World&&!pace.Crew,"expired peers no longer hold room");pace.Reset();Check(!pace.PeerPaused,"reset clears all peer state");
  return checks;
 }
}
'@
Add-Type -TypeDefinition $source
"Multi-peer context, loopback ticket and pace checks: $([MultiTransportTests]::Run()). Real loopback sockets and production transport; command payload validator stubbed (STEP path tested)."
