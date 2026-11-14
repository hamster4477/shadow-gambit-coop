using System;

// Real time is independent of the game's own menus, cutscenes and pause lock.
internal sealed class PeerProgress
{
    private bool armed;
    private int good;
    private float firstGood, lastAdvance, suspectedAt = float.PositiveInfinity;

    internal void Advance(float now,bool healthyRun)
    {
        if (!armed)
        {
            if (!healthyRun || now-lastAdvance>PeerPace.CrewDelay) good=0;
            if (healthyRun)
            {
                if (good++==0) firstGood=now;
                armed=good>=3 && now-firstGood>=0.8f;
            }
        }
        lastAdvance=now;
        suspectedAt=float.PositiveInfinity;
    }
    internal bool CrewStalled(float now) => armed && now-lastAdvance>=PeerPace.CrewDelay;
    internal bool WorldStalled(float now)
    {
        if (!armed || now-lastAdvance<PeerPace.WorldDelay) return false;
        // A slow local frame may have returned with a remote reply already
        // queued. Give the transport a chance to drain it before freezing.
        if (float.IsPositiveInfinity(suspectedAt)) suspectedAt=now;
        return now-suspectedAt>=0.2f;
    }
    internal void Reset()
    {
        armed=false; good=0; suspectedAt=float.PositiveInfinity;
    }
}

internal sealed class PeerPace
{
    internal const float CrewDelay = 0.40f, WorldDelay = 1.5f, MaxHold = 5f;
    private readonly (int id,float time,bool hold)[] sent = new (int,float,bool)[64];
    private readonly PeerProgress progress = new PeerProgress();
    private int lastSeen, recovered;
    private float holdStarted;
    internal bool Crew { get; private set; }
    internal bool World { get; private set; }
    internal bool PeerPaused { get; private set; }
    private float pauseSeen;

    internal void Publish(int seq,float now,bool hold) => sent[seq % sent.Length] = (seq,now,hold);
    internal void Observe(int seq,char phase,float now)
    {
        if (seq <= 0 || seq < lastSeen || seq == lastSeen && phase != 'I' && phase != 'P') return;
        var view=sent[seq % sent.Length];
        if (view.id!=seq) return;
        lastSeen=seq;
        PeerPaused=phase=='P';pauseSeen=now;
        if (PeerPaused) { Disarm(); return; }
        if (phase=='I') { Disarm(); return; }
        bool timely=now-view.time<=CrewDelay;
        if (World)
        {
            recovered=timely && view.hold && phase=='H' ? recovered+1 : 0;
            if (recovered>=2) Disarm();
        }
        else if (phase=='R') progress.Advance(now,timely && !view.hold);
    }
    internal void Tick(float now,bool running)
    {
        if(PeerPaused && now-pauseSeen>3f)PeerPaused=false;
        if (!running || World && now-holdStarted>=MaxHold) { Disarm(); return; }
        if (!World && progress.WorldStalled(now)) { World=true; recovered=0; holdStarted=now; }
        Crew=World || progress.CrewStalled(now);
    }
    private void Disarm() { Crew=false; World=false; recovered=0; progress.Reset(); }
    internal void Reset() { Disarm(); PeerPaused=false;pauseSeen=0;lastSeen=0; Array.Clear(sent,0,sent.Length); }
}

internal sealed class GuestPace
{
    private readonly PeerProgress progress = new PeerProgress();
    private float holdStarted;
    private bool timedOut;
    internal bool World { get; private set; }
    internal bool Crew { get; private set; }
    private void Hold(float now)
    {
        if (World || timedOut) return;
        World=true; holdStarted=now;
    }
    internal void Observe(string mode,float started,float now)
    {
        if (mode=="WAIT") { Reset(); return; }
        if (mode=="HOLD") { Hold(now); return; }
        bool timely=now-started<=PeerPace.CrewDelay;
        if (World || timedOut)
        {
            if (!timely) return;
            Reset();
        }
        progress.Advance(now,timely);
        Crew=false;
    }
    internal void Tick(float now)
    {
        if (World && now-holdStarted>=PeerPace.MaxHold)
        { Reset(); timedOut=true; return; }
        if (timedOut) return;
        Crew=progress.CrewStalled(now);
        if (progress.WorldStalled(now)) Hold(now);
    }
    internal void Reset() { progress.Reset(); Crew=false; World=false; timedOut=false; }
}
