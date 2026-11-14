using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

public static class GuestPrepareProgress
{
    static string Display(string message)
    {
        if(message.StartsWith("Downloading"))return "正在下载并校验房主存档……\nDownloading and verifying the host checkpoint…";
        if(message.StartsWith("Installing"))return "正在备份原档案并准备联机存档……\nBacking up your profile and importing the checkpoint…";
        return "等待房主进入任务，准备最新存档……\nWaiting for the host and preparing the latest checkpoint…";
    }
    internal static Form CreateWindow(out Label status,out Button cancel)
    {
        var form=new Form{Text="影子诡局 · Joining room",ClientSize=new Size(600,210),FormBorderStyle=FormBorderStyle.FixedDialog,
            MaximizeBox=false,MinimizeBox=false,StartPosition=FormStartPosition.CenterScreen,Font=new Font("Microsoft YaHei UI",10)};
        status=new Label{Text=Display("Waiting"),Location=new Point(24,24),Size=new Size(552,62)};
        form.Controls.Add(status);
        form.Controls.Add(new ProgressBar{Style=ProgressBarStyle.Marquee,MarqueeAnimationSpeed=25,Location=new Point(24,100),Size=new Size(552,20)});
        cancel=new Button{Text="取消加入 / Cancel",Location=new Point(396,146),Size=new Size(180,42)};
        form.Controls.Add(cancel);
        return form;
    }
    public static string Run(string room,string account,string runtime,string version)
    {
        Application.EnableVisualStyles();Label status;Button cancel;
        using(var form=CreateWindow(out status,out cancel))
        using(var cancellation=new CancellationTokenSource())
        using(var timer=new System.Windows.Forms.Timer{Interval=100})
        {
            string progress="Waiting";
            bool requestedCancel=false,closing=false,cleaning=false;
            Task<string> work=Task.Run(()=>GuestSavePrepare.Prepare(room,account,runtime,version,s=>Volatile.Write(ref progress,s),cancellation.Token));
            Action requestCancel=()=>{
                if(requestedCancel)return;
                requestedCancel=true;cancel.Enabled=false;cancellation.Cancel();
                status.Text="正在取消加入，通知房主解除等待……\nCancelling and notifying the host…";
            };
            cancel.Click+=(s,e)=>requestCancel();
            form.FormClosing+=(s,e)=>{if(!closing){e.Cancel=true;requestCancel();}};
            timer.Tick+=(s,e)=>{
                if(!requestedCancel)status.Text=Display(Volatile.Read(ref progress));
                if(!work.IsCompleted)return;
                if(requestedCancel&&!cleaning&&work.Status==TaskStatus.RanToCompletion)
                {
                    // Installation may finish just before the user clicks
                    // Cancel. Keep its backup, retire its load request, and
                    // tell the host rather than starting the game afterward.
                    cleaning=true;
                    work=Task.Run<string>(()=>{GuestSavePrepare.AbandonPending(runtime);return (string)null;});
                    return;
                }
                closing=true;timer.Stop();form.Close();
            };
            timer.Start();form.ShowDialog();
            if(work.IsFaulted)
            {
                var error=work.Exception.GetBaseException();
                if(requestedCancel&&error is OperationCanceledException)return null;
                throw error;
            }
            if(work.IsCanceled||requestedCancel)return null;
            return work.GetAwaiter().GetResult();
        }
    }
}
