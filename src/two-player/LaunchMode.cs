using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

// Checked before ANY co-op Harmony patch, component, config restore or network
// listener is created. The record persists across Steam's process restart.
internal static class LaunchMode
{
    internal static bool Host(string path)
    {
        try{
            if(!File.Exists(path)||new FileInfo(path).Length>2048)return false;
            using var data=JsonDocument.Parse(File.ReadAllText(path));var r=data.RootElement;
            return r.GetProperty("schema").GetInt32()==1&&r.GetProperty("mode").GetString()=="host"&&
                Regex.IsMatch(r.GetProperty("generation").GetString()??"","\\A[0-9a-f]{32}\\z");
        }catch{return false;}
    }
    internal static bool CanReconnectGuest(bool hostLaunch,bool hostIntent,bool hosting,string pair)=>
        !hostLaunch&&!hostIntent&&!hosting&&pair!="H";
    internal static bool RememberedGuest(bool hostLaunch,bool hostIntent,string pair)=>
        !hostLaunch&&!hostIntent&&(pair??"").StartsWith("G ",StringComparison.Ordinal);
    internal static string PairingForLaunch(bool hostLaunch,string pair)=>
        hostLaunch&&(pair??"").StartsWith("G ",StringComparison.Ordinal)?"":pair;
    internal static Uri GuestLobby(string path,Func<string,Uri> validate,out string generation)
    {
        generation=null;
        try
        {
            if(!File.Exists(path)||new FileInfo(path).Length>2048)return null;
            using var data=JsonDocument.Parse(File.ReadAllText(path));var root=data.RootElement;
            if(root.GetProperty("schema").GetInt32()!=1||root.GetProperty("mode").GetString()!="guest")return null;
            string nonce=root.GetProperty("generation").GetString();
            if(!Regex.IsMatch(nonce??"","\\A[0-9a-f]{32}\\z")||!root.TryGetProperty("lobbyRoom",out var value))return null;
            var room=validate(value.GetString());if(room==null)return null;
            generation=nonce;return room;
        }
        catch{return null;}
    }
    internal static bool SinglePlayer(string path,string[] arguments)
    {
        if(arguments!=null)foreach(string argument in arguments)
            if(argument=="--sgc-single-player")return true;
        if(!File.Exists(path))return false; // Preserve direct/manual startup.
        try
        {
            if(new FileInfo(path).Length>2048)return true;
            using var data=JsonDocument.Parse(File.ReadAllText(path));
            var root=data.RootElement;
            if(root.GetProperty("schema").GetInt32()!=1||!Regex.IsMatch(root.GetProperty("generation").GetString()??"","\\A[0-9a-f]{32}\\z"))return true;
            string mode=root.GetProperty("mode").GetString();
            return mode!="host"&&mode!="guest";
        }
        catch{return true;} // A broken launch record must not silently reconnect.
    }
}
