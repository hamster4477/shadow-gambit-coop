using System;
using System.Collections.Generic;
using System.Linq;

// Scene-independent departure receipt. Definition keys identify a character
// across scene reloads; live keys identify only that scene's actual actors.
internal sealed class LobbyDeployment
{
    internal sealed class Member
    {internal string definition,peer;internal char owner;}
    internal sealed class Live
    {internal string definition,actor;}
    internal sealed class Binding
    {
        internal string host,guest;
        internal Dictionary<string,string> peers=new(StringComparer.Ordinal);
        internal Dictionary<string,char> owners=new Dictionary<string,char>(StringComparer.Ordinal);
    }
    private readonly int mission;
    private readonly Member[] members;
    internal LobbyDeployment(int mission,IEnumerable<Member> members)
    {
        this.mission=mission;
        this.members=members.Select(m=>new Member{definition=m.definition,owner=m.owner,peer=m.peer}).ToArray();
        if(this.members.Length<1||this.members.Length>8||this.members.Any(m=>!SyncProtocol.Identity(m.definition)||(m.owner!='H'&&m.owner!='G'))||
            this.members.Select(m=>m.definition).Distinct(StringComparer.Ordinal).Count()!=this.members.Length)
            throw new ArgumentException("Invalid departure crew");
    }
    internal Binding Bind(int currentMission,IEnumerable<Live> actors)
    {
        if(currentMission!=mission||actors==null)return null;
        var live=actors.ToArray();
        if(live.Length!=members.Length||live.Any(a=>a==null||!SyncProtocol.Identity(a.actor)||!SyncProtocol.Identity(a.definition))||
            live.Select(a=>a.actor).Distinct(StringComparer.Ordinal).Count()!=live.Length||
            live.Select(a=>a.definition).Distinct(StringComparer.Ordinal).Count()!=live.Length)return null;
        var result=new Binding();
        foreach(var member in members)
        {
            var actor=live.SingleOrDefault(a=>a.definition==member.definition);if(actor==null)return null;
            result.owners.Add(actor.actor,member.owner);
            if(member.peer!=null)result.peers[actor.actor]=member.peer;
            if(member.owner=='H'&&result.host==null)result.host=actor.actor;
            if(member.owner=='G'&&result.guest==null)result.guest=actor.actor;
        }
        // Without a guest choice retain the original automatic first/second
        // assignment. Explicit guest picks are never replaced by slot two.
        if(live.Length==1){result.host=live[0].actor;result.guest=result.host;result.owners[result.host]='H';return result;}
        if(result.guest==null){if(MultiPeerContext.Enabled){result.guest=result.host;return result;}result.guest=live.Single(a=>a.definition==members[1].definition).actor;result.owners[result.guest]='G';}
        if(result.host==null||result.host==result.guest)return null;
        return result;
    }
}
