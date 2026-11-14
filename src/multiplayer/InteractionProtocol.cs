using System;
using System.Globalization;

internal static class InteractionProtocol
{
    // Explicit native interaction surface; arbitrary skill names are rejected.
    internal static readonly string[] Codes={"DE","DX","DW","CE","CX","CM","JP","JA","JD","HI","GE","EX","AU","BT","BD","OP","OD","TD","TT","TU","WH","WS","BA","BN","CA","CL","CS","CEY","CR","CEO","GN","SK","SN","BK","BNK","BLK","BLN","TB","TN","TM","TK","TKN","TMV","S01","S02","S03","S04","S05","S06","S07","S08","S09","S10","S11","S12","S13","S14","S15","S16","S17","S18","S19","S20","S21","S22","S23","S24","S25","S26","S27","S28","S29","S30","S31","S32","S33","S34","S35","S36","S37","S38","S39","S40","S41","S42","S43","S44","S45","S46","S47","S48","S49","S50","S51","S52","S53","S54","S55","S56","S57","S58","S59","S60","CH"};
    internal static readonly string[] Skills={"UseDoorEnter","UseDoorExit","UseDoorWalkthrough","UseClimbEnter","UseClimbExit","UseClimbMove","UseJump","UseJumpAcross","UseJumpDown","UseHiding","UseGeneric","ExplicitUse","AutoUse","UseCarryBodyThrow","UseCarryBodyDrop","UseCarryObjectPickup","UseCarryObjectDrop","UseCarryTorchDrop","UseCarryTorchThrow","TieUp","Whistle","WhistleStone","BaitDistract","BaitTanuki","CannonAttack","CannonLoad","CannonShoot","CannonEmpty","CannonRecall","CannonEmptyOwner","Gun","SnipeKill","SnipeKnockout","BlinkKill","BlinkKnockout","BlinkKillLong","BlinkKnockoutLong","ThrowBush","ThrowNoise","TeleportPlaceMark","TeleportKill","TeleportKnockout","TeleportMove","GunGatling","GunShootObject","HealthPack","ThrowTNT","Explosive","UseJumpAttack","KillOvertake","KnockoutOvertake","OvertakeByOvertaken","BaitCry","BaitKill","BaitKnockout","BarrelBait","BarrelRoll","Blind","BlinkMove","Catatonic","CharmingGhost","CharmingGhostCancel","Control","ControlCancel","DigKill","DigKnockout","DigKnock","DigIn","DigOut","DigKillHideCorpse","DigPortalExit","DigPortalHide","Disguise","DistractTalk","DistractFollow","Explode","Fear","Fishing","FootprintTrailWithVisibilityToggle","FreezeDistract","FreezeKill","FreezeKnockout","GhostCallBack","GhostLure","GhostPlace","LineTrapKill","LineTrapKnockout","LovePotion","Multikill","Overtake","OvertakeCancelPaused","OvertakeCry","OvertakeDistractTalk","OvertakeFreeze","OvertakeGun","OvertakeSnipe","OvertakeWhistle","Revive","TimeFreeze","ToggleLightSource","TriggerAlarm","WalkAway","WalkTowards","UseCharacterInHub"};
    internal static string Code(string skill) { int n=Array.IndexOf(Skills,skill);return n<0?null:Codes[n]; }
    internal static string Skill(string code) { int n=Array.IndexOf(Codes,code);return n<0?null:Skills[n]; }
    internal static bool PointTarget(string code)=>code=="WH" || code=="WS" || code=="BA" || code=="BN" || code=="TB" || code=="TN" || code=="S54";
    // Abilities keep their native range/trajectory and approach rules.
    internal static bool Context(string code)=>code is "GE" or "EX" or "AU" or "CH";
    internal static bool Ability(string code)=>!Context(code)&&Array.IndexOf(Codes,code)>=20;
    internal static bool CastGeometry(string code)=>!Context(code)&&Array.IndexOf(Codes,code)>=30;
    internal static bool OptionalEntityPoint(string code)=>code=="TM" || code=="S04" || code=="S15" || code=="S43" || code=="S44" || code=="S45";
    internal static bool NullTarget(string code)=>code=="S40" || code=="CEY" || code=="CEO" || code=="TK" || code=="TKN" || code=="TMV" || code=="S10" || code=="S13" || code=="S19" || code=="S21" || code=="S33" || code=="S34" || code=="S36" || code=="S48" || code=="S57" || code=="S58";
    internal static bool EntityKey(string value)=>value=="-" || value!=null && value.Length>1 &&
        ((value[0]=='e' || value[0]=='u') && SyncProtocol.GuardId(value.Substring(1)) || (value[0]=='p'||value[0]=='o') && SyncProtocol.Identity(value.Substring(1)));
    internal static bool Traversal(string code)=>code=="JP" || code=="JA" || code=="JD" || code=="CE" || code=="CX" || code=="CM";
    internal static bool Geometry(string code)=>CastGeometry(code) || Traversal(code) || code=="BT" || code=="BD" || code=="TT" || code=="TD" || code=="OD";
    internal static bool Key(string value)
    {
        if(EntityKey(value)) return true;
        if(value==null || value.Length>384) return false;
        var f=value.Split('~');
        return f.Length==13 && EntityKey(f[0]) && (f[1]=="0" || f[1]=="1") &&
            f[2].Length==1 && f[2][0]>='0' && f[2][0]<='7' && EntityKey(f[3]) &&
            SyncProtocol.Position(f,4) && SyncProtocol.Position(f,7) && SyncProtocol.Position(f,10);
    }
    internal static bool Descriptor(string[] f,int i) => i+6<=f.Length && Skill(f[i])!=null && (f[i+1]=="n" ? NullTarget(f[i]) : Key(f[i+1])) &&
        (f[i+1].IndexOf('~')<0 || Geometry(f[i])) && SyncProtocol.Position(f,i+2) && int.TryParse(f[i+5],NumberStyles.None,CultureInfo.InvariantCulture,out int flags) && flags<=15;
    internal static bool Record(string[] f,int i)=> i+8<=f.Length &&
        int.TryParse(f[i],NumberStyles.None,CultureInfo.InvariantCulture,out int serial) &&
        (serial==0 ? f[i+1]=="-" && f[i+2]=="-" && f[i+3]=="-" && SyncProtocol.Position(f,i+4) && f[i+7]=="0" :
        (f[i+1]=="R" || f[i+1]=="D" || f[i+1]=="C") && Descriptor(f,i+2));
}
