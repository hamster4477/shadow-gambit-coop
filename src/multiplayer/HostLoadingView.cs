using UnityEngine;

// Unscaled UI animation stays alive while the checkpoint owns the pause.
// Does not interact with the native loader, camera or Continue button.
internal static class HostLoadingView
{
    internal static void Draw(string stage)
    {
        if(stage==null)return;
        float width=Mathf.Min(620,Screen.width-24),left=(Screen.width-width)*.5f;
        GUI.Box(new Rect(left,18,width,74),"");
        GUI.Label(new Rect(left+50,26,width-58,60),stage);
        var original=GUI.color;
        try
        {
            int step=(int)(Time.realtimeSinceStartup*10)%12;
            for(int i=0;i<12;i++)
            {
                float angle=i*Mathf.PI/6;
                float alpha=.2f+.8f*((i-step+12)%12)/11f;
                GUI.color=new Color(1,1,1,alpha);
                GUI.DrawTexture(new Rect(left+25+Mathf.Cos(angle)*13,43+Mathf.Sin(angle)*13,4,4),Texture2D.whiteTexture);
            }
        }
        finally{GUI.color=original;}
    }
}
