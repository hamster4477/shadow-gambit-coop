using System;
using System.Collections.Generic;

// Native selection remains local (including repeat-select camera events).
// Ownership is enabled only by a later authoritative roster, never by HTTP ACK.
internal sealed class GuestCrewSelection
{
    internal sealed class Request
    {
        internal long Serial,Revision;
        internal string[] Actors;
        internal bool Release;
        internal string Command=>(Release?"RELEASE ":"SELECT ")+string.Join(",",Actors);
    }
    // Planning uses a native proxy; ownership still belongs to the real crew.
    internal static MiCharacter Source(MiCharacter actor)
    {
        if(actor==null)return null;
        var planning=SceneLookup.Find<PlanningMode>();
        if(planning==null||!planning.bIsActive||actor!=planning.planningModeCharacter)return actor;
        return planning.getCharacterForPlanningCharacter(actor)??actor;
    }
    private readonly List<string> remembered=new();
    private string rememberedContext;
    private bool initialSelection;
    internal void Context(string value)
    {
        if(rememberedContext==value)return;
        rememberedContext=value;remembered.Clear();restoreSelection=false;initialSelection=true;
    }
    private bool restoreSelection;
    private Request waiting;
    private bool inFlight,releaseRequested;
    private long serial;
    private float deadline,retryAt;
    private readonly List<MiCharacter> selected=new List<MiCharacter>();
    internal static List<MiCharacter> Members(MiPlayerInput input)
    {
        var result=new List<MiCharacter>();
        var selection=input?.m_selectHandler?.m_liSelected;
        if(selection!=null)
        {
            for(int i=0;i<selection.Count;i++){var actor=selection[i]?.character;if(actor!=null&&!result.Exists(c=>c==actor))result.Add(actor);}
            return result;
        }
        var cached=input?.lSelectedCharacter;
        if(cached!=null)for(int i=0;i<cached.Count;i++)if(cached[i]!=null)result.Add(cached[i]);
        return result;
    }
    internal bool Waiting=>waiting!=null;
    internal void Clear(){restoreSelection=remembered.Count>0;waiting=null;inFlight=releaseRequested=false;selected.Clear();retryAt=deadline=0;}
    internal void ReleaseUnselected(){releaseRequested=true;}
    internal void Reply(long request,bool accepted,float now)
    {
        if(waiting==null||waiting.Serial!=request)return;
        inFlight=false;
        if(accepted)deadline=now+3f;
        else {waiting=null;retryAt=now+0.4f;}
    }
    internal static bool Ready(MiPlayerInput input,Func<MiCharacter,char> owner)
    {
        var list=Members(input);
        if(list==null||list.Count==0)return false;
        for(int i=0;i<list.Count;i++)if(list[i]==null||owner(Source(list[i]))!='G')return false;
        return true;
    }
    internal static void RemoveHostSelection(MiPlayerInput input,Func<MiCharacter,char> owner)
    {
        if(input?.m_selectHandler==null)return;
        var native=Members(input);var leader=input.leader;
        bool invalid=leader!=null&&owner(Source(leader))=='H';
        foreach(var actor in native)if(owner(Source(actor))=='H')invalid=true;
        if(!invalid)return;
        native.RemoveAll(actor=>owner(Source(actor))=='H');
        if(leader!=null&&owner(Source(leader))=='H')leader=native.Count>0?native[0]:null;
        var options=new MiSelectable.SelectOptions(false);
        input.m_selectHandler.deselectAll(options);
        foreach(var actor in native)input.m_selectHandler.select(actor.controllerPlayer.m_selectable,options);
        if(leader!=null)input.m_selectHandler.setLeader(leader.controllerPlayer.m_selectable,options);
    }
    internal void Observe(MiPlayerInput input,MiCharacter anchor,Func<MiCharacter,char> owner,Func<MiCharacter,bool> known,float now)
    {
        if(input==null)return;
        // The imported save usually selects the host's actor. Once the first
        // ownership frame arrives, select the guest's assigned anchor once.
        // Later deliberate deselection and same-scene reload memory stay local.
        if(initialSelection&&input.m_selectHandler!=null&&anchor?.controllerPlayer?.m_selectable!=null&&known(anchor))
        {
            initialSelection=false;
            bool hasChoice=Members(input).Exists(a=>known(Source(a))&&owner(Source(a))!='H');
            if(!hasChoice&&owner(anchor)=='G')
            {
                var options=new MiSelectable.SelectOptions(false);
                input.m_selectHandler.deselectAll(options);
                input.m_selectHandler.select(anchor.controllerPlayer.m_selectable,options);
                input.m_selectHandler.setLeader(anchor.controllerPlayer.m_selectable,options);
            }
        }
        if(restoreSelection&&input.m_selectHandler!=null)
        {
            var restored=new List<MiCharacter>();
            foreach(string id in remembered)
            {
                var actor=MoveProbe.CrewById(id);
                if(actor!=null&&known(actor)&&owner(actor)!='H')restored.Add(actor);
            }
            // A changed scene may have a different roster. Never steal a host actor.
            restoreSelection=false;
            if(restored.Count>0)
            {
                var options=new MiSelectable.SelectOptions(false);
                input.m_selectHandler.deselectAll(options);
                foreach(var actor in restored)input.m_selectHandler.select(actor.controllerPlayer.m_selectable,options);
                input.m_selectHandler.setLeader(restored[0].controllerPlayer.m_selectable,options);
            }
        }
        selected.Clear();
        var native=Members(input);
        if(native!=null)for(int i=0;i<native.Count;i++)
        {
            var actor=native[i];
            if(actor!=null&&known(Source(actor))&&owner(Source(actor))!='H'&&!selected.Exists(c=>c==actor))selected.Add(actor);
        }
        bool repair=native!=null&&native.Count!=selected.Count;
        // Filtering peer-owned selections may leave an empty squad; do not force the anchor back.
        var nativeLeader=input.leader;
        bool hasLeader=selected.Exists(c=>c==nativeLeader);
        if(nativeLeader!=null&&!hasLeader)repair=true;
        if(repair)
        {
            var leader=hasLeader?nativeLeader:(selected.Count>0?selected[0]:null);
            var options=new MiSelectable.SelectOptions(false);
            input.m_selectHandler.deselectAll(options);
            foreach(var actor in selected)input.m_selectHandler.select(actor.controllerPlayer.m_selectable,options);
            if(leader!=null)input.m_selectHandler.setLeader(leader.controllerPlayer.m_selectable,options);
        }
        remembered.Clear();
        foreach(var actor in selected)remembered.Add(GuestMove.Identity(Source(actor)));
        // Never rebuild a valid native group just because ownership or the
        // requested group is unchanged: a second selection can center camera.
        if(waiting==null||inFlight)return;
        bool confirmed=true;
        foreach(string id in waiting.Actors)
        {
            var actor=MoveProbe.CrewById(id);
            if(actor==null || (waiting.Release?owner(actor)=='G':owner(actor)!='G')){confirmed=false;break;}
        }
        if(confirmed){waiting=null;retryAt=0;}
        else if(now>=deadline){waiting=null;retryAt=now+0.4f;}
    }
    internal Request Next(long revision,float now,MiCharacter anchor,IEnumerable<MiCharacter> roster,
        Func<MiCharacter,char> owner,Func<MiCharacter,bool> busy,Func<MiCharacter,bool> releasable=null)
    {
        if(waiting!=null||revision<=0||now<retryAt)return null;
        var ids=new List<string>();
        bool release=releaseRequested;
        if(release)
        {
            foreach(var actor in roster)if(actor!=null&&actor!=anchor&&owner(actor)=='G'&&!selected.Exists(c=>Source(c)==actor)&&!busy(actor)&&
                (releasable==null||releasable(actor)))
                ids.Add(GuestMove.Identity(actor));
            releaseRequested=false;
        }
        else
        {
            bool changed=false;
            foreach(var actor in selected){var source=Source(actor);ids.Add(GuestMove.Identity(source));if(owner(source)!='G')changed=true;}
            foreach(var actor in roster)if(actor!=null&&owner(actor)=='G'&&!selected.Exists(c=>Source(c)==actor)&&
                (releasable==null||releasable(actor)))changed=true;
            if(!changed||selected.Count==0)return null;
        }
        if(ids.Count==0)return null;
        waiting=new Request{Serial=++serial,Revision=revision,Actors=ids.ToArray(),Release=release};
        inFlight=true;return waiting;
    }
}
