using System;

// One instance per guest actor. A transport reply is not a newer world frame.
internal sealed class ActorOrderReceipt
{
    private long sent;
    private int readBarrier;
    private float deadline;
    private bool waiting,rejected;
    internal long Sent=>sent;
    internal bool Waiting=>waiting;
    internal void Sending(long sequence,int read,float now)
    {
        if(sequence<=sent||!float.IsFinite(now))return;
        sent=sequence;readBarrier=read;deadline=now+6f;waiting=true;rejected=false;
    }
    internal void Complete(long sequence,bool accepted,int read)
    {
        if(!waiting||sequence!=sent)return;
        readBarrier=Math.Max(readBarrier,read);rejected=!accepted;
    }
    internal bool Fresh(long appliedSequence,int read,float now)
    {
        if(!waiting)return true;
        if(!float.IsFinite(now))return false;
        if(now>=deadline){waiting=false;return true;}
        if(read<=readBarrier)return false;
        if(!rejected&&appliedSequence<sent)return false;
        waiting=false;return true;
    }
}
