using System;
using System.Collections.Generic;
using System.Text.Json;

// Independent of scene-specific actor identities and gameplay snapshot epochs.
internal static class LobbyProtocol
{
    internal const int MaxBytes=32768;
    internal sealed class Character
    {
        public string id{get;set;} public string label{get;set;} public bool selected{get;set;}
        public bool required{get;set;} public bool unavailable{get;set;} public int slot{get;set;}=-1;
        public string owner{get;set;}="U";
    }
    internal sealed class Frame
    {
        public int schema{get;set;}=1;public string room{get;set;} public long revision{get;set;}
        public string world{get;set;}
        public string nativeStage{get;set;} public string spawn{get;set;}
        public float? gameSpeed{get;set;}
        public bool? slowEffect{get;set;}
        public float? alarmRemaining{get;set;}
        public string[] departure{get;set;}=Array.Empty<string>();
        public bool reloading{get;set;} public bool entering{get;set;}
        public string phase{get;set;} public int mission{get;set;} public string title{get;set;}="";
        public string description{get;set;}="";
        public string shipHost{get;set;} public string shipGuest{get;set;}
        public Dictionary<string,string> shipOwners{get;set;}=new();
        public Dictionary<string,string> peerOwners{get;set;}=new();
        public int limit{get;set;} public bool ready{get;set;}
        public Character[] characters{get;set;}=Array.Empty<Character>();
    }
    internal sealed class Request
    {
        public string request{get;set;} public string room{get;set;} public long revision{get;set;}
        public string character{get;set;} public bool selected{get;set;}
        public string peer{get;set;}
    }
    internal sealed class Reply{public string request{get;set;} public bool accepted{get;set;}public string reason{get;set;}}
    private static bool Label(string value,int max)
    {if(value==null||value.Length>max)return false;foreach(char c in value)if(char.IsControl(c))return false;return true;}
    internal static bool Valid(Frame frame)
    {
        if(frame==null||frame.schema!=1||!SyncProtocol.Session(frame.room)||frame.world!=null&&!SyncProtocol.Session(frame.world)||frame.revision<=0||
            !Label(frame.title,256)||!Label(frame.description,2048)||frame.limit<0||frame.limit>64||frame.characters==null||frame.characters.Length>64||
            Array.IndexOf(new[]{"menu","loading","map","crew","playing","paused"},frame.phase)<0)return false;
        if(frame.gameSpeed.HasValue&&(!float.IsFinite(frame.gameSpeed.Value)||frame.gameSpeed.Value<.01f||frame.gameSpeed.Value>8f))return false;
        if(frame.alarmRemaining.HasValue&&(!float.IsFinite(frame.alarmRemaining.Value)||frame.alarmRemaining.Value<0||frame.alarmRemaining.Value>86400))return false;
        if(frame.nativeStage!=null&&!Label(frame.nativeStage,64)||frame.spawn!=null&&!SyncProtocol.Identity(frame.spawn)||frame.departure==null||frame.departure.Length>8)return false;
        var deployed=new HashSet<string>();foreach(var id in frame.departure)if(!SyncProtocol.Identity(id)||!deployed.Add(id))return false;
        var ids=new HashSet<string>(StringComparer.Ordinal);var slots=new HashSet<int>();
        if(frame.shipHost!=null&&(!SyncProtocol.Identity(frame.shipHost)||frame.shipGuest!=null&&(!SyncProtocol.Identity(frame.shipGuest)||frame.shipGuest==frame.shipHost)))return false;
        if(frame.shipHost==null&&frame.shipGuest!=null)return false;
        if(frame.entering&&frame.reloading)return false;
        if((frame.reloading||frame.entering)&&(frame.phase!="loading"||!SyncProtocol.Session(frame.world)||frame.mission==0))return false;
        foreach(var c in frame.characters)
            if(c==null||!SyncProtocol.Identity(c.id)||!ids.Add(c.id)||!Label(c.label,128)||
                c.owner!="H"&&c.owner!="G"&&c.owner!="U"||c.selected&&(c.slot<0||c.slot>=frame.limit||!slots.Add(c.slot)||c.owner=="U")||
                !c.selected&&(c.slot!=-1||c.owner!="U"))return false;
        if(ShipSelection(frame))
        {
            if(frame.limit<2||!ids.Contains(frame.shipHost)||frame.shipGuest!=null&&!ids.Contains(frame.shipGuest))return false;
            foreach(var c in frame.characters)
            {
                if(c.id==frame.shipHost&&(!c.selected||c.owner!="H"))return false;
                if(c.id==frame.shipGuest&&(!c.selected||c.owner!="G"))return false;
            }
            return true;
        }
        return frame.phase=="crew" || frame.characters.Length==0;
    }
    internal static bool ShipSelection(Frame frame)=>frame!=null&&frame.mission==-379511569&&frame.shipHost!=null&&(frame.phase=="playing"||frame.phase=="paused");
    internal static bool Valid(Request value)=>value!=null&&SyncProtocol.Session(value.request)&&SyncProtocol.Session(value.room)&&
        value.revision>0&&SyncProtocol.Identity(value.character)&&(value.peer==null||SyncProtocol.Session(value.peer));
    internal static Frame Read(string json)
    {
        if(json==null||json.Length>MaxBytes)return null;
        try{var value=JsonSerializer.Deserialize<Frame>(json);return Valid(value)?value:null;}
        catch(JsonException){return null;}
    }
    internal static Character Allowed(Frame frame,Request request)
    {
        if(!Valid(frame)||!Valid(request)||request.room!=frame.room||request.revision!=frame.revision||frame.phase!="crew"&&!ShipSelection(frame)||!frame.ready)return null;
        foreach(var c in frame.characters)if(c.id==request.character)
        {
            if(request.selected)return c.unavailable?null:c;
            return c.required||c.owner=="H"?null:c;
        }
        return null;
    }
}
