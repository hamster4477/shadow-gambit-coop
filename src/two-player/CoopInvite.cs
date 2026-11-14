using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityEngine;

internal sealed class CoopInvite
{
    private bool open,loaded;
    private string address="",udp="",message="";
    private string Runtime=>Path.Combine(BepInEx.Paths.GameRootPath,"ShadowGambitCoop","session-next","runtime","steam-launch");
    internal void Draw()
    {
        if(GUI.Button(new Rect(12,198,270,28),"HTTPS 邀请 / HTTPS invitation"))open=!open;
        if(!open)return;
        if(!loaded)
        {
            loaded=true;
            try {var path=Path.Combine(Runtime,"host-public-address.txt");if(File.Exists(path))address=File.ReadAllText(path);var udpPath=Path.Combine(Runtime,"host-public-udp.txt");if(File.Exists(udpPath))udp=File.ReadAllText(udpPath);}
            catch(Exception ex){message=ex.Message;}
        }
        GUI.Box(new Rect(12,234,650,264),"HTTPS 邀请（5分钟有效）/ Invitation (valid 5 min)");
        GUI.Label(new Rect(26,270,156,25),"HTTPS 地址 / Address");address=GUI.TextField(new Rect(188,270,456,25),address,2048);
        GUI.Label(new Rect(26,304,156,25),"UDP（可选）/ Optional");udp=GUI.TextField(new Rect(188,304,456,25),udp,260);
        if(GUI.Button(new Rect(26,348,344,32),"生成并复制邀请 / Create and copy invitation"))
        {
            try
            {
                string text=address.Trim().TrimEnd('/');
                if(!text.Contains("://"))text="https://"+text;
                if(!Uri.TryCreate(text,UriKind.Absolute,out var endpoint)||endpoint.Scheme!="https"||endpoint.UserInfo!=""||endpoint.AbsolutePath!="/"||endpoint.Query!=""||endpoint.Fragment!="")
                    throw new InvalidOperationException("请输入 HTTPS 域名:端口。 / Enter HTTPS domain:port only.");
                udp=udp.Trim();
                if(udp.Length>0 && (!System.Text.RegularExpressions.Regex.IsMatch(udp,"^[A-Za-z0-9.-]+:[0-9]{1,5}$") || !Uri.TryCreate("udp://"+udp,UriKind.Absolute,out var udpEndpoint) || udpEndpoint.Port<1 || udpEndpoint.Port>65535))throw new InvalidOperationException("UDP 请填域名:端口。 / UDP must be host:port.");
                string selected=File.ReadAllText(Path.Combine(BepInEx.Paths.GameRootPath,"ShadowGambitCoop","room-control","selected-address")).Trim();
                if(!System.Net.IPAddress.TryParse(selected,out _))throw new InvalidOperationException("房主地址不可用。 / Host address unavailable.");
                string token=File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ShadowGambitCoop","rooms",selected+".room")).Trim();
                if(!System.Text.RegularExpressions.Regex.IsMatch(token,"^[A-Za-z0-9_-]{24}$"))throw new InvalidOperationException("房间无效。 / Invalid host room.");
                string code=RandomNumberGenerator.GetInt32(100000000).ToString("D8");
                string hash=Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token))).ToLowerInvariant();
                Directory.CreateDirectory(Runtime);
                string path=Path.Combine(Runtime,"host-invite.json");
                File.WriteAllText(path+".new",JsonSerializer.Serialize(new{code,roomHash=hash,expires=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+300}));
                File.Move(path+".new",path,true);
                address=endpoint.GetLeftPart(UriPartial.Authority);
                File.WriteAllText(Path.Combine(Runtime,"host-public-address.txt"),address);
                File.WriteAllText(Path.Combine(Runtime,"host-public-udp.txt"),udp);
                GUIUtility.systemCopyBuffer="SGC1 "+address+" "+code+" "+(string.IsNullOrWhiteSpace(udp)?"-":udp.Trim());
                message="已复制，请客机粘贴到房间地址栏。\nCopied. Guest: paste into the room address field.";
            }
            catch(Exception ex){message=ex.Message;}
        }
        if(GUI.Button(new Rect(390,348,180,32),"关闭 / Close"))open=false;
        GUI.Label(new Rect(26,394,616,90),message);
    }
}
