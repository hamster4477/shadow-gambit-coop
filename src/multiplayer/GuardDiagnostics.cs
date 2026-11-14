using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityEngine;

// Read-only observation. Never pauses AI, executes skills, or writes game state.
// Every public entry catches its own failures so diagnostics cannot disable the move probe.
internal sealed class GuardDiagnostics
{
    internal static bool Enabled;
    private static readonly string Run = Guid.NewGuid().ToString("N");
    private const int MaxRecords = 12000;
    private static int records;
    private static bool budgetReported;
    private readonly string role;
    private string session, capture, mission;
    private bool failed;
    private MiCharacter previousGuard;
    private int previousHealth, previousAlert;
    private string previousExpected, previousHostHealth, previousHostAlert;
    private bool sampled;

    internal GuardDiagnostics(string role) { this.role = role; }

    private static string Mission()
    {
        var handler = SceneLookup.Find<MiMissionHandler>();
        return handler?.currentMissionData?.name;
    }

    private void Write(string kind, Dictionary<string, object> data)
    {
        if (!Enabled || failed) return;
        if (records >= MaxRecords)
        {
            if (!budgetReported)
            {
                budgetReported = true;
                MoveProbePlugin.ProbeLog.LogWarning("Guard diagnostics record limit reached; restart the game for a new capture.");
            }
            return;
        }
        data["schema"] = 1;
        data["kind"] = kind;
        data["run"] = Run;
        data["role"] = role;
        data["session"] = session;
        data["capture"] = capture;
        data["mission"] = mission;
        data["realtime"] = Time.realtimeSinceStartup;
        data["frame"] = Time.frameCount;
        // Serialize first; unsupported values cannot leave half a JSON record.
        string json = JsonSerializer.Serialize(data);
        MoveProbePlugin.ProbeLog.LogInfo("CoopDiag " + json);
        records++;
    }

    private void Fail(Exception ex)
    {
        failed = true;
        MoveProbePlugin.ProbeLog.LogWarning("Guard diagnostics stopped for this capture: " + ex.GetType().Name);
    }

    private static Dictionary<string, object> State(MiCharacter actor)
    {
        var position = actor.transform.position;
        var controller = actor.controllerAI;
        return new Dictionary<string, object> {
            ["id"] = actor.guid.isEmpty() ? null : (object)actor.guid.iValue,
            ["name"] = actor.name,
            ["instance"] = actor.GetInstanceID(), // Local-only; never used to match machines.
            ["position"] = new float[] { position.x, position.y, position.z },
            ["yaw"] = actor.transform.eulerAngles.y,
            ["health"] = actor.health == null ? null : actor.health.eHealthState.ToString(),
            ["alert"] = controller?.m_ai == null ? null : controller.m_ai.eAlertState.ToString(),
            ["skill"] = controller?.skillCommand?.m_skill == null ? null : controller.skillCommand.m_skill.eSkillType.ToString()
        };
    }

    internal void Begin(string newSession)
    {
        if (!Enabled || newSession == session) return;
        session = newSession;
        capture = Guid.NewGuid().ToString("N");
        failed = false;
        sampled = false;
        try
        {
            mission = Mission();
            Write("inventory_begin", new Dictionary<string, object>());
            var actors = UnityEngine.Object.FindObjectsOfType<MiCharacter>();
            int emitted = 0, empty = 0, errors = 0;
            bool truncated = false;
            var ids = new HashSet<int>();
            var duplicates = new HashSet<int>();
            var canonical = new List<string>();
            foreach (var actor in actors)
            {
                if (actor == null || !actor.bControllerAI()) continue;
                if (emitted >= 2048) { truncated = true; break; }
                try
                {
                    var state = State(actor);
                    if (state["id"] == null) empty++;
                    else
                    {
                        int id = (int)state["id"];
                        if (!ids.Add(id)) duplicates.Add(id);
                        canonical.Add(id.ToString(CultureInfo.InvariantCulture) + "\t" + actor.name);
                    }
                    Write("inventory_item", new Dictionary<string, object> { ["guard"] = state });
                    emitted++;
                }
                catch (Exception) { errors++; }
            }
            canonical.Sort(StringComparer.Ordinal);
            Write("inventory_end", new Dictionary<string, object> {
                ["count"] = emitted, ["emptyIds"] = empty, ["duplicateIds"] = new List<int>(duplicates),
                ["errors"] = errors, ["truncated"] = truncated,
                ["complete"] = mission != null && errors == 0 && !truncated,
                ["idNameHash"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", canonical))))
            });
        }
        catch (Exception ex) { Fail(ex); }
    }

    internal void HostSample(int sequence, MiCharacter guard)
    {
        if (!Enabled || failed || capture == null || guard == null) return;
        try
        {
            if (SampleDue(sequence, guard, null, null, null))
                Write("guard_host", new Dictionary<string, object> { ["seq"] = sequence, ["guard"] = State(guard) });
        }
        catch (Exception ex) { Fail(ex); }
    }

    internal void GuestSample(string[] snapshot, MiCharacter guard)
    {
        if (!Enabled || failed || capture == null) return;
        try
        {
            int sequence = int.Parse(snapshot[3], CultureInfo.InvariantCulture);
            if (!SampleDue(sequence, guard, snapshot[15], snapshot[19], snapshot[20])) return;
            Write("guard_guest", new Dictionary<string, object> {
                ["seq"] = sequence,
                ["expectedId"] = int.Parse(snapshot[15], CultureInfo.InvariantCulture),
                ["resolved"] = guard != null,
                ["guard"] = guard == null ? null : State(guard)
            });
        }
        catch (Exception ex) { Fail(ex); }
    }

    private bool SampleDue(int sequence, MiCharacter guard, string expected, string hostAlert, string hostHealth)
    {
        if (budgetReported) return false;
        var health = guard == null ? null : guard.health;
        var ai = guard == null ? null : guard.controllerAI?.m_ai;
        int localHealth = health == null ? -1 : (int)health.eHealthState;
        int alert = ai == null ? -1 : (int)ai.eAlertState;
        // Sample on the same sequence on both PCs; retain intervening status
        // changes immediately without serializing a full pose for every reply.
        bool due = !sampled || sequence % 8 == 0 || previousGuard != guard ||
            previousHealth != localHealth || previousAlert != alert || previousExpected != expected ||
            previousHostAlert != hostAlert || previousHostHealth != hostHealth;
        sampled = true;
        previousGuard = guard; previousHealth = localHealth; previousAlert = alert;
        previousExpected = expected; previousHostAlert = hostAlert; previousHostHealth = hostHealth;
        return due;
    }
}
