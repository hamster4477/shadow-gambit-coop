using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// GUI-subsystem bridge: create PowerShell without a console, rather than hiding
// its first window (which can be the actual role dialog when Steam launches it).
internal static class SteamLauncherHost
{
    private static string Quote(string value)
    {
        string escaped=Regex.Replace(value,"(\\\\*)\"","$1$1\\\"");
        return "\""+Regex.Replace(escaped,"(\\\\+)$","$1$1")+"\"";
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string root=AppDomain.CurrentDomain.BaseDirectory;
        try
        {
            if(args.Length!=2||(args[0]!="Choose"&&args[0]!="Guest"))throw new ArgumentException("Invalid launcher arguments");
            bool setup=args[1]=="--setup";
            string runtime=Path.Combine(root,"session-next","runtime","steam-launch");
            if(!setup)
            {
                string path=Path.GetFullPath(args[1]);
                if(!string.Equals(Path.GetDirectoryName(path),Path.GetFullPath(runtime),StringComparison.OrdinalIgnoreCase)||
                    !Regex.IsMatch(Path.GetFileName(path),"\\Alaunch-[A-Za-z0-9_.-]+[.]json\\z")||!File.Exists(path))
                    throw new ArgumentException("Invalid Steam argument file");
            }
            var info=new ProcessStartInfo();
            info.FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
            info.WorkingDirectory=root;
            info.UseShellExecute=false;
            info.CreateNoWindow=true;
            // Do not set SW_HIDE: role and address dialogs must be visible.
            info.Arguments="-NoProfile -STA -ExecutionPolicy Bypass -File "+Quote(Path.Combine(root,"steam-start.ps1"))+
                " -Role "+args[0]+(setup?" -Setup":" -ArgumentsFile "+Quote(Path.GetFullPath(args[1])));
            using(var child=Process.Start(info)){child.WaitForExit();return child.ExitCode;}
        }
        catch(Exception ex)
        {
            try{Directory.CreateDirectory(Path.Combine(root,"session-next","runtime","steam-launch"));
                File.WriteAllText(Path.Combine(root,"session-next","runtime","steam-launch","host-error.txt"),ex.ToString(),new UTF8Encoding(false));}catch{}
            MessageBox.Show("联机启动器未能启动。请查看 host-error.txt。\r\nCo-op launcher could not start. See host-error.txt.\r\n"+ex.Message,"影子诡局 · Launch error",MessageBoxButtons.OK,MessageBoxIcon.Error);
            return 1;
        }
    }
}
