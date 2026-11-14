using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

internal static class GuestExitCollection
{
    private static string gameRoot;
    internal static void Arm(string root)
    {
        if (Interlocked.CompareExchange(ref gameRoot, root, null) == null)
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    private static void OnProcessExit(object sender, EventArgs args)
    {
        try { Collect(); }
        catch (Exception) { /* Unity logging may already be unavailable. */ }
    }

    internal static void Collect()
    {
        string root = Interlocked.Exchange(ref gameRoot, null);
        if (root == null) return;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        // The Steam parent collects after WaitForExit, including the actual
        // exit code. Do not block Unity teardown or race a second collector.
        if (Environment.GetEnvironmentVariable("SGC_COLLECT_AFTER_EXIT") == "1") return;
        var script = Path.Combine(root, "ShadowGambitCoop", "test-support.ps1");
        var shell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo(shell,
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -Mode Collect -Role Guest -OnExit")
            { UseShellExecute = false, CreateNoWindow = true };
        using var collector = Process.Start(start);
        if (collector.WaitForExit(25000) && collector.ExitCode != 0)
            throw new IOException("Exit collector status " + collector.ExitCode);
    }
}
