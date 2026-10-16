// Exo Shaders - unofficial fork of SCSKiller, based on SCSKiller (https://github.com/BlueHeisenberg/SCSKiller).
// Copyright (C) 2026 The SCSKiller authors
// Copyright (C) 2026 Exo Shaders contributors
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
// License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later
// version, with the additional permission in LICENSE-EXCEPTION.txt (see LICENSE and THIRD-PARTY-NOTICES.md).
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
//
// Exo Shaders is an unofficial fork; it is not made or supported by the SCSKiller authors.
//
// Entry point of the headless engine service: argument handling, the single-instance check, then Host.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.App;

namespace ExoShaders.Service;

internal static class Program
{
    public const string ServiceVersion = "0.1.0";

    private const int ExitRefused = 3;
    private const int ExitUsage = 2;
    private const int ExitFatal = 1;

    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception ?? new Exception($"{e.ExceptionObject}"));
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash(e.Exception); e.SetObserved(); };
        RouteFailover.Product = "ExoShaders";
        Console.InputEncoding = new UTF8Encoding(false);

        // The engine's cleanup helper (ScsKiller.CleanupHelper): `<exe> --offline-cleanup <gameId>`, as the app runs it.
        if (args is ["--offline-cleanup", var gameId])
        {
            var store = new AppStore(AppStore.DefaultDir);
            return ScsKiller.RunOfflineCleanup(store, gameId, null, Environment.ProcessPath);
        }

        bool safe = false, logStderr = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data-dir" when i + 1 < args.Length:
                    string dir = Path.GetFullPath(args[++i]);
                    if (!Directory.Exists(dir)) return Usage("data folder not found: " + dir);
                    Environment.SetEnvironmentVariable("SCSKILLER_DATA_DIR", dir);
                    break;
                case "--safe":
                    safe = true;
                    break;
                case "--log-stderr":
                    logStderr = true;
                    break;
                case "-h" or "--help":
                    Console.Error.WriteLine("usage: ExoShaders.Service [--data-dir <dir>] [--safe] [--log-stderr]\nNewline-delimited JSON on stdin/stdout, see CONTRACT.md.");
                    return 0;
                default:
                    return Usage("unknown argument '" + args[i] + "'");
            }
        }

        string dataDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppStore.DefaultDir));
        try
        {
            Directory.CreateDirectory(dataDir);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("can't use the data folder " + dataDir + ": " + e.Message);
            return ExitFatal;
        }

        if (Refusal(dataDir) is { } why)
        {
            Console.Error.WriteLine(why);
            return ExitRefused;
        }
        using var instance = new Mutex(initiallyOwned: false, InstanceName(dataDir), out bool created);
        if (!created)
        {
            Console.Error.WriteLine("another Exo Shaders service is running on " + dataDir);
            return ExitRefused;
        }
        if (Busy.Applying(dataDir, DateTimeOffset.UtcNow))
        {
            Console.Error.WriteLine("an SCSKiller update is being installed, try again in a minute");
            return ExitRefused;
        }

        try
        {
            var host = new Host(new Protocol(), new Options(safe, logStderr));
            int code = host.RunAsync().GetAwaiter().GetResult();
            GC.KeepAlive(instance);
            // The engine may leave foreground threads behind (stdin reads, its schedulers): exit for sure.
            Environment.Exit(code);
            return code;
        }
        catch (Exception e)
        {
            LogCrash(e);
            Console.Error.WriteLine("Exo Shaders service failed: " + e);
            return ExitFatal;
        }
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        return ExitUsage;
    }

    /// <summary>
    /// `Local\ExoShaders.Service.&lt;hash of the data folder&gt;`: one service per data folder.
    /// </summary>
    internal static string InstanceName(string dataDir) =>
        "Local\\ExoShaders.Service." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDir.ToLowerInvariant())))[..16];

    /// <summary>
    /// The official app keeps one instance through the Windows App SDK instance key "SCSKiller" (AppInstance), which is
    /// scoped to its own exe and can't be checked from another program; its CLI has no lock at all. So the service
    /// refuses while a process named SCSKiller (the app SCSKiller.exe or the CLI scskiller.exe) runs and the service
    /// would use the official data folder.
    /// </summary>
    private static string? Refusal(string dataDir) =>
        OfficialDataDir(dataDir) && OfficialRunning() is { } other ? $"the official SCSKiller app is running ({other}) on {dataDir}: quit it first" : null;

    internal static bool OfficialDataDir(string dataDir) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCSKiller"))),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>A running SCSKiller.exe / scskiller.exe that isn't this service's own bundled CLI: "pid N, path".</summary>
    internal static string? OfficialRunning()
    {
        string own = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        Process[] running = Process.GetProcessesByName("SCSKiller");
        try
        {
            foreach (Process p in running.Where(p => p.Id != Environment.ProcessId))
            {
                string? path = null;
                try { path = p.MainModule?.FileName; }
                catch (Exception) { }
                if (path != null && path.StartsWith(own + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                return $"pid {p.Id}, {path ?? "SCSKiller.exe"}";
            }
            return null;
        }
        finally
        {
            foreach (Process p in running) p.Dispose();
        }
    }

    private static void LogCrash(Exception e)
    {
        try
        {
            CrashLog.Write(AppStore.DefaultDir, e);
        }
        catch
        {
        }
    }
}
