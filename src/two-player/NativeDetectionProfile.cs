using System;
using System.Globalization;

// Build a private native profile for one replayed blind process. Reusing or
// editing a preset would change sight rules for unrelated guards as well.
internal static class NativeDetectionProfile
{
    private static string Value(BalancedFloat value)=>value==null?"-":value.Value.ToString("G9",CultureInfo.InvariantCulture);
    internal static string Capture(DetectionData data)
    {
        if(data==null)return "-";
        string token=string.Join("~",((int)data.m_dataPreset).ToString(CultureInfo.InvariantCulture),
            Value(data.m_fViewRange),Value(data.m_fCrawlDistance),Value(data.m_fViewRangeNight),Value(data.m_fCrawlDistanceNight),
            Value(data.m_fViewAngle),Value(data.m_fMoveAngle),Value(data.m_fMoveSpeed),
            data.m_iHeightOverrideActive.ToString(CultureInfo.InvariantCulture),
            data.m_fNoVisionHeightOverride.ToString("G9",CultureInfo.InvariantCulture),data.m_fVisionCapBottomOverride.ToString("G9",CultureInfo.InvariantCulture));
        if(!DetectionProfileProtocol.Valid(token))throw new InvalidOperationException("Invalid native detection profile");
        return token;
    }
    private static BalancedFloat BuildValue(string value)=>value=="-"?null:new BalancedFloat(float.Parse(value,CultureInfo.InvariantCulture));
    internal static bool TryBuild(string token,out DetectionData data)
    {
        data=null;if(!DetectionProfileProtocol.Valid(token))return false;if(token=="-")return true;
        var f=token.Split('~');
        // Unity native objects require engine allocation; a managed constructor
        // leaves the ScriptableObject without a valid engine instance.
        data=UnityEngine.ScriptableObject.CreateInstance<DetectionData>();
        data.m_dataPreset=(DetectionDataPreset.PresetName)int.Parse(f[0],CultureInfo.InvariantCulture);
        data.m_fViewRange=BuildValue(f[1]);data.m_fCrawlDistance=BuildValue(f[2]);
        data.m_fViewRangeNight=BuildValue(f[3]);data.m_fCrawlDistanceNight=BuildValue(f[4]);
        data.m_fViewAngle=BuildValue(f[5]);data.m_fMoveAngle=BuildValue(f[6]);data.m_fMoveSpeed=BuildValue(f[7]);
        data.m_iHeightOverrideActive=int.Parse(f[8],CultureInfo.InvariantCulture);
        data.m_fNoVisionHeightOverride=float.Parse(f[9],CultureInfo.InvariantCulture);
        data.m_fVisionCapBottomOverride=float.Parse(f[10],CultureInfo.InvariantCulture);
        return true;
    }
}
