using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

// PowerShell 5.1/.NET Framework entry point. Guests do not need Python.
public static class GuestSavePrepare
{
    const int MaxFile=32*1024*1024,MaxTotal=96*1024*1024,MaxPackage=MaxTotal+65536;
    static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=32768,RecursionLimit=12};}
    static Dictionary<string,object> Map(string text){var m=Json().DeserializeObject(text) as Dictionary<string,object>;if(m==null)throw new InvalidDataException("Expected JSON object");return m;}
    static string Str(Dictionary<string,object> m,string name){object v;if(!m.TryGetValue(name,out v)||!(v is string))throw new InvalidDataException("Missing field: "+name);return (string)v;}
    static long Num(Dictionary<string,object> m,string name){object v;if(!m.TryGetValue(name,out v)||!(v is int||v is long))throw new InvalidDataException("Invalid integer: "+name);return Convert.ToInt64(v);}
    static bool Match(string value,string pattern){return value!=null&&Regex.IsMatch(value,"\\A"+pattern+"\\z");}
    static string Hash(byte[] data){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(data)).Replace("-","").ToLowerInvariant();}
    static bool Allowed(string n){return n=="profileData.save"||n=="profileData.save1"||n=="profileData.save2"||Match(n,@"(manualSave|quickSave)/[A-Za-z0-9_-]{1,180}\.save");}
    static byte[] ReadBounded(Stream input,long length,int limit,CancellationToken token)
    {
        if(length<=0||length>limit)throw new InvalidDataException("Invalid transfer size");
        using(var output=new MemoryStream())
        {
            byte[] buffer=new byte[65536];int n;
            while((n=input.ReadAsync(buffer,0,buffer.Length,token).GetAwaiter().GetResult())>0)
            {if(output.Length+n>length)throw new InvalidDataException("Oversized transfer");output.Write(buffer,0,n);}
            if(output.Length!=length)throw new InvalidDataException("Truncated transfer");return output.ToArray();
        }
    }
    public static Dictionary<string,byte[]> Verify(byte[] data,string expectedHash)
    {
        if(data==null||data.Length>MaxPackage||!Match(expectedHash,"[0-9a-f]{64}")||Hash(data)!=expectedHash)throw new InvalidDataException("Checkpoint checksum mismatch");
        var result=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        using(var zip=new ZipArchive(new MemoryStream(data),ZipArchiveMode.Read))
        {
            if(zip.Entries.Count<2||zip.Entries.Count>33)throw new InvalidDataException("Invalid save entry count");
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
            foreach(var entry in zip.Entries)
            {
                if(!names.Add(entry.FullName)||entry.FullName!="manifest.json"&&!Allowed(entry.FullName)||entry.Length<=0||entry.Length>MaxFile)
                    throw new InvalidDataException("Invalid or duplicate save entry");
                total+=entry.Length;if(total>MaxTotal+16384)throw new InvalidDataException("Checkpoint too large");
            }
            var manifest=zip.GetEntry("manifest.json");if(manifest==null||manifest.Length>16384)throw new InvalidDataException("Missing save manifest");
            Dictionary<string,object> index;
            using(var stream=manifest.Open())index=Map(Encoding.UTF8.GetString(ReadBounded(stream,manifest.Length,16384,CancellationToken.None)));
            object rowsValue;object[] rows;
            if(Num(index,"schema")!=1||!index.TryGetValue("files",out rowsValue)||(rows=rowsValue as object[])==null||rows.Length!=zip.Entries.Count-1)
                throw new InvalidDataException("Invalid save manifest");
            var listed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(object value in rows)
            {
                var row=value as Dictionary<string,object>;if(row==null)throw new InvalidDataException("Invalid save row");
                string name=Str(row,"path");if(!Allowed(name)||!listed.Add(name))throw new InvalidDataException("Invalid manifest path");
                var entry=zip.GetEntry(name);if(entry==null||entry.Length!=Num(row,"size"))throw new InvalidDataException("Manifest size mismatch");
                byte[] bytes;using(var input=entry.Open())bytes=ReadBounded(input,entry.Length,MaxFile,CancellationToken.None);
                if(Hash(bytes)!=Str(row,"sha256"))throw new InvalidDataException("Save entry checksum mismatch");result.Add(name,bytes);
            }
            bool mission=false;foreach(string name in result.Keys)if(name.Contains("/"))mission=true;
            object hubValue;bool hub=index.TryGetValue("hub",out hubValue)&&hubValue is bool&&(bool)hubValue;
            if(hub&&mission)throw new InvalidDataException("Ship bundle contains mission saves");
            if(!result.ContainsKey("profileData.save")||!hub&&!mission)throw new InvalidDataException("Profile and mission are required");
        }
        return result;
    }
    static void NoLinks(string path)
    {
        for(string current=Path.GetFullPath(path);current!=null;current=Path.GetDirectoryName(current))
        {
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked session paths are not supported");
            string parent=Path.GetDirectoryName(current);if(parent==current)break;
        }
    }
    static void Atomic(string path,object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=path+"."+Guid.NewGuid().ToString("N")+".new";
        try{File.WriteAllText(temp,Json().Serialize(value),new UTF8Encoding(false));if(File.Exists(path))throw new IOException("A session request already exists");File.Move(temp,path);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    static List<string> FilesWithoutLinks(string root)
    {
        var files=new List<string>();var pending=new Stack<string>();pending.Push(root);
        while(pending.Count>0)
        {
            string directory=pending.Pop();NoLinks(directory);
            foreach(string entry in Directory.GetFileSystemEntries(directory))
            {
                NoLinks(entry);
                if(Directory.Exists(entry))pending.Push(entry);else files.Add(entry);
            }
        }
        return files;
    }
    public static string Install(byte[] data,string manifestJson,string account,string control,string room,Func<bool> running,CancellationToken cancellation=default(CancellationToken))
    {
        cancellation.ThrowIfCancellationRequested();
        if(running())throw new InvalidOperationException("Exit the game before installing the host checkpoint");
        using(var prepared=Stage(data,manifestJson,account,control,room,cancellation))
            return prepared.Commit(()=>!running(),cancellation);
    }
    // ZIP verification and durable staging may run while a task is active.
    // Only Commit changes a player slot. The game adapter must invoke the
    // suspended commit on Unity's thread, between native reset and reindex.
    public static PreparedCheckpoint Stage(byte[] data,string manifestJson,string account,string control,string room,CancellationToken cancellation=default(CancellationToken))
    {
        cancellation.ThrowIfCancellationRequested();
        Room(room);
        var m=Map(manifestJson);string selected=Str(m,"savePath"),generation=Str(m,"generation");
        string mission=Str(m,"mission"),version=Str(m,"gameVersion");
        var entries=Verify(data,Str(m,"sha256"));
        object hubValue;bool hub=m.TryGetValue("hub",out hubValue)&&hubValue is bool&&(bool)hubValue;
        if(hub&&(mission!="-379511569"||selected!="profileData.save"))throw new InvalidDataException("Invalid ship destination");
        if(Num(m,"schema")!=1||Num(m,"bytes")!=data.LongLength||!Match(generation,"[0-9a-f]{32}")||!entries.ContainsKey(selected)||!hub&&!selected.Contains("/")||mission.Length==0||mission.Length>160||version.Length==0||version.Length>80)
            throw new InvalidDataException("Invalid checkpoint manifest");
        if(hub)foreach(string name in entries.Keys)if(name.Contains("/"))throw new InvalidDataException("Ship transfer cannot contain mission files");
        account=Path.GetFullPath(account);control=Path.GetFullPath(control);
        if(!Directory.Exists(account)||!Match(new DirectoryInfo(account).Name,"user_steam_[0-9]{17}"))throw new InvalidDataException("Current Steam account directory is required");
        NoLinks(account);NoLinks(control);
        return new PreparedCheckpoint(account,control,room,entries,m,cancellation);
    }
    public sealed class PreparedCheckpoint:IDisposable
    {
        readonly string account,control,room,stamp,target,stage,backups,backup,request,selected,mission,version,bundleHash,fileHash,hostWorld;
        readonly object rewind;
        readonly bool entering,hub,preparation;
        readonly Dictionary<string,string> hashes=new Dictionary<string,string>(StringComparer.Ordinal);
        bool committed,disposed;
        public string Generation{get;private set;}
        public string Account{get{return account;}}
        public string SavePath{get{return selected;}}
        public string Mission{get{return mission;}}
        // Same-mission reload keeps the active profile and existing holders.
        // Install a unique immutable checkpoint; never overwrite a live file.
        public string CommitReload(Func<bool> nativeReady,CancellationToken cancellation=default(CancellationToken),bool joinAfterLoad=true,bool? autoContinue=null)
        {
            if(hub)throw new InvalidDataException("Ship profiles cannot be loaded as mission rewinds");
            Ready(nativeReady,cancellation);
            string relative="quickSave/coop_"+Generation+".save";
            string path=Path.Combine(target,relative.Replace('/',Path.DirectorySeparatorChar));
            NoLinks(path);Directory.CreateDirectory(Path.GetDirectoryName(path));
            if(File.Exists(path))throw new IOException("Reload checkpoint already exists");
            bool copied=false;
            try
            {
                File.Copy(Path.Combine(stage,selected.Replace('/',Path.DirectorySeparatorChar)),path,false);copied=true;
                if(Hash(File.ReadAllBytes(path))!=fileHash)throw new InvalidDataException("Reload checkpoint checksum mismatch");
                cancellation.ThrowIfCancellationRequested();
                Atomic(request,new{schema=1,generation=Generation,room,profile=2,savePath=relative,fileSha256=fileHash,
                    mission,gameVersion=version,bundleSha256=bundleHash,hostWorld,rewind,entering,hub,preparation,joinAfterLoad,waitForHost=joinAfterLoad,autoContinue=autoContinue??joinAfterLoad,reloadInPlace=true});
                committed=true;return path;
            }
            catch {if(copied&&File.Exists(path))File.Delete(path);throw;}
        }
        internal PreparedCheckpoint(string account,string control,string room,Dictionary<string,byte[]> entries,Dictionary<string,object> manifest,CancellationToken cancellation)
        {
            this.account=account;this.control=control;this.room=room;
            object prepValue;preparation=manifest.TryGetValue("preparation",out prepValue)&&prepValue is bool&&(bool)prepValue;
            object hubValue;hub=manifest.TryGetValue("hub",out hubValue)&&hubValue is bool&&(bool)hubValue;
            object entryValue;
            if(manifest.TryGetValue("entering",out entryValue)){if(!(entryValue is bool))throw new InvalidDataException("Invalid entry flag");entering=(bool)entryValue;}
            object rewindValue;
            if(manifest.TryGetValue("rewind",out rewindValue)&&rewindValue!=null)
            {
                if(!(rewindValue is Dictionary<string,object>)||Json().Serialize(rewindValue).Length>4096)throw new InvalidDataException("Invalid enemy rewind envelope");
                rewind=rewindValue;
            }
            selected=Str(manifest,"savePath");mission=Str(manifest,"mission");version=Str(manifest,"gameVersion");
            object world;if(manifest.TryGetValue("world",out world)&&world!=null){hostWorld=world as string;if(!Match(hostWorld,"[0-9a-f]{32}"))throw new InvalidDataException("Invalid checkpoint scene");}
            Generation=Str(manifest,"generation");bundleHash=Str(manifest,"sha256");fileHash=Hash(entries[selected]);
            stamp=Guid.NewGuid().ToString("N");target=Path.Combine(account,"gameState_02");
            stage=Path.Combine(account,".coop-stage-"+stamp);backups=Path.Combine(account,".coop-session-backups");backup=Path.Combine(backups,"gameState_02-"+stamp);
            request=Path.Combine(control,"session-load.json");NoLinks(target);NoLinks(backups);
            if(File.Exists(request))throw new IOException("A previous load request is still active");
            try
            {
                Directory.CreateDirectory(stage);
                foreach(var item in entries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string path=Path.Combine(stage,item.Key.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(item.Value,0,item.Value.Length);file.Flush(true);}
                    hashes.Add(item.Key,Hash(item.Value));
                }
                Directory.CreateDirectory(backups);Directory.CreateDirectory(control);
            }
            catch{Dispose();throw;}
        }
        void Ready(Func<bool> canCommit,CancellationToken cancellation)
        {
            if(disposed||committed)throw new InvalidOperationException("Checkpoint staging transaction already finished");
            cancellation.ThrowIfCancellationRequested();
            NoLinks(account);NoLinks(control);NoLinks(target);NoLinks(backups);NoLinks(stage);
            if(File.Exists(request))throw new IOException("A previous load request is still active");
            if(FilesWithoutLinks(stage).Count!=hashes.Count)throw new InvalidDataException("Staged checkpoint file set changed");
            foreach(var item in hashes)
            {
                cancellation.ThrowIfCancellationRequested();
                string path=Path.Combine(stage,item.Key.Replace('/',Path.DirectorySeparatorChar));NoLinks(path);
                var info=new FileInfo(path);if(info.Length<=0||info.Length>MaxFile||Hash(File.ReadAllBytes(path))!=item.Value)throw new InvalidDataException("Staged checkpoint changed");
            }
            if(canCommit==null||!canCommit())throw new InvalidOperationException("Native save access is not ready for checkpoint installation");
        }
        public string Commit(Func<bool> canCommit,CancellationToken cancellation=default(CancellationToken))
        {
            Ready(canCommit,cancellation);return CommitCore(cancellation);
        }
        public string CommitWhileSuspended(Func<bool> nativeReady,Action suspendNative,Action resumeNative,CancellationToken cancellation=default(CancellationToken),bool joinAfterLoad=true,bool? autoContinue=null)
        {
            if(suspendNative==null||resumeNative==null)throw new ArgumentNullException("Native index transition callbacks are required");
            Ready(nativeReady,cancellation);
            // Recovery runs even when reset partially fails. No async wait or
            // heavy extraction is permitted between the two native callbacks.
            try{suspendNative();return CommitCore(cancellation,joinAfterLoad,autoContinue);}
            finally{resumeNative();}
        }
        string CommitCore(CancellationToken cancellation,bool joinAfterLoad=true,bool? autoContinue=null)
        {
            bool moved=false,installed=false;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if(Directory.Exists(target)){Directory.Move(target,backup);moved=true;}
                Directory.Move(stage,target);installed=true;
                Atomic(request,new{schema=1,generation=Generation,room,profile=2,savePath=selected,fileSha256=fileHash,
                    mission,gameVersion=version,bundleSha256=bundleHash,hostWorld,rewind,entering,hub,preparation,joinAfterLoad,waitForHost=joinAfterLoad,autoContinue=autoContinue??joinAfterLoad});
                committed=true;
                return Json().Serialize(new{profile=target,backup=moved?backup:null,request,generation=Generation});
            }
            catch
            {
                if(installed)Directory.Move(target,stage);
                if(moved)Directory.Move(backup,target);
                throw;
            }
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            // Only this transaction's staging directory is removed, never a
            // player slot or its backup. Verify the full deletion boundary.
            if(Directory.Exists(stage)&&Path.GetDirectoryName(Path.GetFullPath(stage))==account&&Path.GetFileName(stage)==".coop-stage-"+stamp)
            {FilesWithoutLinks(stage);Directory.Delete(stage,true);}
        }
    }
    static Uri Room(string room)
    {
        Uri url;
        if(!Match(room,@"https?://[^/?#\\\s%@]+/[A-Za-z0-9_-]{24}")||!Uri.TryCreate(room,UriKind.Absolute,out url)||url.UserInfo!=""||url.Port<1||url.HostNameType==UriHostNameType.IPv6)
            throw new InvalidDataException("Invalid room address");return url;
    }
    sealed class StatusError:IOException{internal int Code;internal StatusError(int code):base("Room returned HTTP "+code){Code=code;}}
    static byte[] Request(HttpClient client,Uri room,string suffix,object payload,int limit,CancellationToken cancellation=default(CancellationToken))
    {
        using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        using(var request=new HttpRequestMessage(payload==null?HttpMethod.Get:HttpMethod.Post,room.ToString()+suffix))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(payload==null?60:8));
            CoopPeerIdentity.Stamp(request);
            if(payload!=null){request.Headers.Add("X-Coop-Session","1");request.Content=new StringContent(Json().Serialize(payload),Encoding.UTF8,"application/json");}
            using(var response=client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).GetAwaiter().GetResult())
            {
                if((int)response.StatusCode!=200)throw new StatusError((int)response.StatusCode);
                if(!response.Content.Headers.ContentLength.HasValue)throw new InvalidDataException("Missing transfer length");
                using(var input=response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())return ReadBounded(input,response.Content.Headers.ContentLength.Value,limit,timeout.Token);
            }
        }
    }
    static Dictionary<string,object> Call(HttpClient client,Uri room,string action,object value,CancellationToken cancellation=default(CancellationToken)){return Map(Encoding.UTF8.GetString(Request(client,room,"/session/"+action,value,8192,cancellation)));}
    static void Cancel(HttpClient client,Uri room,string generation){try{Call(client,room,"cancel",new{generation});}catch(IOException){}catch(HttpRequestException){}catch(OperationCanceledException){}catch(ArgumentException){}catch(InvalidOperationException){}}
    static void Retire(HttpClient client,string control,Uri trustedRoom=null)
    {
        string path=Path.Combine(control,"session-load.json");if(!File.Exists(path))return;NoLinks(path);
        if(GameRunning())throw new InvalidOperationException("Exit the game before retrying join");
        Dictionary<string,object> old=null;
        if(new FileInfo(path).Length<=8192)try{old=Map(File.ReadAllText(path));}catch(ArgumentException){}catch(InvalidDataException){}catch(InvalidOperationException){}
        if(GameRunning())throw new InvalidOperationException("Game started while retrying join");
        File.Move(path,Path.Combine(control,"retired-load-"+Guid.NewGuid().ToString("N")+".json"));
        object wait;
        if(old!=null&&old.TryGetValue("waitForHost",out wait)&&wait is bool&&(bool)wait)
            try{
                Uri previous=Room(Str(old,"room"));
                // Native TCP launchers expose a different ephemeral loopback
                // URL each run. Only a freshly verified room with the SAME
                // private room token may replace that old local transport.
                // Never redirect cancellation from a remote HTTP/HTTPS room,
                // or from a different host's token, to the current room.
                Uri destination=trustedRoom!=null&&previous.Scheme=="http"&&previous.IsLoopback&&
                    previous.AbsolutePath==trustedRoom.AbsolutePath?trustedRoom:previous;
                Cancel(client,destination,Str(old,"generation"));
            }catch(InvalidDataException){}
    }
    public static void AbandonPending(string runtime)
    {AbandonPending(runtime,null);}
    public static void AbandonPending(string runtime,string trustedAddress)
    {
        Uri trustedRoom=trustedAddress==null?null:Room(trustedAddress);
        using(var client=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan})
            Retire(client,Path.Combine(runtime,"control"),trustedRoom);
    }
    static void CancelAttempt(HttpClient client,Uri room,string attempt)
    {
        try{Call(client,room,"cancel-client",new{client=attempt});}
        catch(IOException){}catch(HttpRequestException){}catch(OperationCanceledException){}catch(ArgumentException){}catch(InvalidOperationException){}
    }
    static void RetryDelay(CancellationToken cancellation)
    {cancellation.WaitHandle.WaitOne(500);cancellation.ThrowIfCancellationRequested();}
    public static string Prepare(string address,string account,string runtime,string version,Action<string> report)
    {return Prepare(address,account,runtime,version,report,CancellationToken.None);}
    public static string Prepare(string address,string account,string runtime,string version,Action<string> report,CancellationToken cancellation)
    {
        return PrepareCore(address,runtime,version,report,cancellation,true,
            (data,manifest)=>Install(data,manifest,account,Path.Combine(runtime,"control"),address,GameRunning,cancellation));
    }
    public static PreparedCheckpoint PrepareLive(string address,string account,string runtime,string version,Action<string> report,CancellationToken cancellation)
    {return PrepareLive(address,account,runtime,version,report,cancellation,null);}
    public static PreparedCheckpoint PrepareLive(string address,string account,string runtime,string version,Action<string> report,CancellationToken cancellation,string expectedWorld)
    {
        return PrepareCore(address,runtime,version,report,cancellation,false,
            (data,manifest)=>Stage(data,manifest,account,Path.Combine(runtime,"control"),address,cancellation),expectedWorld);
    }
    public static void CancelJoin(string address,string generation)
    {
        if(!Match(generation,"[0-9a-f]{32}"))return;
        using(var client=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan})
            Cancel(client,Room(address),generation);
    }
    static T PrepareCore<T>(string address,string runtime,string version,Action<string> report,CancellationToken cancellation,bool closedGame,Func<byte[],string,T> accept,string expectedWorld=null)
    {
        cancellation.ThrowIfCancellationRequested();
        if(expectedWorld!=null&&!Match(expectedWorld,"[0-9a-f]{32}"))throw new InvalidDataException("Invalid requested scene");
        if(closedGame&&GameRunning())throw new InvalidOperationException("Exit the guest game before joining");
        Uri room=Room(address);string control=Path.Combine(runtime,"control"),attempt=Guid.NewGuid().ToString("N"),generation=null;
        using(var client=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan})
        {
            if(closedGame)Retire(client,control,room);
            else if(File.Exists(Path.Combine(control,"session-load.json")))throw new InvalidOperationException("A checkpoint load is already pending");
            DateTime until=DateTime.UtcNow.AddMinutes(3);Dictionary<string,object> state=null;
            report("Waiting for the host task and a fresh checkpoint...");
            try
            {
                while(generation==null)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if(DateTime.UtcNow>until)throw new TimeoutException("Host did not become ready");
                    try{state=Call(client,room,"join",new{client=attempt,world=expectedWorld},cancellation);generation=Str(state,"generation");}
                    catch(StatusError ex){if(ex.Code!=409&&ex.Code!=503)throw;}
                    catch(HttpRequestException){}catch(OperationCanceledException){cancellation.ThrowIfCancellationRequested();}
                    if(generation==null)RetryDelay(cancellation);
                }
                if(!Match(generation,"[0-9a-f]{32}"))throw new InvalidDataException("Invalid join generation");
                while(true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if(DateTime.UtcNow>until)throw new TimeoutException("Host checkpoint preparation timed out");
                    try{state=Call(client,room,"status",new{generation},cancellation);}
                    catch(HttpRequestException){RetryDelay(cancellation);continue;}
                    catch(OperationCanceledException){cancellation.ThrowIfCancellationRequested();RetryDelay(cancellation);continue;}
                    if(Str(state,"generation")!=generation||Str(state,"phase")=="failed")throw new InvalidDataException("Host cancelled checkpoint preparation");
                    if(Str(state,"phase")=="waiting_guest")break;RetryDelay(cancellation);
                }
                object manifestValue;var m=state.TryGetValue("manifest",out manifestValue)?manifestValue as Dictionary<string,object>:null;
                if(m==null||Num(m,"schema")!=1||Str(m,"generation")!=generation||!Match(Str(m,"sha256"),"[0-9a-f]{64}")||Str(m,"gameVersion")!=version||Num(m,"bytes")<=0||Num(m,"bytes")>MaxPackage)
                    throw new InvalidDataException("Incompatible host checkpoint");
                if(expectedWorld!=null&&Str(m,"world")!=expectedWorld)throw new InvalidDataException("Host changed scene during joining");
                report("Downloading and verifying the host checkpoint...");
                byte[] data=Request(client,room,"/save/"+Str(m,"sha256")+".zip",null,MaxPackage,cancellation);
                if(data.LongLength!=Num(m,"bytes"))throw new InvalidDataException("Checkpoint length mismatch");
                cancellation.ThrowIfCancellationRequested();report(closedGame?"Installing the verified checkpoint...":"Staging the verified checkpoint...");
                return accept(data,Json().Serialize(m));
            }
            catch
            {
                if(generation!=null)Cancel(client,room,generation);else CancelAttempt(client,room,attempt);
                throw;
            }
        }
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct Entry{public uint Size,Usage,Pid;public UIntPtr Heap;public uint Module,Threads,Parent;public int Priority;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Name;}
    [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool Process32FirstW(IntPtr h,ref Entry e);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool Process32NextW(IntPtr h,ref Entry e);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr h);
    // Loaded only by the Windows launcher; keep the shared in-game installer free
    // of a System.Management compile-time dependency.
    internal sealed class ProcessCensus
    {
        internal int Id;internal string Name,Created;internal long? Threads,Handles;
        internal bool Empty(int id){return Id==id&&string.Equals(Name,"ShadowGambit_TCC.exe",StringComparison.OrdinalIgnoreCase)
            &&!string.IsNullOrEmpty(Created)&&Threads==0&&Handles==0;}
    }
    static ProcessCensus ReadCensus(int id)
    {
        try
        {
            var assembly=System.Reflection.Assembly.Load("System.Management, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");
            var type=assembly.GetType("System.Management.ManagementObjectSearcher",true);
            using(var search=(IDisposable)Activator.CreateInstance(type,new object[]{"SELECT ProcessId,Name,CreationDate,ThreadCount,HandleCount FROM Win32_Process WHERE ProcessId="+id}))
            using(var rows=(IDisposable)type.GetMethod("Get",Type.EmptyTypes).Invoke(search,null))
            {
                foreach(var row in (System.Collections.IEnumerable)rows)
                using((IDisposable)row)
                {
                    var item=row.GetType().GetProperty("Item",new[]{typeof(string)});
                    Func<string,object> get=name=>item.GetValue(row,new object[]{name});
                    object threads=get("ThreadCount"),handles=get("HandleCount");
                    return new ProcessCensus{Id=Convert.ToInt32(get("ProcessId")),Name=get("Name") as string,Created=get("CreationDate") as string,
                        Threads=threads==null?(long?)null:Convert.ToInt64(threads),Handles=handles==null?(long?)null:Convert.ToInt64(handles)};
                }
            }
        }
        catch { /* Unknown is never permission to modify saves. */ }
        return null;
    }
    internal static bool ConfirmResidual(int id,Func<int,ProcessCensus> read,Action wait)
    {
        var first=read(id);if(first==null||!first.Empty(id))return false;
        wait();var second=read(id);
        return second!=null&&second.Empty(id)&&first.Created==second.Created;
    }
    static bool ProcessIsRunning(int id)
    {
        return CheckProcess(id,()=>{using(var process=System.Diagnostics.Process.GetProcessById(id)){return !process.HasExited;}},ReadCensus,()=>Thread.Sleep(150));
    }
    internal static bool CheckProcess(int id,Func<bool> running,Func<int,ProcessCensus> read,Action wait)
    {
        try {return running();}
        catch(ArgumentException){return false;}
        catch(System.ComponentModel.Win32Exception error)
        {
            if(error.NativeErrorCode==87||error.NativeErrorCode==1168)return false;
            if(ConfirmResidual(id,read,wait))return false;
            // An unreadable live process is not proof that save files are free.
            throw new IOException("Cannot verify game process PID "+id+" (Windows "+error.NativeErrorCode+")",error);
        }
    }
    public static bool GameRunning()
    {
        IntPtr h=CreateToolhelp32Snapshot(2,0);if(h==new IntPtr(-1))throw new IOException("Cannot enumerate game processes");
        try
        {
            var e=new Entry{Size=(uint)Marshal.SizeOf(typeof(Entry))};bool found=Process32FirstW(h,ref e);
            if(!found)throw new IOException("Cannot read process list");
            do{if(string.Equals(e.Name,"ShadowGambit_TCC.exe",StringComparison.OrdinalIgnoreCase)&&ProcessIsRunning((int)e.Pid))return true;}while(Process32NextW(h,ref e));
            if(Marshal.GetLastWin32Error()!=18)throw new IOException("Process list was incomplete");return false;
        }
        finally{CloseHandle(h);}
    }
}

// One identity per installation, shared by the launcher and the game process.
// This random capability is local runtime data and is never included in packages.
public static class CoopPeerIdentity
{
    static string cached;
    static readonly object gate=new object();
    static bool Valid(string id){return id!=null&&Regex.IsMatch(id,@"\A[0-9a-f]{32}\z");}
    public static string Id {get {lock(gate){if(cached==null)Initialize(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ShadowGambitCoop","session-next","runtime"));return cached;}}}
    public static string Initialize(string runtime)
    {
        lock(gate)
        {
            string env=Environment.GetEnvironmentVariable("COOP_MULTIPLAYER_PEER");
            if(Valid(env)){cached=env;return cached;}
            string directory=Path.Combine(runtime,"control"),path=Path.Combine(directory,"multiplayer-peer.txt");
            Directory.CreateDirectory(directory);
            string value=File.Exists(path)?File.ReadAllText(path).Trim():null;
            if(!Valid(value)){value=Guid.NewGuid().ToString("N");File.WriteAllText(path,value);}
            cached=value;Environment.SetEnvironmentVariable("COOP_MULTIPLAYER_PEER",value);return value;
        }
    }
    public static void Stamp(HttpRequestMessage request){request.Headers.TryAddWithoutValidation("X-Coop-Peer",Id);}
}
