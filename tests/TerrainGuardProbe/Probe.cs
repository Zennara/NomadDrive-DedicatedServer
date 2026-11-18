using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BepInEx;
using Den.Tools.Tasks;
using HarmonyLib;
using MapMagic.Core;
using Newtonsoft.Json;
using NomadDrive.Features.ColliderLOD;
using UnityEngine;

namespace NomadDrive.TerrainGuardProbe;

[BepInPlugin("odin.nomaddrive.terrainguardprobe", "Isolated Terrain Guard Probe", "0.1.0")]
public sealed class Probe : BaseUnityPlugin
{
    private string root;
    private Type guard;
    private readonly List<string> checks = new List<string>();
    private ManualResetEventSlim releaseWorker;
    private bool releaseApply;
    private Transform[] stressObservers;
    private MapMagicObject stressMap;
    private double stressLast, stressMax;
    private int stressFrames, terrainErrors;
    private object stressResult;
    private static Probe activeStress;
    private int stressMapCalls, stressAdmittedCalls;

    private void Awake()
    {
        root = Environment.GetEnvironmentVariable("ND_TERRAIN_GUARD_TEST_ROOT");
        if (string.IsNullOrEmpty(root)) { enabled = false; return; }
        root = Path.GetFullPath(root);
        var data = Environment.GetEnvironmentVariable("NOMADDRIVE_SERVER_DATA");
        if (!root.StartsWith("E:\\Odin\\scratch\\nomad-drive\\", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(data) || !string.Equals(Path.GetFullPath(data), Path.Combine(root, "data"), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(Paths.GameRootPath, ".nomaddrive-server-runtime")))
            throw new InvalidOperationException("Probe requires isolated scratch data and a prepared runtime.");
        for (var path = root; path != null; path = Path.GetDirectoryName(path))
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Probe cannot traverse links.");
        StartCoroutine(Observe(Run()));
    }

    private IEnumerator Observe(IEnumerator routine)
    {
        while (true)
        {
            object next;
            try { if (!routine.MoveNext()) break; next = routine.Current; }
            catch (Exception error)
            {
                releaseWorker?.Set(); releaseApply = true;
                File.WriteAllText(Path.Combine(root, "terrain-guard.json"), JsonConvert.SerializeObject(new { passed = false, checks, stressResult, error = error.ToString() }, Formatting.Indented));
                Logger.LogError(error);
                yield break;
            }
            yield return next;
        }
        File.WriteAllText(Path.Combine(root, "terrain-guard.json"), JsonConvert.SerializeObject(new { passed = true, checks, stressResult }, Formatting.Indented));
        Logger.LogInfo("Terrain guard native checks passed: " + checks.Count);
    }

    private bool Busy(string method) => (bool)AccessTools.Method(guard, method).Invoke(null, null);
    private long Counter(string name) => (long)AccessTools.Field(guard, name).GetValue(null);
    private void Check(bool condition, string text)
    { if (!condition) throw new InvalidOperationException(text); checks.Add(text); }
    private IEnumerator HeldApply()
    { while (!releaseApply) yield return null; }

    private sealed class GridTarget : IColliderLodTarget
    {
        public Transform transform => null;
        public bool ParticipatesInColliderLod => true;
        public bool LodPositionMayChange => false;
        public Collider[] GetRigidColliders() => Array.Empty<Collider>();
        public void SetLodColliderActive(bool active) { }
    }

    private void CheckColliderGrid()
    {
        var query = AccessTools.Method(typeof(ColliderLodSpatialGrid), nameof(ColliderLodSpatialGrid.QueryRing));
        Check(Harmony.GetPatchInfo(query)?.Prefixes.Any(patch => patch.owner == "odin.nomaddrive.dedicatedserver") == true,
            "The dedicated server installed the collider grid prefix");

        // Populate precise signed coordinates without creating Unity objects or changing the world's grid.
        var grid = new ColliderLodSpatialGrid(1f);
        var buckets = (Dictionary<long, List<ColliderLodEntry>>)AccessTools.Field(typeof(ColliderLodSpatialGrid), "_cells").GetValue(grid);
        var target = new GridTarget();
        ColliderLodEntry Add(int x, int z)
        {
            var entry = new ColliderLodEntry(target);
            long key = ((long)x << 32) | (uint)z;
            if (!buckets.TryGetValue(key, out var bucket))
                buckets.Add(key, bucket = new List<ColliderLodEntry>());
            bucket.Add(entry);
            return entry;
        }

        var center = Add(-2, -3);
        var nearby = Add(-1, -4);
        var nearbyDuplicateCell = Add(-1, -4);
        var boundary = Add(7, -12);
        var outsideBoundary = Add(-12, -3);
        const int wrapCenter = 2147483520; // Greatest representable float below Int32.MaxValue.
        var wrapOrigin = Add(wrapCenter, 0);
        var wrapNeighbor = Add(int.MinValue + 1, 0);
        var outsideWrap = Add(int.MinValue + 2, 0);
        var results = new HashSet<ColliderLodEntry>();
        var position = new Vector3(-1.5f, 0, -2.5f);

        grid.QueryRing(position, 0.25f, results);
        Check(results.SetEquals(new[] { center, nearby, nearbyDuplicateCell }),
            "The small native ring floors negative coordinates and returns every entry in a populated bucket");
        results.Clear();
        results.Add(outsideBoundary);
        grid.QueryRing(position, 8.25f, results);
        Check(results.SetEquals(new[] { center, nearby, nearbyDuplicateCell, boundary, outsideBoundary }),
            "The sparse ring rounds up, includes the square boundary, and preserves existing results");
        results.Clear();
        grid.QueryRing(position, 8.25f, results);
        Check(results.SetEquals(new[] { center, nearby, nearbyDuplicateCell, boundary }),
            "The sparse ring excludes the first outside bucket");
        results.Clear();
        grid.QueryRing(new Vector3(wrapCenter, 0, 0), 129f, results);
        Check(results.SetEquals(new[] { wrapOrigin, wrapNeighbor }),
            "Sparse filtering matches the native signed-coordinate wrap and boundary");
        results.Clear();
        grid.QueryRing(position, float.MaxValue, results);
        Check(results.SetEquals(buckets.Values.SelectMany(bucket => bucket)) && results.Contains(outsideWrap),
            "An oversized finite radius completes and visits every populated bucket");
        results.Clear();
        grid.QueryRing(position, float.PositiveInfinity, results);
        Check(results.SetEquals(buckets.Values.SelectMany(bucket => bucket)),
            "An infinite radius completes and visits every populated bucket");
    }

    private IEnumerator Run()
    {
        guard = AccessTools.TypeByName("NomadDrive.DedicatedServer.TerrainWorkGuard") ?? throw new Exception("Candidate guard not installed");
        var host = AccessTools.TypeByName("NomadDrive.DedicatedServer.Plugin");
        var deadline = Time.realtimeSinceStartupAsDouble + 180;
        while (AccessTools.Field(host, "Instance").GetValue(null) == null ||
               !(bool)AccessTools.Field(host, "ready").GetValue(AccessTools.Field(host, "Instance").GetValue(null)) || Busy("WorkersBusy") || Busy("ApplyBusy"))
        {
            if (Time.realtimeSinceStartupAsDouble > deadline) throw new Exception("Host never settled");
            yield return null;
        }
        if (Environment.GetEnvironmentVariable("ND_COLLIDER_GRID_TEST") == "1")
        {
            CheckColliderGrid();
            yield break;
        }
        var map = UnityEngine.Object.FindAnyObjectByType<MapMagicObject>();
        Check(map != null, "Native world is ready and terrain settled");
        Check(!ThreadManager.autoMaxThreads && ThreadManager.maxThreads == 4 && ThreadManager.useMultithreading,
            "Four background workers configured");
        var coordinates = string.Join(";", map.tiles.All().Select(t => t.coord.ToString()).OrderBy(s => s));
        releaseWorker = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var worker = ThreadManager.Enqueue(() => { started.Set(); releaseWorker.Wait(10000); }, name: "Guard test worker");
        deadline = Time.realtimeSinceStartupAsDouble + 5;
        while (!started.IsSet)
        { if (Time.realtimeSinceStartupAsDouble > deadline) throw new Exception("Worker never started"); yield return null; }
        var workerPaused = false;
        try
        {
            Check(Busy("WorkersBusy"), "Running native worker is recognized");
            ThreadManager.Pause(worker);
            workerPaused = true;
            Check(Busy("WorkersBusy"), "Paused worker still prevents TileData reuse");
            ThreadManager.Resume(worker);
            workerPaused = false;
            var updates = Counter("DeferredUpdates");
            var refreshed = Counter("DeferredRefreshes");
            bool applied = false;
            CoroutineManager.Enqueue(() => applied = true, name: "Guard test apply");
            map.Update();
            Check(Counter("DeferredUpdates") > updates, "Grid update deferred while worker runs");
            Check(applied, "Apply callbacks continue while grid update deferred");
            map.Refresh(false); map.Refresh(true);
            Check(Counter("DeferredRefreshes") == refreshed + 2, "Refresh requests defer while worker runs");
            Check(coordinates == string.Join(";", map.tiles.All().Select(t => t.coord.ToString()).OrderBy(s => s)),
                "Grid coordinates preserved during worker retirement");
        }
        finally { if (workerPaused) ThreadManager.Resume(worker); releaseWorker.Set(); }
        deadline = Time.realtimeSinceStartupAsDouble + 30;
        while (Busy("WorkersBusy"))
        { if (Time.realtimeSinceStartupAsDouble > deadline) throw new Exception("Worker did not retire"); yield return null; }
        map.Update();
        deadline = Time.realtimeSinceStartupAsDouble + 90;
        while (Busy("WorkersBusy") || Busy("ApplyBusy"))
        { if (Time.realtimeSinceStartupAsDouble > deadline) throw new Exception("Refreshed terrain did not settle"); yield return null; }

        releaseApply = false;
        CoroutineManager.Enqueue(HeldApply(), name: "Guard active-only test");
        CoroutineManager.Update();
        try
        {
            Check(CoroutineManager.IsQueueEmpty && Busy("ApplyBusy"), "Active-only yielding apply is recognized despite empty queue");
            var updates = Counter("DeferredUpdates");
            var refreshed = Counter("DeferredRefreshes");
            map.Update(); map.Refresh(false);
            Check(Counter("DeferredUpdates") > updates, "Grid update deferred for active-only apply");
            Check(Counter("DeferredRefreshes") == refreshed + 1, "Refresh deferred for active-only apply");
        }
        finally { releaseApply = true; }
        map.Update(); map.Update();
        deadline = Time.realtimeSinceStartupAsDouble + 90;
        while (Busy("WorkersBusy") || Busy("ApplyBusy"))
        { if (Time.realtimeSinceStartupAsDouble > deadline) throw new Exception("Terrain did not drain after gate release"); yield return null; }
        Check(true, "Deferred work drains without blocking game thread");
        if (Environment.GetEnvironmentVariable("ND_TERRAIN_GUARD_STRESS") == "1")
        {
            stressMap = map;
            var origin = map.tiles.genAroundTfmsList.First(t => t != null).position;
            stressObservers = new[] { new GameObject("Private terrain stress A").transform, new GameObject("Private terrain stress B").transform };
            for (var i = 0; i < stressObservers.Length; i++)
            {
                stressObservers[i].SetParent(map.transform, true);
                stressObservers[i].position = origin + Vector3.forward * map.tileSize.z * (8 + i * 8);
            }
            Application.logMessageReceivedThreaded += TerrainLog;
            activeStress = this;
            var stressHarmony = new Harmony("odin.nomaddrive.terrainguardprobe.observers");
            stressHarmony.Patch(AccessTools.Method(typeof(MapMagicObject), nameof(MapMagicObject.Update)),
                prefix: new HarmonyMethod(typeof(Probe), nameof(PinStressObservers)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(Probe), nameof(CountStressAdmission)));
            var startedAt = Time.realtimeSinceStartupAsDouble;
            stressLast = startedAt;
            try
            {
                while (Time.realtimeSinceStartupAsDouble - startedAt < 60) yield return null;
                var finalDestinations = stressObservers.Count(observer =>
                {
                    var local = map.transform.InverseTransformPoint(observer.position);
                    var x = Mathf.FloorToInt(local.x / map.tileSize.x);
                    var z = Mathf.FloorToInt(local.z / map.tileSize.z);
                    return map.tiles.All().Any(tile => tile.coord.x == x && tile.coord.z == z && tile.main != null && tile.main.applyReady);
                });
                stressResult = new { seconds = Time.realtimeSinceStartupAsDouble - startedAt, frames = stressFrames,
                    maxFrameSeconds = stressMax, terrainErrors, regions = 3, finalDestinations, remoteClients = 0,
                    stressMapCalls, stressAdmittedCalls, workersBusy = Busy("WorkersBusy"), applyBusy = Busy("ApplyBusy"),
                    tileCount = map.tiles.All().Count(), targets = stressObservers.Select(observer =>
                    {
                        var local = map.transform.InverseTransformPoint(observer.position);
                        var x = Mathf.FloorToInt(local.x / map.tileSize.x); var z = Mathf.FloorToInt(local.z / map.tileSize.z);
                        var tile = map.tiles.All().FirstOrDefault(t => t.coord.x == x && t.coord.z == z);
                        return new { x,z, exists = tile != null, distance = tile?.distance,
                            generated = tile?.main?.generateReady, applied = tile?.main?.applyReady,
                            started = tile?.main?.generateStarted, taskActive = tile?.main?.task?.Active,
                            taskQueued = tile?.main?.task?.Enqueued };
                    }).ToArray() };
                Check(finalDestinations == 2, "Both distant destinations have final applied terrain");
                Check(terrainErrors == 0, "Three-region streaming emitted no MapMagic exceptions during sample");
                Check(stressFrames > 60, "Game frames continue during multi-region generation");
            }
            finally
            {
                Application.logMessageReceivedThreaded -= TerrainLog;
                stressHarmony.UnpatchSelf(); activeStress = null;
                map.tiles.genAroundTfmsList = map.tiles.genAroundTfmsList.Where(t => !stressObservers.Contains(t)).ToArray();
                foreach (var observer in stressObservers) Destroy(observer.gameObject);
                stressObservers = null; stressMap = null;
            }
        }
    }

    private void LateUpdate()
    {
        if (stressObservers == null || stressMap == null) return;
        var now = Time.realtimeSinceStartupAsDouble;
        stressMax = Math.Max(stressMax, now - stressLast); stressLast = now; stressFrames++;
    }

    private static void PinStressObservers(MapMagicObject __instance)
    {
        var probe = activeStress;
        if (probe == null || __instance != probe.stressMap) return;
        probe.stressMapCalls++;
        __instance.tiles.genAroundTfmsList = __instance.tiles.genAroundTfmsList.Concat(probe.stressObservers).Where(t => t != null).Distinct().ToArray();
    }

    private static void CountStressAdmission(MapMagicObject __instance, bool __runOriginal)
    { if (activeStress != null && __instance == activeStress.stressMap && __runOriginal) activeStress.stressAdmittedCalls++; }

    private void TerrainLog(string message, string stack, LogType type)
    {
        if ((type == LogType.Exception || type == LogType.Error) &&
            (message.Contains("MapMagic") || stack.Contains("MapMagic"))) Interlocked.Increment(ref terrainErrors);
    }

    private void OnDestroy() { releaseWorker?.Set(); releaseApply = true; }
}
