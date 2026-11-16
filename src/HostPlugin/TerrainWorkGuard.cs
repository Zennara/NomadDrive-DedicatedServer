using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Den.Tools.Tasks;
using HarmonyLib;
using MapMagic.Core;

namespace NomadDrive.DedicatedServer;

// Native tile recycling cancels cooperatively, then clears/reuses the worker's
// TileData immediately. Defer the whole grid update, not individual Move calls:
// the grid's dictionary and tile coordinates must change together.
internal static class TerrainWorkGuard
{
    private static readonly List<ThreadManager.Task> Active =
        (List<ThreadManager.Task>)AccessTools.Field(typeof(ThreadManager), "active").GetValue(null);
    private static readonly List<ThreadManager.Task> Queue =
        (List<ThreadManager.Task>)AccessTools.Field(typeof(ThreadManager), "queue").GetValue(null);
    private static readonly List<ThreadManager.Task> Paused =
        (List<ThreadManager.Task>)AccessTools.Field(typeof(ThreadManager), "paused").GetValue(null);
    private static readonly List<CoroutineManager.Task> ApplyQueue =
        (List<CoroutineManager.Task>)AccessTools.Field(typeof(CoroutineManager), "queue").GetValue(null);
    private static readonly Dictionary<MapMagicObject, bool> PendingRefresh = new Dictionary<MapMagicObject, bool>();
    internal static int WorkerLimit = 4;
    internal static long DeferredUpdates, DeferredRefreshes;

    internal static void ValidateAssemblies()
    {
        Check(typeof(MapMagicObject).Assembly.Location, "7666B54C7F1BF76060925CC693BD758FB2F6E98FE3818D07FF975ED2E6AD238F");
        Check(typeof(ThreadManager).Assembly.Location, "4E3588E6E383E934C28C0606F4E10AC8EC0A02ACF56CBF3E892930F2E18E858F");
    }

    private static void Check(string path, string expected)
    {
        using var sha = SHA256.Create();
        using var file = File.OpenRead(path);
        if (BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "") != expected)
            throw new InvalidOperationException("Unsupported terrain assembly: " + Path.GetFileName(path));
    }

    internal static bool WorkersBusy()
    {
        // Match Pause/Resume's paused -> active -> queue lock order; LaunchThreads
        // uses the active -> queue suffix. Paused tasks can still own TileData.
        // A worker retires only after its generation action has returned.
        lock (Paused) lock (Active) lock (Queue)
            return Paused.Count != 0 || Active.Count != 0 || Queue.Count != 0;
    }

    internal static bool ApplyBusy()
    {
        // IsQueueEmpty misses the last yielding routine after it becomes active.
        // IsWorking includes both, with the native enqueue lock for visibility.
        lock (ApplyQueue) return CoroutineManager.IsWorking;
    }

    private static void ApplyLimit()
    {
        // MapMagic deserialization can restore its serialized static settings.
        // Reassert before admitting work, keeping generation off the game thread.
        ThreadManager.autoMaxThreads = false;
        ThreadManager.maxThreads = Math.Max(1, Math.Min(WorkerLimit, Environment.ProcessorCount));
        ThreadManager.useMultithreading = true;
    }

    internal static bool BeforeUpdate(MapMagicObject map)
    {
        if (Plugin.Instance == null) return true;
        ApplyLimit();
        if (WorkersBusy() || ApplyBusy())
        {
            DeferredUpdates++;
            // Apply work must continue or the admission gate would deadlock.
            // EOS, physics, player updates and every other Unity system continue.
            CoroutineManager.Update();
            return false;
        }
        if (PendingRefresh.TryGetValue(map, out var clearAll))
        {
            PendingRefresh.Remove(map);
            map.Refresh(clearAll);
            if (WorkersBusy() || ApplyBusy())
            {
                CoroutineManager.Update();
                return false;
            }
        }
        return true;
    }

    internal static bool BeforeRefresh(MapMagicObject map, bool clearAll)
    {
        if (Plugin.Instance == null) return true;
        ApplyLimit();
        if (!WorkersBusy() && !ApplyBusy()) return true;
        PendingRefresh.TryGetValue(map, out var previous);
        PendingRefresh[map] = previous || clearAll;
        DeferredRefreshes++;
        return false;
    }

    internal static void Forget(MapMagicObject map) => PendingRefresh.Remove(map);
}

[HarmonyPatch(typeof(MapMagicObject), nameof(MapMagicObject.Update))]
internal static class TerrainUpdateGuardPatch
{
    private static bool Prefix(MapMagicObject __instance) => TerrainWorkGuard.BeforeUpdate(__instance);
}

[HarmonyPatch(typeof(MapMagicObject), nameof(MapMagicObject.Refresh))]
internal static class TerrainRefreshGuardPatch
{
    private static bool Prefix(MapMagicObject __instance, bool clearAll) => TerrainWorkGuard.BeforeRefresh(__instance, clearAll);
}

[HarmonyPatch(typeof(MapMagicObject), nameof(MapMagicObject.OnDisable))]
internal static class TerrainGuardCleanupPatch
{
    private static void Postfix(MapMagicObject __instance) => TerrainWorkGuard.Forget(__instance);
}
