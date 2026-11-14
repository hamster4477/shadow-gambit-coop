using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Text.RegularExpressions;
using System.Security.Authentication;
using UnityEngine;

internal static class GuestMove
{
    private static readonly HttpClient Client = new HttpClient(new HttpClientHandler {
        UseProxy = false, AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromSeconds(4) };

    internal static Uri Room(string value)
    {
        if (value == null) return null;
        value = value.Trim();
        // Preserve the generated room token exactly; reject URL normalization tricks,
        // embedded credentials, fragments and provider login URLs.
        if (value.Length > 2048 || !Regex.IsMatch(value, @"\Ahttps?://[^/?#\\\s%]+/[A-Za-z0-9_-]{1,128}\z", RegexOptions.IgnoreCase) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || uri.Port < 1 || uri.Port > 65535 ||
            uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "") return null;
        if (uri.HostNameType == UriHostNameType.Dns) return uri;
        if (uri.HostNameType != UriHostNameType.IPv4 || !IPAddress.TryParse(uri.Host, out var ip)) return null;
        var b = ip.GetAddressBytes();
        return b[0] > 0 && b[0] < 224 ? uri : null;
    }

    internal static string Failure(Exception error)
    {
        for (Exception current = error; current != null; current = current.InnerException)
        {
            if (current is AuthenticationException) return "TLS certificate or handshake failed";
            if (current is OperationCanceledException) return "Request cancelled or timed out";
        }
        if (error is HttpRequestException http && http.StatusCode.HasValue) return "HTTP " + (int)http.StatusCode.Value;
        return error.GetBaseException().GetType().Name;
    }

    internal static string Identity(MiCharacter actor)=>ControlledCrew.Active.Find(actor)?.Id??RawIdentity(actor);
    internal static string RawIdentity(MiCharacter actor)
    {
        if (missionHandler == null) missionHandler = SceneLookup.Find<MiMissionHandler>();
        var mission = missionHandler?.currentMissionData;
        if (mission == null || actor == null || string.IsNullOrEmpty(mission.name)) return null;
        if (identityMission != mission.name)
        {
            identityMission = mission.name;
            identities.Clear();
        }
        string name = actor.name ?? "";
        if (!identities.TryGetValue(name, out string id))
            identities[name] = id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityMission + "\n" + name)));
        return id;
    }
    private static MiMissionHandler missionHandler;
    private static string identityMission;
    private static readonly Dictionary<string, string> identities = new Dictionary<string, string>();

    private static async Task<string> Read(HttpResponseMessage response, CancellationToken token,int limit=2048)
    {
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new FormatException("Reply too large");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[limit+1];
        int total = 0, count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length-total), token).ConfigureAwait(false)) > 0)
        {
            total += count;
            if (total > limit) throw new FormatException("Reply too large");
        }
        for (int i=0; i<total; i++) if (buffer[i] > 127) throw new FormatException("Non-ASCII reply");
        return Encoding.ASCII.GetString(buffer, 0, total);
    }

    private static async Task<string> Get(string uri, CancellationToken token,string seen = null,int limit=2048)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        token = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get,uri);
        if (seen != null) request.Headers.Add("X-Coop-Seen",seen);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        return await Read(response, token,limit).ConfigureAwait(false);
    }

    internal static Task<string> State(Uri room, CancellationToken token = default,string seen = null) => Get(room.AbsoluteUri + "/state", token,seen,SnapshotTransport.MaxBytes);
    internal static Task<string> Lobby(Uri room,CancellationToken token)=>Get(room.AbsoluteUri+"/lobby",token,limit:LobbyProtocol.MaxBytes);
    internal static Task<string> Story(Uri room,CancellationToken token)=>Get(room.AbsoluteUri+"/story",token,limit:32768);
    internal static async Task<string> SendStory(Uri room,string payload,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(4));
        using var body=new StringContent(payload,Encoding.ASCII,"application/json");
        using var request=new HttpRequestMessage(HttpMethod.Post,room.AbsoluteUri+"/story"){Content=body};
        request.Headers.Add("X-Coop-Lobby","1");
        using var response=await Client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token).ConfigureAwait(false);
        return await Read(response,deadline.Token).ConfigureAwait(false);
    }
    internal static async Task<string> SelectLobby(Uri room,string payload,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(4));
        using var body=new StringContent(payload,Encoding.ASCII,"application/json");
        using var request=new HttpRequestMessage(HttpMethod.Post,room.AbsoluteUri+"/lobby"){Content=body};
        request.Headers.Add("X-Coop-Lobby","1");
        using var response=await Client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token).ConfigureAwait(false);
        return await Read(response,deadline.Token,32768).ConfigureAwait(false);
    }

    internal static async Task Leave(Uri room,string session,int sequence)
    {
        // A deliberate exit should release the host without waiting for the
        // lost-peer watchdog. The watchdog still covers a dropped notice.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try { await State(room,deadline.Token,session+" "+sequence.ToString(CultureInfo.InvariantCulture)+" I").ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException || ex is IOException ||
            ex is HttpRequestException || ex is FormatException) { }
    }

    private static bool InterruptedTransport(Exception error, CancellationToken token) =>
        !token.IsCancellationRequested && (error is IOException ||
        error is HttpRequestException http && !http.StatusCode.HasValue && http.GetBaseException() is IOException);

    internal static async Task<string> Send(Uri room, string identity, string session, string command, CancellationToken token = default, string hint = null, bool fast = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        // A contact acknowledgment is disposable. Never hold a later player
        // withdrawal behind four seconds of retries for an old strike.
        deadline.CancelAfter(TimeSpan.FromMilliseconds(command != null && command.StartsWith("READY ",StringComparison.Ordinal) ? 500 : 4000));
        token = deadline.Token;
        if (!SyncProtocol.Command($"{identity} {session} {command}", out _)) return "REJECTED";
        string endpoint = room.AbsoluteUri + "/point";
        if (SyncProtocol.Session(hint))
        {
            string hinted = $"{hint} {identity} {session} {command}";
            try
            {
                string reply = await Post(endpoint, hinted, token, fast).ConfigureAwait(false);
                if (reply != "NOT_READY") return reply;
            }
            catch (Exception ex) when (InterruptedTransport(ex, token))
            { return await Post(endpoint, hinted, token, fast).ConfigureAwait(false); }
        }
        string ticket;
        try { ticket = await Get(endpoint, token).ConfigureAwait(false); }
        catch (Exception ex) when (InterruptedTransport(ex, token))
        { ticket = await Get(endpoint, token).ConfigureAwait(false); }
        if (!Guid.TryParseExact(ticket, "N", out _)) return "NOT_READY";
        string payload = $"{ticket} {identity} {session} {command}";
        // A lost reply may follow a successful action. Replay only the identical
        // ticket and body, which the host deduplicates, within the original deadline.
        try { return await Post(endpoint, payload, token, fast).ConfigureAwait(false); }
        catch (Exception ex) when (InterruptedTransport(ex, token))
        { return await Post(endpoint, payload, token, fast).ConfigureAwait(false); }
    }

    private static async Task<string> Post(string endpoint, string payload, CancellationToken token, bool fast)
    {
        using var body = new StringContent(payload, Encoding.ASCII, "text/plain");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = body };
        if (fast) request.Headers.Add("X-Coop-Next","1");
        using var result = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        return await Read(result, token).ConfigureAwait(false);
    }
}
