$ErrorActionPreference='Stop'
$source=[IO.File]::ReadAllText((Join-Path (Split-Path -Parent $PSScriptRoot) 'LaunchMode.cs'))
$fixture=@'
public static class LaunchModeTests {
 public static int Run(string root){
  Directory.CreateDirectory(root);string path=Path.Combine(root,"launch-mode.json");int checks=0;
  Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception(why);checks++;};
  check(!LaunchMode.SinglePlayer(path,new string[0]),"missing marker preserves legacy launcher compatibility");
  check(LaunchMode.SinglePlayer(path,new[]{"game.exe","--sgc-single-player"}),"explicit single-player argument bypasses co-op");
  foreach(string mode in new[]{"solo","host","guest"}){
   File.WriteAllText(path,JsonSerializer.Serialize(new{schema=1,generation=new string('a',32),mode}));
   for(int restart=0;restart<3;restart++)check(LaunchMode.SinglePlayer(path,new string[0])==(mode=="solo"),"mode persists across process restarts");
   check(LaunchMode.SinglePlayer(path,new[]{"--sgc-single-player"}),"explicit argument wins over remembered role");
  }
  foreach(string invalid in new[]{"{","{}",new string('x',2049),"{\"schema\":1,\"generation\":\"bad\",\"mode\":\"host\"}","{\"schema\":2,\"generation\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"mode\":\"guest\"}"}){
   File.WriteAllText(path,invalid);check(LaunchMode.SinglePlayer(path,new string[0]),"invalid launch record cannot silently reconnect");
  }
  string token=new string('b',32),address="http://localhost:38477/abcdefghijklmnopqrstuvwx",generation;
  Func<string,Uri> validate=s=>s==address?new Uri(s):null;
  File.WriteAllText(path,JsonSerializer.Serialize(new{schema=1,generation=token,mode="guest",lobbyRoom=address}));
  check(LaunchMode.GuestLobby(path,validate,out generation)?.AbsoluteUri==address&&generation==token,"first guest launch can observe room without a local actor");
  File.WriteAllText(path,JsonSerializer.Serialize(new{schema=1,generation=token,mode="host",lobbyRoom=address}));
  check(LaunchMode.GuestLobby(path,validate,out generation)==null&&generation==null,"host launch cannot inherit guest lobby intent");
  File.WriteAllText(path,JsonSerializer.Serialize(new{schema=1,generation=token,mode="guest",lobbyRoom="invalid"}));
  check(LaunchMode.GuestLobby(path,validate,out generation)==null,"invalid room is rejected by production room validator");
  File.WriteAllText(path,JsonSerializer.Serialize(new{schema=1,generation="wrong",mode="guest",lobbyRoom=address}));
  check(LaunchMode.GuestLobby(path,validate,out generation)==null,"malformed generation cannot authorize lobby binding");
  File.WriteAllText(path,JsonSerializer.Serialize(new{schema=1,generation=token,mode="host",lobbyRoom=address}));
  check(LaunchMode.Host(path),"host role comes from validated launch marker despite saved guest address");
  check(!LaunchMode.CanReconnectGuest(LaunchMode.Host(path),false,false,""),"create-room launch hides and blocks stale guest reconnect");
  check(!LaunchMode.CanReconnectGuest(false,true,false,"")&&!LaunchMode.CanReconnectGuest(false,false,true,"")&&!LaunchMode.CanReconnectGuest(false,false,false,"H"),"host resume intent, active receiver and pairing each block guest reconnect");
  File.WriteAllText(path,JsonSerializer.Serialize(new{schema=1,generation=token,mode="guest"}));
  check(!LaunchMode.Host(path)&&LaunchMode.CanReconnectGuest(false,false,false,""),"guest after disconnect retains explicit reconnect option");
  File.WriteAllText(path,"{}");check(!LaunchMode.Host(path),"invalid marker never identifies a host");
  string rememberedGuest="G 1 "+address;
  check(LaunchMode.RememberedGuest(false,false,rememberedGuest),"guest in menu stays protected until handoff or F11");
  File.WriteAllText(path,JsonSerializer.Serialize(new{schema=1,generation=token,mode="host"}));
  string hostPair=LaunchMode.PairingForLaunch(LaunchMode.Host(path),rememberedGuest);
  check(hostPair=="","create room clears previous guest pairing before menu guards run");
  check(!LaunchMode.RememberedGuest(true,false,rememberedGuest),"host cannot inherit guest menu lock even before cleanup");
  check(!LaunchMode.RememberedGuest(false,true,rememberedGuest),"legacy host-start intent overrides guest pairing");
  check(!LaunchMode.RememberedGuest(true,true,hostPair),"new host may continue without an active mission or actor");
  check(LaunchMode.PairingForLaunch(true,"")=="","F11 followed by Steam process restart does not resurrect pairing");
  check(LaunchMode.PairingForLaunch(true,"H")=="H","host resume pairing preserved");
  check(LaunchMode.PairingForLaunch(false,rememberedGuest)==rememberedGuest,"guest launch retains guest handoff");
  check(LaunchMode.PairingForLaunch(false,"")=="","guest F11 remains disconnected");
  return checks;
 }
}
'@
Add-Type -TypeDefinition ($source+"`n"+$fixture)
$root=Join-Path $PSScriptRoot ('runtime/launch-mode-'+[Guid]::NewGuid().ToString('N'))
"Launch-mode policy checks passed: $([LaunchModeTests]::Run($root))"
$plugin=[IO.File]::ReadAllText((Join-Path (Split-Path -Parent $PSScriptRoot) 'MoveProbe.cs'))
$load=$plugin.Substring($plugin.IndexOf('public override void Load()'))
if($load.IndexOf('LaunchMode.SinglePlayer') -gt $load.IndexOf('CoopWaitLock.InstallSaveFilters')){throw 'Launch mode must precede native initialization'}
