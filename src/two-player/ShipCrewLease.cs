using System;
using Mimimi.HUB;
using UnityEngine;

// Controller lifetime only. Does not select a character, move the camera,
// rewrite transforms or claim network ownership. Initially exercised only by
// the explicitly armed local diagnostic channel.
internal sealed class ShipCrewLease
{
    private readonly HUBLogic scene;
    private readonly HUBCharactersLogic hub;
    private readonly HUBCharactersLogic.HUBCharacterData data;
    private bool released;
    internal MiCharacter Actor=>data.m_character;
    internal ShipCrewLease(HUBLogic scene,HUBCharactersLogic.HUBCharacterData data)
    {
        if(scene==null||data==null||!data.bUnlocked||data.m_character==null||data.m_playerController==null||data.m_aiController==null)
            throw new InvalidOperationException("Ship crew controllers are not ready");
        this.scene=scene;hub=scene.characters;this.data=data;
        if(hub==null||!hub.m_bRoutineSpawningProcessEnded)throw new InvalidOperationException("Ship routines are not ready");
        Maintain();
    }
    internal void Maintain()
    {
        if(released||scene==null||Actor==null)return;
        if(SaveProcessManager.bLoading||SaveProcessManager.bProcessing||MiCutsceneHandler.bCutsceneRunning)return;
        // Native switchOutOfLeader starts this delayed AI takeover. A leased
        // peer must retain its player controller after the local player switches.
        if(data.m_coroDelay!=null){scene.StopCoroutine(data.m_coroDelay);data.m_coroDelay=null;}
        // Possession deliberately hands control away from the caster. Do not
        // reconnect its parked controller over the native possessed actor.
        if(ControlledCrew.Transferred(Actor))return;
        if(!Actor.bControllerPlayer()||Actor.controllerPlayer.Pointer!=data.m_playerController.Pointer)
            data.m_playerController.connect(true,true);
        // Match native switchToCharacter: clear only HUBNotSelected (16),
        // preserving story, skill and other imprisonment reasons.
        if((data.m_playerController.iImprisonedReasonSet&(int)MiCharacterControllerPlayer.ImprisonedReason.HUBNotSelected)!=0)
            data.m_playerController.setImprisonedReason(false,MiCharacterControllerPlayer.ImprisonedReason.HUBNotSelected);
    }
    internal void Release()
    {
        if(released)return;released=true;
        if(scene==null||Actor==null||SaveProcessManager.bLoading)return;
        if(hub.leader==Actor)return; // Native local selection still owns it.
        if(ControlledCrew.Transferred(Actor))return;
        data.m_playerController.setImprisonedReason(true,MiCharacterControllerPlayer.ImprisonedReason.HUBNotSelected);
        if(data.m_coroDelay==null)data.m_coroDelay=scene.StartCoroutine(hub.coroDelayBeforeCharAiTakeover(data));
    }
}
