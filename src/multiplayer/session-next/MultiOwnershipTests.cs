namespace HarmonyLib{public class HarmonyPatch:Attribute{public HarmonyPatch(Type t,string name){}public HarmonyPatch(Type t,string name,Type[] args){}}}
internal static class MultiPeerContext{internal static bool Enabled=true;internal static string Current="guest";}
internal static class SyncProtocol{internal static bool Identity(string s)=>s!=null&&s.Length==64;}
internal static class GuestMove{internal static string Identity(MiCharacter c)=>c?.id;}
internal static class GuestCrewSelection{internal static MiCharacter Source(MiCharacter c)=>c;}
public class MiCharacter{public string id;public MiCharacterControllerPlayer controllerPlayer;public MiCharacter(int n){id=n.ToString("X64");controllerPlayer=new(){character=this,m_selectable=new(){character=this}};}}
public class MiCharacterControllerPlayer{public MiCharacter character;public MiSelectable m_selectable;}
public class SkillCommand{}
public class MiSelectable{public MiCharacter character;public struct SelectOptions{public SelectOptions(bool value){}}}
public class MiPlayerInput{public List<MiCharacter> lSelectedCharacter=new();public MiCharacter leader;public SelectHandler m_selectHandler;public MiPlayerInput(){m_selectHandler=new(){input=this};}}
public class SelectHandler{public MiPlayerInput input;public void deselectAll(MiSelectable.SelectOptions o){input.lSelectedCharacter.Clear();input.leader=null;}public void select(MiSelectable s,MiSelectable.SelectOptions o){input.lSelectedCharacter.Add(s.character);}public void setLeader(MiSelectable s,MiSelectable.SelectOptions o){input.leader=s.character;}}
internal static class NativeInput{internal static ProbeStub Probe;}
internal class ProbeStub{internal bool PeerSelectionBlocked(MiSelectable s)=>false;internal bool HostCommandAllowed(MiCharacter c,SkillCommand k)=>true;}
public static class MultiOwnershipTests
{
 static int count;static void Check(bool b,string message){if(!b)throw new Exception(message);count++;}
 static string Peer(int i)=>i.ToString("x32");
 public static int Run(){
  foreach(int peers in new[]{3,12,64}){
   var c=new CrewOwnership();c.ResetMultiplayer("world",new[]{"a","b","c","d","e","f","g","h"},"a");
   Check(c.Owned("guest").Length==0&&c.Owner("b")==null,"multiplayer starts without phantom guest reservation");
   for(int i=1;i<=peers;i++){
    string peer=Peer(i),next=Peer(i+1);long revision=c.Revision;
    Check(c.Select("world",revision,peer,new[]{"b"},_=>false),"arbitrary peer claims free native crew");
    Check(!c.Select("world",c.Revision,next,new[]{"b"},_=>false),"next peer cannot steal current crew");
    Check(!c.Admit("world",c.Revision,next,"b",1),"other peer command denied");
    long admitted=c.Revision;Check(c.Admit("world",admitted,peer,"b",1)&&!c.Admit("world",admitted,peer,"b",1),"exact peer sequence admitted only once");
    Check(c.ReleasePeer("world",peer,_=>false)==1&&c.Owner("b")==null,"disconnect releases idle native crew");
    Check(!c.Admit("world",admitted,peer,"b",2)&&!c.Select("world",c.Revision,peer,new[]{"b"},_=>false),"delayed and revoked peer commands cannot reacquire");
   }
  }
  var proxy=new CrewOwnership();proxy.ResetMultiplayer("p",new[]{"a","b","c"},"a");
  Check(proxy.Select("p",proxy.Revision,Peer(1),new[]{"b","c"},_=>false),"remote squad claim");
  Check(proxy.ReplaceControlled("p",new[]{new CrewOwnership.ControlledActor("npc","b","inc1")}),"native proxy attaches to original remote source");
  long rev=proxy.Revision;
  Check(proxy.Admit("p",rev,Peer(1),"npc",1)&&!proxy.Admit("p",rev,Peer(2),"npc",1),"proxy authority remains source peer");
  Check(proxy.ReleasePeer("p",Peer(1),a=>a=="c")==0&&proxy.Owner("b")==Peer(1)&&proxy.Owner("c")==Peer(1),"disconnect retains proxy source and busy original");
  Check(!proxy.Admit("p",proxy.Revision,Peer(1),"npc",2),"disconnect revokes admission even while proxy remains");
  Check(proxy.ReleasePeer("p",Peer(1),_=>false)==1&&proxy.Owner("b")==Peer(1),"idle members release independently of controlled source");
  proxy.ReplaceControlled("p",Array.Empty<CrewOwnership.ControlledActor>());
  Check(proxy.ReleasePeer("p",Peer(1),_=>false)==1&&proxy.Owner("b")==null,"source releases after native proxy ends");
  Check(proxy.ResumePeer("p",Peer(1))&&proxy.Select("p",proxy.Revision,Peer(1),new[]{"b"},_=>false),"explicit reconnect permits fresh selection");
  Check(!proxy.Admit("p",proxy.Revision,Peer(1),"b",0)&&!proxy.Admit("old",proxy.Revision,Peer(1),"b",2),"invalid sequence and stale world denied");
  Check(proxy.ReleasePeer("p","host",_=>false)==0&&proxy.Owner("a")=="host","remote disconnect cannot release host");
  bool rejected=false;try{new CrewOwnership().ResetMultiplayer("too-many",Enumerable.Range(0,9).Select(n=>n.ToString()),"0");}catch(ArgumentException){rejected=true;}
  Check(rejected,"unlimited peers do not remove native eight-crew mission limit");
  var ship=new CrewOwnership();ship.ResetMultiplayer("ship",Enumerable.Range(0,11).Select(n=>n.ToString()),"0",CrewOwnership.RosterScope.Ship);Check(ship.Select("ship",ship.Revision,Peer(64),new[]{"10"},_=>false),"ship roster supports extra DLC/hidden crew and later peer IDs");
  var actors=Enumerable.Range(1,8).Select(n=>new MiCharacter(n)).ToArray();var host=new HostCrewSelection();
  host.Reset("native",actors,actors[0],actors[1]);Check(host.RawOwner(actors[0])=="host"&&host.RawOwner(actors[1])==null,"host adapter does not reserve crewB");
  for(int i=1;i<=3;i++){MultiPeerContext.Current=Peer(i);Check(host.SelectGuestGroup(host.Generation,host.Revision,new[]{actors[i].id}),"scoped native squad selected");}
  for(int i=1;i<=3;i++){
   MultiPeerContext.Current=Peer(i);Check(host.OwnerCode(actors[0])=='H'&&host.OwnerCode(actors[i])=='G'&&host.OwnerCode(actors[4])=='U',"per-peer projected roster");
   int other=i%3+1;Check(host.OwnerCode(actors[other])=='H'&&host.IsGuest(actors[other])&&!host.IsCurrentPeer(actors[other]),"another remote projects blocked but remains a remote to host logic");
   Check(!host.CanGuestClaim(actors[other])&&!host.SelectGuest(host.Generation,host.Revision,new[]{actors[other].id})&&!host.Admit(host.Generation,host.Revision,actors[other].id,5),"all native admission paths isolate peer");
   Check(host.PeerAnchor(Peer(i))==actors[i]&&host.OwnedActors(Peer(i)).Single()==actors[i],"snapshot anchor follows actual ownership");
  }
  long before=host.Revision;var observer=host.PeerAnchor(Peer(64));Check(observer==actors[4]&&host.RawOwner(observer)==null&&host.Revision==before,"spectator anchor has no ownership side effect");
  MultiPeerContext.Current=Peer(3);Check(host.SelectGuestGroup(host.Generation,host.Revision,Array.Empty<string>())&&host.OwnedBy(Peer(3)).Length==0,"remote can clear native selection and become spectator");
  MultiPeerContext.Current="host";Check(!host.SelectGuest(host.Generation,host.Revision,new[]{actors[4].id})&&host.OwnerCode(actors[1])=='H'&&host.IsGuest(actors[1]),"host context cannot impersonate guest but recognizes all remote actors");
  var nativeProxy=new MiCharacter(99);Check(host.SetControlled(new[]{new CrewOwnership.ControlledActor(nativeProxy.id,actors[1].id,"v1")},new Dictionary<string,MiCharacter>{{nativeProxy.id,nativeProxy}}),"host proxy binding");
  MultiPeerContext.Current=Peer(1);Check(host.OwnerCode(nativeProxy)=='G'&&host.SelectGuestGroup(host.Generation,host.Revision,new[]{nativeProxy.id}),"owner can select its native proxy without releasing source");
  MultiPeerContext.Current=Peer(2);Check(!host.SelectGuestGroup(host.Generation,host.Revision,new[]{nativeProxy.id})&&host.OwnerCode(nativeProxy)=='H',"other guest cannot select proxy");
  Check(host.ReleasePeer(Peer(1),_=>false)==0&&!host.Admit(host.Generation,host.Revision,nativeProxy.id,10),"busy proxy retained and revoked on disconnect");
  host.SetControlled(Array.Empty<CrewOwnership.ControlledActor>(),new Dictionary<string,MiCharacter>());Check(host.ReleasePeer(Peer(1),_=>false)==1,"native source becomes free after proxy teardown");
  Check(host.Blocked(actors[2].controllerPlayer.m_selectable)&&!host.Blocked(actors[1].controllerPlayer.m_selectable),"native host selection blocks any remote, not released sources");
  var assigned=actors.ToDictionary(a=>a.id,a=>a==actors[0]?'H':a==actors[1]?'G':'U');host.Reset("new",actors,actors[0],actors[1],assigned:assigned);
  Check(host.RawOwner(actors[1])==null&&host.HostCount==1,"legacy departure G cannot create phantom peer in new multi mission");
  MultiPeerContext.Current=Peer(1);Check(host.SelectGuestGroup(host.Generation,host.Revision,new[]{actors[1].id}),"busy handoff setup");
  Check(host.SelectGuestGroup(host.Generation,host.Revision,new[]{actors[2].id},actor=>actor==actors[1])&&host.RawOwner(actors[1])==Peer(1)&&host.RawOwner(actors[2])==Peer(1),"new selection retains busy previous actor");
  MultiPeerContext.Current=Peer(2);Check(!host.SelectGuestGroup(host.Generation,host.Revision,new[]{actors[1].id}),"other peer cannot steal retained busy source");
  MultiPeerContext.Current=Peer(1);Check(host.SelectGuestGroup(host.Generation,host.Revision,new[]{actors[2].id},_=>false)&&host.RawOwner(actors[1])==null,"later idle selection releases old actor");
  Check(host.SelectGuestGroup(host.Generation,host.Revision,Array.Empty<string>(),actor=>actor==actors[2])&&host.RawOwner(actors[2])==Peer(1),"empty native selection also retains busy actor");
  Check(host.ReleaseGuest(host.Generation,host.Revision,new[]{actors[2].id},null,_=>false)&&host.OwnedBy(Peer(1)).Length==0,"idle explicit release includes former network primary");
  MultiPeerContext.Enabled=false;MultiPeerContext.Current="guest";host.Reset("legacy",actors,actors[0],actors[1]);Check(host.OwnerCode(actors[1])=='G',"explicit compatibility reset preserves legacy two-player fixture");
  return count;
 }
}
