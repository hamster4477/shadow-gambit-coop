using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// Identify the alarm house, not an arbitrary nearby guard. Let the mission
// spawn its own configured group and retain control of doors and squad setup.
internal sealed class ReinforcementSync
{
    private AlarmSystem system;
    private float lookupAt, nextScan, nextTry;
    private readonly List<string> active = new List<string>();
    private readonly HashSet<string> issued = new HashSet<string>();
    private readonly Dictionary<string,(int cursor,float seen)> peerPages=new(StringComparer.Ordinal);
    private float nextPageSweep;

    private AlarmSystem Find()
    {
        if (system == null && Time.realtimeSinceStartup >= lookupAt)
        {
            lookupAt = Time.realtimeSinceStartup + 2f;
            system = UnityEngine.Object.FindObjectOfType<AlarmSystem>();
        }
        return system;
    }

    private static string Key(AlarmHouse house)
    {
        var reference = house.m_alarmHouse;
        if (reference.m_guid != 0) return "g" + reference.m_guid.ToString(CultureInfo.InvariantCulture);
        var building = reference.Resolve(false);
        if (building == null) return null;
        var p = building.transform.position;
        string location = string.Format(CultureInfo.InvariantCulture,"{0}|{1:F2}|{2:F2}|{3:F2}",building.name,p.x,p.y,p.z);
        uint hash = 2166136261;
        foreach (char value in location) hash = unchecked((hash ^ value) * 16777619);
        return "p" + hash.ToString("X8");
    }

    private static bool Spawned(AlarmHouse house) =>
        house.bIsSpawningGuards || house.m_liSpawnedChars != null && house.m_liSpawnedChars.Count > 0;

    internal string Write(string alarm)
    {
        if (!alarm.StartsWith("Alarm",StringComparison.Ordinal) || Find() == null) return "-";
        if (Time.time >= nextScan)
        {
            nextScan = Time.time + 0.5f;
            active.Clear();
            var houses = system.m_liAlarmHouses;
            if (houses != null)
                for (int i=0; i<houses.Count; i++)
                {
                    var house=houses[i];
                    if (house == null || !Spawned(house)) continue;
                    string key=Key(house);
                    if (key != null) active.Add(key);
                }
        }
        if (active.Count==0) return "-";
        float now=Time.realtimeSinceStartup;
        if(now>=nextPageSweep)
        {
            nextPageSweep=now+5f;
            foreach(string key in new List<string>(peerPages.Keys))if(now-peerPages[key].seen>30f)peerPages.Remove(key);
        }
        string peer=MultiPeerContext.Current;
        int cursor=peerPages.TryGetValue(peer,out var previous)?previous.cursor%active.Count:0;
        peerPages[peer]=((cursor+1)%active.Count,now);
        return active[cursor];
    }

    internal void Apply(string key,string alarm)
    {
        if (alarm=="None") { issued.Clear(); return; }
        if (key=="-" || issued.Contains(key) || Time.time<nextTry || Find()==null) return;
        nextTry=Time.time+0.5f;
        var info=system.squadLastActive?.m_targetInfo;
        var houses=system.m_liAlarmHouses;
        if (info==null || houses==null) return;
        AlarmHouse match=null;
        for(int i=0;i<houses.Count;i++)
        {
            var house=houses[i];
            if(house==null || Key(house)!=key) continue;
            if(match!=null) return;
            match=house;
        }
        if(match==null) return;
        if(Spawned(match)) { issued.Add(key); return; }
        if(!match.bHasSpawnableGuards()) return;
        issued.Add(key);
        match.spawn(info);
        MoveProbePlugin.ProbeLog.LogInfo($"Host reinforcement house {key}: guest requested mission guards");
    }
}
