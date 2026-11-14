using System;
using System.Text.Json;
using UnityEngine;

// Only the successful checkpoint handoff calls this coordinator. It uses the
// original loader's dedicated Continue flow, never a generic confirmation UI.
internal sealed class SessionTaskContinue
{
    private bool issued;
    private bool entered;
    private float issuedAt;
    internal void Reset(){issued=entered=false;issuedAt=0;}
    internal bool Tick(JsonElement request,Action<string> report)
    {
        if(!request.TryGetProperty("autoContinue",out var automatic)||!automatic.GetBoolean())return true;
        // The next coordinator deliberately pauses the entered task while
        // waiting for the host. That pause must not undo this completed step.
        if(entered)return true;
        if(SaveProcessManager.bLoading||SaveProcessManager.bProcessing||SaveProcessManager.bAsyncWriteOperationRunning)return false;
        var loader=SceneLookup.Find<MiSceneLoaderTactics>();
        bool visible=loader!=null&&loader.m_goRootContinue!=null&&loader.m_goRootContinue.activeInHierarchy;
        if(!visible)return entered=issued||Time.timeScale>0f||(request.TryGetProperty("entering",out var entry)&&entry.GetBoolean());
        if(issued)
        {
            if(Time.realtimeSinceStartup-issuedAt>30f)throw new TimeoutException("Native checkpoint Continue did not finish");
            return false;
        }
        var button=loader.m_buttonContinue;
        if(button==null||!button.gameObject.activeInHierarchy||!button.interactable)return false;
        issued=true;issuedAt=Time.realtimeSinceStartup;
        report("Checkpoint ready. Entering the synchronized task...");
        loader.onContinueButtonPressed();return false;
    }
}
