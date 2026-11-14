using System;

// Reuse the native copy lifecycle without opening a wheel or clearing the
// host's selection. The network ownership gate remains outside this adapter.
internal static class MimicNative
{
    private static int replayTraceCount;
    internal static void ObserveAction(MiCharacter actor,SkillCommand command,string phase)
    {
        if(replayTraceCount>=64||command?.m_skill==null||!InteractionSync.Supported(command.m_skill.eSkillType))return;
        var binding=ControlledCrew.Active.Find(actor);if(binding?.Kind!="M")return;
        replayTraceCount++;
        var skill=command.m_skill.TryCast<PlayerSkill>();
        MoveProbePlugin.ProbeLog.LogInfo("Mimic action "+phase+" guest="+(NativeInput.Probe?.NativeGuest==true)+
            " actor="+binding.Id+" skill="+command.m_skill.eSkillType+" serial="+InteractionSync.CommandSerial(command)+
            " item="+(skill?.m_bHasSkillItem==true)+" input="+actor.controllerPlayer?.skillCommand?.m_skill?.eSkillType+
            " process="+actor.controller?.skillCommandProcess?.m_skill?.eSkillType+" active="+actor.controller?.skillCommandActive?.m_skill?.eSkillType);
    }
    internal static SkillBeingATrickster Ability(MiCharacter actor)
    {
        var skill=actor?.controller?.getSkill(Skill.SkillType.BeingATrickster);
        return skill?.TryCast<SkillBeingATrickster>()??skill?.TryCast<SkillBeingATricksterCopy>()?.skillBeingATrickster;
    }
    internal static bool Available(int form)
    {
        if(form<0)return false;
        var saved=SaveGameManager.m_instance?.saveGameUser?.characters?.findCharacterSaveData(form);
        return saved?.bUnlocked==true&&saved.charDefPlayer!=null&&
            saved.charDefPlayer.m_eCharacterName.m_iValue==form&&saved.charDefPlayer.IsPlayableInCurrentBuild();
    }
    // Native spawn copies the original clown's character identity onto the
    // new actor. The cache, not that identity or the pending requested form,
    // records which skill set this particular copy actually represents.
    internal static int Form(SkillBeingATrickster skill,MiCharacter actor)
    {
        if(skill?.character==null||actor==null)return -1;
        if(actor.Pointer==skill.character.Pointer)return skill.character.m_eCharacterName.m_iValue;
        var cache=skill.m_liTricksterInfoCache;
        if(cache!=null)for(int i=0;i<cache.Count;i++)
        {
            var entry=cache[i];
            if(entry?.character?.Pointer==actor.Pointer)return entry.eCharacterName.m_iValue;
        }
        return -1; // An unfinished spawn must not advertise another form.
    }
    internal static bool Apply(MiCharacter actor,int form)
    {
        if(actor?.health?.eHealthState!=MiCharacterHealth.HealthState.Alive||!Available(form))return false;
        var skill=Ability(actor);if(skill==null)return false;
        // A delayed request addressed to an old cached copy cannot transform
        // whichever copy currently occupies the caster's control slot.
        var current=skill.charCurrentMimicry??skill.character;
        if(current==null||current.Pointer!=actor.Pointer||!actor.gameObject.activeInHierarchy)return false;
        // The requested form is written before asynchronous spawn completes.
        // It is not evidence that the new actor actually exists.
        if(Form(skill,current)==form)return true;
        if(skill.m_bInExecution&&skill.m_eCurrentMimickedCharName.m_iValue==form)return true;
        if(skill.m_bInExecution)return false;
        var previous=skill.m_eCurrentMimickedCharName;var executed=skill.m_charExecutedBy;
        try
        {
            var chosen=new MiEnumValue<MiCharacterNameEnum>(form);
            skill.m_charExecutedBy=actor;skill.m_eCurrentMimickedCharName=chosen;skill.m_bInExecution=true;
            // Reenable performs the same native transition as spawning. Both
            // paths preserve cached skill resources and original callbacks.
            if(skill.tryReenableSpawnedCharacter(actor,chosen,out var copy))skill.playCommentBarkFor(copy);
            else skill.spawnNewCopy(actor,chosen);
            return true;
        }
        catch(Exception ex)
        {
            skill.m_eCurrentMimickedCharName=previous;skill.m_charExecutedBy=executed;skill.onMimicUICanceled();
            MoveProbePlugin.ProbeLog.LogWarning("Mimic native transition: "+ex.GetType().Name);
            return false;
        }
    }
}
