using System;

// Scene-scoped singleton lookup cache. Missing scene-specific UIs back off;
// no actor lists or gameplay state cached.
internal sealed class SceneLookupSlot<T> where T : class
{
    private T value;
    private int generation=int.MinValue;
    private double expires;
    private double missDelay=.1;
    internal T Get(int epoch,double now,Func<T> find,Func<T,bool> alive)
    {
        if(generation!=epoch){value=null;expires=0;missDelay=.1;generation=epoch;}
        if(value!=null&&!alive(value)){value=null;expires=0;missDelay=.1;}
        if(now>=expires)
        {
            value=find();
            expires=value==null?now+missDelay:double.PositiveInfinity;
            missDelay=value==null?Math.Min(.5,missDelay*2):.1;
        }
        return value;
    }
}
