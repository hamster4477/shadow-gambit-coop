using UnityEngine;

// One instance per actor. Switching the leader must not turn another actor's
// first click into a double click or reuse its native destination/attack.
internal sealed class GuestGestureState
{
    internal int nativeRunFrame=-1,issuedPointFrame=-1,nonMoveFrame=-1;
    internal bool nativeWasCrouched=false;
    internal Vector3 issuedPoint=default;
    internal float previousClickTime=float.NegativeInfinity;
    private Vector3 previousClickPoint,previousClickPixel;
    private bool previousClickCrouched;
    internal MiCharacter lastMeleeGuard=null;
    internal Skill.SkillType lastMeleeSkill=default;
    internal bool lastMeleeFast=false;
    internal int lastMeleePress=-1;
    internal float lastMeleeTime=float.NegativeInfinity;
    internal int contextPress=-1;
    internal string contextDescriptor;
    internal bool RepeatMelee(MiCharacter guard,Skill.SkillType kind,int press,float now,float window)=>
        press>=0 && press!=lastMeleePress && lastMeleeGuard==guard && kind==lastMeleeSkill &&
        now-lastMeleeTime>=0 && now-lastMeleeTime<=window;
    internal string Gait(Vector3 point,Vector3 pixel,int started,float now,float window,bool gameDouble,bool crouch)
    {
        bool nativeFast=started>=0 && nativeRunFrame>=started;
        bool pair=now-previousClickTime>=0 && now-previousClickTime<=window &&
            (pixel-previousClickPixel).sqrMagnitude<=144f && Vector3.Distance(point,previousClickPoint)<=1.25f;
        if(pair)crouch=previousClickCrouched;
        if(nativeFast&&nativeWasCrouched)crouch=true;
        bool run=nativeFast||pair||gameDouble;
        previousClickTime=now;previousClickPoint=point;previousClickPixel=pixel;previousClickCrouched=crouch;
        return run?(crouch?"RC":"RW"):(crouch?"GC":"GW");
    }
}
