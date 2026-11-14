using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnityEngine;

// Explicit local verification of the native reindex boundary needed by live
// checkpoint installation. This does not install, delete or download saves.
// No request means no native work; never refresh while a mission is running.
internal sealed class SessionIndexRefresh
{
    private string generation,phase="idle",expectedProfile,expectedNewest,error;
    private int profileNumber;
    private IntPtr oldContainer,user;
    private SaveProfileContainer originalContainer;
    private float next,deadline;
    private bool switched;
    internal bool Busy=>phase=="waiting-menu"||phase=="indexing"||phase=="restoring-profile";
    private bool Exclusive=>phase=="indexing"||phase=="restoring-profile";
    private static string PathFor(string name)=>Path.Combine(SessionHost.DirectoryPath,name);
    private static bool MenuReady()
    {
        if(SaveProcessManager.bProcessing||SaveProcessManager.bAsyncWriteOperationRunning||SaveProcessManager.bLoading)return false;
        if(SceneLookup.Find<MiMissionHandler>()?.currentMissionData!=null)return false;
        var menu=SceneLookup.Find<UIMainMenuGUI>();
        return menu!=null&&menu.gameObject.activeInHierarchy&&menu.m_bOnOpenAfterFadeInDone;
    }
    private void Result()
    {
        Directory.CreateDirectory(SessionHost.DirectoryPath);
        string path=PathFor("index-refresh-result.json");
        File.WriteAllText(path+".new",JsonSerializer.Serialize(new{schema=1,generation,phase,error,profile=profileNumber,
            indexReplaced=phase=="verified",checkpointIndexed=phase=="verified",updatedUtc=DateTime.UtcNow.ToString("O")}));
        File.Move(path+".new",path,true);
    }
    internal bool Tick(Action<string> report)
    {
        if(Time.realtimeSinceStartup<next)return Exclusive;
        next=Time.realtimeSinceStartup+.25f;
        try
        {
            if(!Busy)
            {
                string path=PathFor("index-refresh.json");
                if(!File.Exists(path)||new FileInfo(path).Length>1024)return false;
                var age=DateTime.UtcNow-File.GetLastWriteTimeUtc(path);
                if(age<TimeSpan.FromMinutes(-1)||age>TimeSpan.FromMinutes(5))return false;
                using var request=JsonDocument.Parse(File.ReadAllText(path));
                var root=request.RootElement;
                if(root.GetProperty("schema").GetInt32()!=1||root.GetProperty("operation").GetString()!="verify-existing-index")return false;
                string candidate=root.GetProperty("generation").GetString();
                if(!Regex.IsMatch(candidate??"","\\A[0-9a-f]{32}\\z")||candidate==generation)return false;
                // Bootstrap owns profile loading and its join barrier. Never
                // interleave a diagnostic refresh with an actual handoff.
                if(File.Exists(PathFor("session-load.json")))return false;
                File.Move(path,path+".accepted",true);
                generation=candidate;phase="waiting-menu";error=null;switched=false;
                expectedProfile=expectedNewest=null;deadline=Time.realtimeSinceStartup+300f;
                Result();
            }
            if(Time.realtimeSinceStartup>deadline)throw new TimeoutException("Native index refresh timed out in "+phase);
            var manager=SaveGameManager.m_instance;
            if(phase=="waiting-menu")
            {
                if(!MenuReady()||manager==null||!manager.bLoadedAllSaveFiles)return false;
                if(File.Exists(PathFor("session-load.json")))throw new InvalidOperationException("A checkpoint handoff became pending");
                var profile=manager.saveProfile;
                if(profile?.saveGameUser==null||profile.saveGameUserHolder==null||manager.saveProfileContainer==null||manager.m_gameUserOwningSaveFiles==null)
                    throw new InvalidOperationException("Native profile is not ready for reindex");
                profileNumber=profile.iProfileNumber;
                if(profileNumber<0||profileNumber>=SaveProfileContainer.c_iMaxSaveProfileCount)throw new InvalidDataException("Invalid active profile");
                expectedProfile=Path.GetFullPath(profile.saveGameUserHolder.sPath);
                if(profile.getNewestHolder(out var newest)&&newest!=null)expectedNewest=Path.GetFullPath(newest.sPath);
                originalContainer=manager.saveProfileContainer;
                oldContainer=originalContainer.Pointer;user=manager.m_gameUserOwningSaveFiles.Pointer;
                phase="indexing";deadline=Time.realtimeSinceStartup+60f;
                Result();report("Refreshing native save index...");
                manager.resetAllSaveFiles();
                manager.loadSaveFilesForPrimaryGameUser();
                return true;
            }
            if(manager==null||!manager.bLoadedAllSaveFiles||SaveProcessManager.bProcessing||SaveProcessManager.bAsyncWriteOperationRunning)return true;
            if(!MenuReady())throw new InvalidOperationException("Main menu left during native reindex");
            if(manager.m_gameUserOwningSaveFiles==null||manager.m_gameUserOwningSaveFiles.Pointer!=user||
                manager.saveProfileContainer==null||manager.saveProfileContainer.Pointer==oldContainer)
                throw new InvalidOperationException("Native user or profile container did not refresh correctly");
            var current=manager.saveProfile;
            if(current?.iProfileNumber!=profileNumber||current.saveGameUser==null)
            {
                if(!switched)
                {
                    bool exists=false;var profiles=manager.saveProfileContainer.m_liProfiles;
                    if(profiles!=null)for(int i=0;i<profiles.Count;i++)if(profiles[i]?.iProfileNumber==profileNumber){exists=true;break;}
                    if(!exists)throw new InvalidDataException("Original profile disappeared from native index");
                    switched=true;phase="restoring-profile";Result();
                    manager.createAndSetActiveSaveProfile(profileNumber);
                }
                return true;
            }
            if(current.saveGameUserHolder==null||!string.Equals(expectedProfile,Path.GetFullPath(current.saveGameUserHolder.sPath),StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native profile path changed during reindex");
            bool hasNewest=current.getNewestHolder(out var restored);
            string actual=hasNewest&&restored!=null?Path.GetFullPath(restored.sPath):null;
            if(!string.Equals(actual,expectedNewest,StringComparison.OrdinalIgnoreCase)||restored?.bHeaderOrDataCorrupt==true)
                throw new InvalidDataException("Native checkpoint index did not survive refresh");
            phase="verified";originalContainer=null;Result();report("Native save index refreshed; original profile and checkpoint verified.");
            MoveProbePlugin.ProbeLog.LogInfo("Native index refresh verified: "+generation+" profile="+profileNumber);
            return false;
        }
        catch(Exception ex)
        {
            phase="failed";originalContainer=null;error=ex.Message;
            try{string rejected=PathFor("index-refresh.json");if(File.Exists(rejected))File.Move(rejected,rejected+".rejected",true);}catch{}
            MoveProbePlugin.ProbeLog.LogWarning("Native index refresh: "+ex);
            try{Result();}catch{}
            report("Native save index refresh failed. See diagnostic result.");
            return false;
        }
    }
    internal void CancelPending()
    {
        // Once native containers were released, finish rebuilding them even
        // if the user disconnects. Pending, unstarted work can be cancelled.
        if(Exclusive)return;
        try
        {
            string path=PathFor("index-refresh.json");
            if(File.Exists(path))File.Move(path,path+".cancelled",true);
            if(phase=="waiting-menu"){phase="cancelled";Result();}
        }
        catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Index refresh cancellation: "+ex.GetType().Name);}
    }
}
