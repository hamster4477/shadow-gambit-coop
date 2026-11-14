using System;
using System.IO;
using UnityEngine;

// Preserve the current mission/profile while indexing one received checkpoint.
// The memory slot owns native load options and scene events; do not box them here.
internal sealed class SessionQuickReload
{
    private const int ReloadPause=1<<26;
    private static bool held;
    private SaveGameHolder holder;
    private string path;
    // The HUD quickload icon only opens the memory menu in this game.
    // Use its real checkpoint slot, including when a pause overlay hides it.
    internal static bool HudReady=>UnityEngine.Object.FindObjectOfType<UIPauseMenuGUI>(true)?.m_memoryWindow!=null;
    internal static bool InMission=>SceneLookup.Find<MiMissionHandler>()?.currentMissionData!=null;
    internal static bool Ready(int mission)
    {
        var current=SceneLookup.Find<MiMissionHandler>();
        return mission!=0&&current?.currentMissionData?.guid.iValue==mission&&
            SaveGameManager.m_instance?.saveProfile?.iProfileNumber==2&&
            !SaveProcessManager.bProcessing&&!SaveProcessManager.bLoading&&!SaveProcessManager.bAsyncWriteOperationRunning&&
            !MiCutsceneHandler.bCutsceneRunning;
    }
    internal static void Hold(){if(!held){MiTime.setPause(true,ReloadPause);held=true;}}
    internal static void Release(){if(held){MiTime.setPause(false,ReloadPause);held=false;}}
    internal SaveGameHolder Index(string expected)
    {
        var container=SaveGameManager.m_instance.saveProfile.quickSaveContainer;
        if(path==null)
        {
            path=expected;
            // The container's full indexing coroutine sets persisted storage
            // state and save-type metadata before publishing the holder.
            // CoroLoadHeader on a hand-built holder omits those native steps.
            NativeInput.Probe.StartCoroutine(container.coroLoadHeader(path));
            return null;
        }
        if(path!=expected)throw new InvalidOperationException("Reload checkpoint changed during header read");
        holder=container.getHolder(path);
        if(holder==null)return null;
        if(holder.bHeaderOrDataCorrupt)throw new InvalidDataException("Received reload header is corrupt");
        if(holder.Header==null||!holder.m_bDataIsWrittenToStorage)return null;
        return holder;
    }
    internal void Load()
    {
        var profile=SaveGameManager.m_instance.saveProfile;
        var menu=UnityEngine.Object.FindObjectOfType<UIPauseMenuGUI>(true);
        var memory=menu?.m_memoryWindow;
        if(memory==null||holder?.Header==null)throw new InvalidOperationException("Native memory window or checkpoint unavailable");
        memory.updateQuicksaveSlots();
        var slots=memory.m_liQuickSaveSlots;
        if(slots!=null)for(int i=0;i<slots.Count;i++)
        {
            var slot=slots[i];var assigned=slot?.m_saveGameHolder;
            if(assigned==null||!string.Equals(assigned.sPath,path,StringComparison.OrdinalIgnoreCase))continue;
            if(assigned.bHeaderOrDataCorrupt||assigned.Header==null||!assigned.m_bDataIsWrittenToStorage)
                throw new InvalidDataException("Native memory slot has an invalid checkpoint header");
            // onButtonPressed synchronously passes this exact holder to
            // SaveLoadExecuteAndListener.loadSaveGame using native options.
            // It does not select newest, or go through main-menu Continue.
            Release();
            slot.onButtonPressed();
            return;
        }
        throw new InvalidOperationException("Received checkpoint has no native memory slot");
    }
}
