using System;

// Local intent is independent of the network sequence. A gesture can still
// be queued behind another actor's request when an older world sample arrives.
internal sealed class GuestActorIntent
{
    private bool queued;
    private float queueDeadline,manualDeadline,lastAttack=float.NegativeInfinity;
    private string manualStyle;
    private long manualSequence;
    internal bool PreferredCrouch{get;private set;}
    internal bool Crouched{get;private set;}
    internal bool HasMelee{get;private set;}
    internal string IntendedGuard{get;private set;}
    internal bool Busy{get;private set;}
    internal bool CanRedirect(float now)=>Busy || now-lastAttack<2f;
    internal bool Holding(float now)=>queued&&now<queueDeadline;
    internal bool HoldingStance(float now)=>manualStyle!=null&&now<manualDeadline;
    internal void Queued(float now){queued=true;queueDeadline=now+2f;}
    internal void Sent(string command,long sequence,bool remaining)
    {
        queued=remaining;
        if(command=="CROUCH" || command=="WALK")
        {
            string style=command=="CROUCH"?"C":"W";
            if(style==manualStyle)manualSequence=sequence;
        }
    }
    internal void Attack(string command,float now)
    {
        lastAttack=now;
        IntendedGuard=ContactTarget(command);
        // The native melee selection can outlive cancellation. Remember that
        // the player used it so subsequent floor clicks can still withdraw.
        if(IntendedGuard!=null)HasMelee=true;
    }
    internal static string ContactTarget(string command)
    {
        var f=command.Split(' ');
        return f.Length>=3&&f[0]=="HIT"&&(f[1]=="L"||f[1]=="K"||f[1]=="LR"||f[1]=="KR")?f[2]:null;
    }
    internal void Withdraw(){IntendedGuard=null;}
    internal void Stance(bool crouch,float now)
    {
        PreferredCrouch=crouch;manualStyle=crouch?"C":"W";
        manualDeadline=now+6f;manualSequence=0;Withdraw();
    }
    internal void Reply(long sequence,bool accepted)
    {
        if(!accepted&&sequence==manualSequence&&!queued)manualStyle=null;
    }
    internal void Observe(string travel,string rest,bool busy,bool fresh,float now)
    {
        Crouched=travel=="C";Busy=busy;
        if(manualStyle!=null && (now>=manualDeadline || fresh&&!Holding(now)&&rest==manualStyle))manualStyle=null;
        if(manualStyle==null)PreferredCrouch=rest=="C";
    }
}
