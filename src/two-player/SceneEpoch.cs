using System;

// Mission GUID alone cannot distinguish reloads of the same island. Native
// instance, load completion and observed loading/menu boundaries all matter.
internal sealed class SceneEpoch
{
    private IntPtr instance;
    private int mission;
    private long loadRevision;
    private bool crossedBoundary=true;
    private string world;
    private string reserved;
    private long reserveRevision;
    internal void Reserve(string value,long revision){reserved=value;reserveRevision=revision;}
    internal void CancelReservation(){reserved=null;}
    internal string Observe(IntPtr current,int currentMission,long completedLoads,bool loading)
    {
        if(loading||current==IntPtr.Zero){crossedBoundary=true;return null;}
        if(crossedBoundary||current!=instance||currentMission!=mission||completedLoads!=loadRevision)
        {
            world=reserved!=null&&completedLoads!=reserveRevision?reserved:Guid.NewGuid().ToString("N");
            if(completedLoads!=reserveRevision)reserved=null;
            instance=current;mission=currentMission;
            loadRevision=completedLoads;crossedBoundary=false;
        }
        return world;
    }
}
