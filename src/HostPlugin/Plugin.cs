using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using Cysharp.Threading.Tasks;
using EvilCore;
using EvilCore.EvilSave;
using EvilCore.Networking;
using HarmonyLib;
using Mirror;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NomadDrive.Features.SaveSystem;
using NomadDrive.Features.FloatingOrigin;
using NomadDrive.Features.TextChat;
using NomadDrive.Features.WorldGeneration;
using NomadDrive.Features.WorldGeneration.ObjectSpawning;
using UnityEngine;
using Player = NomadDrive.Features.Player.Player;

namespace NomadDrive.DedicatedServer;

[BepInPlugin("odin.nomaddrive.dedicatedserver", "Nomad Drive Dedicated Server", "0.3.0")]
public sealed class Plugin : BaseUnityPlugin
{
    internal static Plugin Instance;
    internal static GameSaveService Saves;
    internal static JObject Settings;
    internal static string DataRoot, RunRoot;
    internal static bool SaveAuthorized;
    /// <summary>Host plugins may finish durable work before a planned shutdown. False cancels shutdown.</summary>
    public static Func<UniTask<bool>> BeforeShutdown;
    public static Action ShutdownCanceled;
    private bool preparingShutdown, pluginCheckpoint;
    internal static bool RestoreCompleted;
    internal static int AppliedTiles;
    internal static Vector3? ImportAnchor;
    internal static int HumanLimit => (int)Settings["lobby"]["maxPlayers"];
    private bool busy, ready, failed;
    private bool hasTravelAnchor;
    private bool closing;
    private readonly HashSet<NetworkConnectionToClient> pendingPlayers = new HashSet<NetworkConnectionToClient>();
    private readonly ConnectionOrder<NetworkConnectionToClient> connectionOrder = new ConnectionOrder<NetworkConnectionToClient>();
    private float pollAt;
    private Transform observer;
    private WorldGenerator world;
    private EOSLobbyManager lobby;
    private string detail = "Initializing hosting plugin";
    private readonly HashSet<string> processed = new HashSet<string>();
    private PositionState positions = new PositionState();
    private const string SupportedAssembly = "6957F9A1180D811CA2AE1BB44748157D794FED5BEFD845290FD15E41FB54FFAF";

    private void Awake()
    {
        var config = Environment.GetEnvironmentVariable("NOMADDRIVE_SERVER_CONFIG");
        if (string.IsNullOrEmpty(config)) return; // Ordinary BepInEx sessions are untouched.
        Instance = this;
        try
        {
            DataRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("NOMADDRIVE_SERVER_DATA") ?? throw new Exception("Missing server data path"));
            RunRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("NOMADDRIVE_SERVER_RUN") ?? throw new Exception("Missing run path"));
            Settings = JObject.Parse(File.ReadAllText(config));
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                throw new Exception("No-graphics hosting cannot preserve paint/restoration saves. Run the hidden graphics-enabled server.");
            Directory.CreateDirectory(Path.Combine(RunRoot, "requests"));
            Directory.CreateDirectory(Path.Combine(RunRoot, "responses"));
            using (var hash = SHA256.Create())
            using (var stream = File.OpenRead(typeof(Player).Assembly.Location))
                if (BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "") != SupportedAssembly)
                    throw new Exception("Unsupported Assembly-CSharp build. Refusing version-specific patches; rebuild/review adapter first.");
            new Harmony("odin.nomaddrive.dedicatedserver").PatchAll(typeof(Plugin).Assembly);
            ConfigureSaveStorage();
            Application.runInBackground = true;
            Application.targetFrameRate = 30;
            WriteStatus();
            StartCoroutine(Boot());
        }
        catch (Exception ex) { Fail(ex); }
    }

    internal static void ConfigureSaveStorage()
    {
        // Patch GetSaveRootPath too: the built-in Custom option appends an extra EvilSave directory.
        var settings = EvilSaveSettings.Instance;
        settings.EnableAutoSave = false;
        settings.EnableCloudSync = false;
        settings.PlayerPrefsPrefix = "NomadDriveDedicated_";
        Directory.CreateDirectory(Path.Combine(DataRoot, "saves"));
    }

    private IEnumerator Boot()
    {
        // Services are created by BootstrapLifetimeScope. Constructor patches capture the save service.
        while (Saves == null) yield return null;
        detail = "Waiting for EOS device authentication";
        var auth = UnityEngine.Object.FindAnyObjectByType<EOSAuthManager>();
        while (auth == null || !auth.IsLoggedIn)
        {
            auth = UnityEngine.Object.FindAnyObjectByType<EOSAuthManager>();
            yield return new WaitForSecondsRealtime(0.25f);
        }
        var network = (MirrorNetworkManager)NetworkManager.singleton;
        var flow = (ISceneFlowManager)AccessTools.Field(typeof(MirrorNetworkManager), "_sceneFlowManager").GetValue(network);
        while (flow.IsTransitioning) yield return null;
        try { StartLobby(); }
        catch (Exception ex) { Fail(ex); }
    }

    private void StartLobby()
    {
        ConfigureSaveStorage();
        var statePath = Path.Combine(DataRoot, "world.json");
        if (File.Exists(statePath))
        {
            var slot = (string)JObject.Parse(File.ReadAllText(statePath))["slotId"];
            CheckSlot(slot);
            if (!Directory.Exists(Path.Combine(DataRoot, "saves", slot))) throw new Exception("Recorded world is missing; refusing to create a replacement.");
            var result = Saves.SelectSlotForContinue(slot);
            if (result.ToString() == "FreshWorld" && EvilSave.GetSlotMetadata(slot)?.HasEverBeenSaved != false)
                throw new Exception("Recorded slot has no save data and cannot be proven never saved; refusing a replacement world.");
            if (result.ToString() != "Loaded" && result.ToString() != "RecoveredFromBackup" && result.ToString() != "FreshWorld")
                throw new Exception("Cannot load saved world: " + result);
        }
        else
        {
            Saves.MarkNewGame((string)Settings["world"]["name"]);
            AtomicJson(statePath, new { slotId = Saves.ActiveSlotId });
        }
        var positionsPath = Path.Combine(EvilSave.GetSlotDirectory(Saves.ActiveSlotId), "dedicated-positions.json");
        if (File.Exists(positionsPath))
        {
            positions = JsonConvert.DeserializeObject<PositionState>(File.ReadAllText(positionsPath)) ?? throw new Exception("Invalid dedicated positions sidecar");
            if (positions.anchor != null) ImportAnchor = positions.anchor.Vector;
        }
        hasTravelAnchor = ImportAnchor.HasValue;
        var network = (MirrorNetworkManager)NetworkManager.singleton;
        network.autoCreatePlayer = false;
        network.maxConnections = HumanLimit + 1;
        lobby = UnityEngine.Object.FindAnyObjectByType<EOSLobbyManager>();
        if (lobby == null) throw new Exception("EOS lobby service unavailable");
        var w = Settings["world"];
        var preset = (WorldDifficultyPreset)Enum.Parse(typeof(WorldDifficultyPreset), (string)w["preset"]);
        var economy = preset == WorldDifficultyPreset.Custom
            ? WorldEconomySettings.Create(preset, (byte)w["foodDensity"], (byte)w["waterDensity"], (byte)w["fuelDensity"], (byte)w["partsDensity"], (byte)w["medicineDensity"], (byte)Math.Min(8, HumanLimit))
            : WorldEconomySettings.FromPreset(preset, HumanLimit);
        detail = "Creating EOS lobby and loading world";
        lobby.CreateGameWithLobby(new LobbyCreateOptions {
            LobbyName = (string)Settings["lobby"]["name"], MaxPlayers = (uint)(HumanLimit + 1),
            IsPublic = (bool)Settings["lobby"]["public"], Password = (string)Settings["lobby"]["password"],
            Seed = (int)w["seed"], EconomySettings = economy
        });
    }

    private void Update()
    {
        if (Instance != this || failed || Time.realtimeSinceStartup < pollAt) return;
        pollAt = Time.realtimeSinceStartup + 0.5f;
        try
        {
            FollowPlayers();
            if (ready && (!NetworkServer.active || !NetworkClient.activeHost)) throw new Exception("Host networking stopped unexpectedly.");
            if (!ready && world != null && world.IsWorldFullyReady && AppliedTiles > 0 && Saves != null &&
                (!Saves.IsLoadedWorld || RestoreCompleted) && !Saves.IsRestoring && Saves.CanSaveNow)
            {
                if ((bool)AccessTools.Field(typeof(GameSaveService), "_restoreFailed").GetValue(Saves) ||
                    (int)AccessTools.Field(typeof(GameSaveService), "_restoreSkippedCount").GetValue(Saves) > 0)
                    throw new Exception("World restoration failed; saves and server readiness blocked.");
                ready = true;
                detail = "World ready";
                Logger.LogInfo("Server ready; join code " + ((IEOSLobbyManager)lobby).JoinCode);
            }
            if (ready && !closing)
                foreach (var connection in connectionOrder.Snapshot().Where(c => pendingPlayers.Contains(c)))
                {
                    pendingPlayers.Remove(connection);
                    if (NetworkServer.connections.TryGetValue(connection.connectionId, out var current) && ReferenceEquals(current, connection) && connection.identity == null)
                        NetworkManager.singleton.OnServerAddPlayer(connection);
                }
            WriteStatus();
            if (!busy)
            {
                var path = Directory.GetFiles(Path.Combine(RunRoot, "requests"), "*.json").OrderBy(p => p).FirstOrDefault(p => !processed.Contains(p));
                if (path != null) { processed.Add(path); HandleRequest(path).Forget(); }
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void FollowPlayers()
    {
        if (!NetworkServer.active) return;
        world = UnityEngine.Object.FindAnyObjectByType<WorldGenerator>();
        if (world == null) return;
        if (observer == null)
        {
            observer = new GameObject("Dedicated terrain observer").transform;
            if (FloatingOriginManager.Instance != null) observer.SetParent(FloatingOriginManager.Instance.WorldContentRoot, true);
            observer.position = ImportAnchor.HasValue ? FloatingOriginManager.ToRenderWorld(ImportAnchor.Value) : world.PlayerSpawnPoint;
            world.SetTerrainObserver(observer);
        }
        var players = connectionOrder.Snapshot().Where(IsActiveHuman)
            .Select(c => c.identity.GetComponent<Player>()).ToArray();
        var focuses = players.Select(Focus).ToArray();
        if (players.Length > 0) { observer.position = focuses[0].position; hasTravelAnchor = true; }
        else if (!hasTravelAnchor) observer.position = world.PlayerSpawnPoint;
        foreach (var player in players)
        {
            if (!string.IsNullOrEmpty(player.EosProductUserId))
                positions.players[player.EosProductUserId] = new Point(FloatingOriginManager.ToTrueWorld(Focus(player).position));
        }
        positions.anchor = new Point(FloatingOriginManager.ToTrueWorld(observer.position));
        var map = AccessTools.Field(typeof(WorldGenerator), "_mapMagic").GetValue(world);
        if (map == null) return;
        var tiles = AccessTools.Field(map.GetType(), "tiles").GetValue(map);
        AccessTools.Field(tiles.GetType(), "genAroundTfmsList").SetValue(tiles, focuses.Length > 0 ? focuses : new[] { observer });
        AccessTools.Field(tiles.GetType(), "genAroundMainCam").SetValue(tiles, false);
    }

    private async UniTaskVoid HandleRequest(string path)
    {
        busy = true;
        string id = Path.GetFileNameWithoutExtension(path);
        bool quit = false;
        bool shutdownRequested = false;
        try
        {
            if (!Guid.TryParse(id, out _)) throw new Exception("Invalid request ID");
            var request = JObject.Parse(File.ReadAllText(path));
            if ((string)request["id"] != id) throw new Exception("Request ID mismatch");
            var command = (string)request["command"];
            shutdownRequested = command == "shutdown";
            if (!ready) throw new Exception("Server is not ready: " + detail);
            string snapshot = null;
            switch (command)
            {
                case "say": Broadcast((string)request["text"]); break;
                case "save": case "snapshot": case "shutdown":
                    if (command == "shutdown")
                    {
                        closing = true;
                        preparingShutdown = true;
                        try
                        {
                            var callbacks = BeforeShutdown;
                            if (callbacks != null)
                                foreach (Func<UniTask<bool>> callback in callbacks.GetInvocationList())
                                    if (!await callback()) throw new Exception("A host plugin could not prepare a safe shutdown; action canceled.");
                        }
                        finally { preparingShutdown = false; }
                    }
                    SaveAuthorized = true;
                    bool saved;
                    try { saved = await Saves.SaveAndWaitAsync(); }
                    finally { SaveAuthorized = false; }
                    if (!saved || !Saves.LastSaveSucceeded) throw new Exception("Game did not complete a successful save; action canceled.");
                    AtomicJson(Path.Combine(EvilSave.GetSlotDirectory(Saves.ActiveSlotId), "dedicated-positions.json"), positions);
                    if (command == "snapshot")
                    {
                        snapshot = Path.Combine(RunRoot, "snapshots", id);
                        CopyTree(EvilSave.GetSlotDirectory(Saves.ActiveSlotId), snapshot);
                    }
                    quit = command == "shutdown";
                    break;
                default: throw new Exception("Unknown plugin command: " + command);
            }
            AtomicJson(Path.Combine(RunRoot, "responses", id + ".json"), new { id, ok = true, slotId = Saves.ActiveSlotId, snapshotPath = snapshot });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            AtomicJson(Path.Combine(RunRoot, "responses", id + ".json"), new { id, ok = false, error = ex.Message });
        }
        finally
        {
            busy = false;
            if (!quit)
            {
                closing = false;
                if (shutdownRequested && ShutdownCanceled != null)
                    foreach (Action callback in ShutdownCanceled.GetInvocationList())
                        try { callback(); } catch (Exception error) { Logger.LogError("Shutdown cancellation callback failed: " + error); }
            }
        }
        if (quit)
        {
            ready = false;
            detail = "Saved; shutting down";
            WriteStatus();
            await UniTask.Delay(250, ignoreTimeScale: true);
            NetworkManager.singleton.StopHost();
            Application.Quit(0);
        }
    }

    /// <summary>Run on the Unity thread. Serialize plugin checkpoints with supervisor commands.</summary>
    public static async UniTask<bool> SavePluginCheckpointAsync()
    {
        var host = Instance;
        if (host == null || !host.ready || host.failed || Saves == null || host.pluginCheckpoint ||
            (host.busy && !host.preparingShutdown) || (host.closing && !host.preparingShutdown)) return false;
        var ownsBusy = !host.busy;
        host.busy = true;
        host.pluginCheckpoint = true;
        SaveAuthorized = true;
        try
        {
            var saved = await Saves.SaveAndWaitAsync();
            if (!saved || !Saves.LastSaveSucceeded) return false;
            AtomicJson(Path.Combine(EvilSave.GetSlotDirectory(Saves.ActiveSlotId), "dedicated-positions.json"), host.positions);
            return true;
        }
        finally
        {
            SaveAuthorized = false;
            host.pluginCheckpoint = false;
            if (ownsBusy) host.busy = false;
        }
    }

    internal static void Broadcast(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256 || text.Any(char.IsControl)) throw new Exception("Chat text must be 1..256 printable characters.");
        var chat = UnityEngine.Object.FindAnyObjectByType<TextChatManager>() ?? throw new Exception("Chat manager unavailable");
        var sequence = AccessTools.Field(typeof(TextChatManager), "_nextSequence");
        uint next = (uint)sequence.GetValue(chat);
        sequence.SetValue(chat, next + 1);
        var line = new ChatLine(ChatLineKind.Player, 0, "Server", "dedicated-server", text, next, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        AccessTools.Method(typeof(TextChatManager), "RpcLine").Invoke(chat, new object[] { line });
    }

    internal static Vector3 SpawnPoint(Player player, WorldGenerator world)
    {
        var reference = OldestPlayer(player);
        if (reference != null)
        {
            // Use the game's ordinary near-host offset, substituting the oldest human.
            var radius = (float)AccessTools.Field(typeof(MirrorNetworkManager), "nearHostSpawnRadius").GetValue(NetworkManager.singleton);
            var offset = UnityEngine.Random.insideUnitCircle * radius;
            return reference.transform.position + new Vector3(offset.x, 0f, offset.y);
        }
        return Instance.hasTravelAnchor && Instance.positions.anchor != null ? FloatingOriginManager.ToRenderWorld(Instance.positions.anchor.Vector) : world.PlayerSpawnPoint;
    }

    private static bool IsActiveHuman(NetworkConnectionToClient connection) =>
        !(connection is LocalConnectionToClient) &&
        NetworkServer.connections.TryGetValue(connection.connectionId, out var current) && ReferenceEquals(current, connection) &&
        connection.identity != null && connection.identity.GetComponent<Player>() != null;

    internal static Player OldestPlayer(Player excluding = null)
    {
        var connection = Instance.connectionOrder.FirstEligible(c => IsActiveHuman(c) && c.identity.GetComponent<Player>() != excluding);
        return connection == null ? null : connection.identity.GetComponent<Player>();
    }

    internal static void TrackConnection(NetworkConnectionToClient connection)
    {
        if (!(connection is LocalConnectionToClient)) Instance.connectionOrder.Add(connection);
    }

    internal static void ForgetConnection(NetworkConnectionToClient connection)
    {
        Instance.pendingPlayers.Remove(connection);
        Instance.connectionOrder.Remove(connection);
    }

    internal static Transform Focus(Player player) => player.SeatedVehicleFocusTransform ?? player.DownedFocusTransform ?? player.transform;

    internal static bool AdmitPlayer(NetworkConnectionToClient connection)
    {
        if (connection is LocalConnectionToClient) return false;
        TrackConnection(connection);
        if (Instance.failed || Instance.closing) { connection.Disconnect(); return false; }
        if (Instance.ready) return true;
        Instance.pendingPlayers.Add(connection);
        return false;
    }

    private sealed class PositionState
    {
        public Point anchor;
        public Dictionary<string, Point> players = new Dictionary<string, Point>();
    }
    private sealed class Point
    {
        public float x, y, z;
        public Point() { }
        public Point(Vector3 point) { x = point.x; y = point.y; z = point.z; }
        [JsonIgnore] public Vector3 Vector => new Vector3(x, y, z);
    }

    private void WriteStatus()
    {
        try
        {
            AtomicJson(Path.Combine(RunRoot, "status.json"), new {
                state = failed ? "error" : ready ? "ready" : "starting", detail,
                slotId = Saves?.ActiveSlotId, players = NetworkServer.connections.Values.Count(c => !(c is LocalConnectionToClient) && c.identity != null),
                maxPlayers = HumanLimit, lobbyCode = lobby == null ? null : ((IEOSLobbyManager)lobby).JoinCode, updatedUtc = DateTime.UtcNow.ToString("O")
            });
        }
        catch (IOException ex)
        {
            // An external Windows reader can deny replacement. Retry next heartbeat;
            // a status-file lock must not turn a healthy game session into a failed one.
            Logger.LogWarning("Status publication deferred: " + ex.Message);
        }
    }

    private void Fail(Exception ex)
    {
        failed = true; ready = false; detail = ex.Message;
        Logger.LogError(ex);
        if (RunRoot != null && Settings != null) WriteStatus();
    }

    internal static void AtomicJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(value, Formatting.Indented));
        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
    }

    internal static void CheckSlot(string slot)
    {
        if (string.IsNullOrEmpty(slot) || slot != Path.GetFileName(slot) || slot.Contains("..") || !slot.StartsWith("slot_")) throw new Exception("Unsafe save slot ID");
    }

    internal static void CopyTree(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new Exception("Snapshots cannot traverse reparse points");
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.GetFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new Exception("Snapshots cannot traverse reparse points");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0) CopyTree(entry, target); else File.Copy(entry, target, false);
        }
    }
}

[HarmonyPatch]
internal static class CaptureSaveService
{
    static MethodBase TargetMethod() => typeof(GameSaveService).GetConstructors().Single();
    static void Postfix(GameSaveService __instance) { Plugin.Saves = __instance; __instance.OnRestoreComplete += () => Plugin.RestoreCompleted = true; }
}

[HarmonyPatch(typeof(EvilSaveSettings), nameof(EvilSaveSettings.GetSaveRootPath))]
internal static class IsolatedSaves
{
    static bool Prefix(ref string __result) { __result = Path.Combine(Plugin.DataRoot, "saves"); return false; }
}

[HarmonyPatch(typeof(EOSAuthManager), "Awake")]
internal static class DeviceAuthentication
{
    static void Prefix(EOSAuthManager __instance) => AccessTools.Field(typeof(EOSAuthManager), "forceDeviceIdLogin").SetValue(__instance, true);
}

[HarmonyPatch(typeof(MirrorNetworkManager), "Construct")]
internal static class OnlineBootstrap
{
    static void Prefix(MirrorNetworkManager __instance, IReadOnlyList<IOnlineService> onlineServices)
    {
        foreach (var service in onlineServices)
            if (service is EOSAuthManager auth)
                AccessTools.Field(typeof(EOSAuthManager), "forceDeviceIdLogin").SetValue(auth, true);
        AccessTools.Field(typeof(MirrorNetworkManager), "activeTransportType").SetValue(__instance, TransportType.Online);
        __instance.autoCreatePlayer = false;
        __instance.maxConnections = Plugin.HumanLimit + 1;
    }
    static void Postfix(MirrorNetworkManager __instance) => AccessTools.Method(typeof(MirrorNetworkManager), "RestoreDefaultTransport").Invoke(__instance, null);
}

[HarmonyPatch(typeof(EOSLobbyManager), "get_PendingMaxPlayers")]
internal static class HumanPartySize
{
    static bool Prefix(ref uint __result) { __result = (uint)Plugin.HumanLimit; return false; }
}

[HarmonyPatch(typeof(EOSLobbyManager), "CreateLobbyWithBackoffAsync")]
internal static class LargeLobbyVoice
{
    static void Prefix(ref Epic.OnlineServices.Lobby.CreateLobbyOptions createOptions)
    {
        if (Plugin.HumanLimit > 15) createOptions.EnableRTCRoom = false;
    }
}

[HarmonyPatch(typeof(MirrorNetworkManager), nameof(MirrorNetworkManager.OnServerConnect))]
internal static class RecordHumanConnection
{
    static void Postfix(NetworkConnectionToClient conn) => Plugin.TrackConnection(conn);
}

[HarmonyPatch(typeof(MirrorNetworkManager), nameof(MirrorNetworkManager.OnServerDisconnect))]
internal static class RemoveHumanConnection
{
    static void Prefix(NetworkConnectionToClient conn) => Plugin.ForgetConnection(conn);
}

[HarmonyPatch(typeof(MirrorNetworkManager), nameof(MirrorNetworkManager.OnServerAddPlayer))]
internal static class NoHostAvatar
{
    static bool Prefix(NetworkConnectionToClient conn) => Plugin.AdmitPlayer(conn);
    static void Postfix(NetworkConnectionToClient conn)
    {
        if (conn is LocalConnectionToClient || conn.identity == null) return;
        var world = UnityEngine.Object.FindAnyObjectByType<WorldGenerator>();
        var player = conn.identity.GetComponent<Player>();
        if (world != null && player != null)
        {
            var position = Plugin.SpawnPoint(player, world);
            // The vanilla method only sends a TargetRpc. Seed the server transform
            // before another queued admission can use this player as its reference.
            player.transform.position = position;
            player.ServerApplyInitialSpawnNearHost(position);
        }
    }
}

[HarmonyPatch(typeof(WorldGenerator), "OnTileApplied")]
internal static class ConfirmTerrain
{
    static void Postfix(object data)
    {
        if (!(bool)AccessTools.Field(data.GetType(), "isDraft").GetValue(data)) Plugin.AppliedTiles++;
    }
}

[HarmonyPatch(typeof(NetworkServer), nameof(NetworkServer.SetClientReady))]
internal static class ReadyHostObserver
{
    static void Postfix(NetworkConnectionToClient conn)
    {
        if (conn is LocalConnectionToClient && conn.identity == null)
            AccessTools.Method(typeof(NetworkServer), "SpawnObserversForConnection").Invoke(null, new object[] { conn });
    }
}

[HarmonyPatch(typeof(LootInterestManagement), nameof(LootInterestManagement.OnCheckObserver))]
internal static class HostObservesObjects
{
    static bool Prefix(NetworkConnectionToClient newObserver, ref bool __result)
    {
        if (!(newObserver is LocalConnectionToClient)) return true;
        __result = true; return false;
    }
}

[HarmonyPatch(typeof(LootInterestManagement), nameof(LootInterestManagement.OnRebuildObservers))]
internal static class KeepHostObserver
{
    static void Postfix(HashSet<NetworkConnectionToClient> newObservers)
    {
        if (NetworkServer.localConnection != null && NetworkServer.localConnection.isReady)
            newObservers.Add(NetworkServer.localConnection);
    }
}

[HarmonyPatch(typeof(FloatingOriginManager), "TryGetFocus")]
internal static class RemoteOriginFocus
{
    static bool Prefix(ref Vector3 focus, ref bool __result)
    {
        var player = Plugin.OldestPlayer();
        __result = player != null;
        if (player != null) focus = Plugin.Focus(player).position;
        return false;
    }
}

[HarmonyPatch(typeof(GameSaveService), "CaptureThumbnailAsync")]
internal static class NoHeadlessThumbnail
{
    static bool Prefix(ref UniTask __result) { __result = UniTask.CompletedTask; return false; }
}

[HarmonyPatch(typeof(GameSaveService), nameof(GameSaveService.SaveAndWaitAsync))]
internal static class ManagedSaveGate
{
    static bool Prefix(ref UniTask<bool> __result)
    {
        if (Plugin.SaveAuthorized) return true;
        __result = UniTask.FromResult(false); return false;
    }
}

[HarmonyPatch(typeof(GameSaveService), nameof(GameSaveService.SaveNow))]
internal static class ManagedSyncSaveGate { static bool Prefix() => Plugin.SaveAuthorized; }

[HarmonyPatch(typeof(GameSaveManifest), nameof(GameSaveManifest.Deserialize))]
internal static class ImportHostAsRemote
{
    static void Postfix(GameSaveManifest __result)
    {
        if (__result?.HostPlayer == null) return;
        Plugin.ImportAnchor = __result.HostPlayer.Position;
        if (!string.IsNullOrEmpty(__result.HostPlayer.Puid) && !__result.RemotePlayers.Any(p => p.Puid == __result.HostPlayer.Puid))
            __result.RemotePlayers.Add(__result.HostPlayer);
    }
}

[HarmonyPatch(typeof(GameSaveService), "ApplyHostPlayerWhenReady")]
internal static class SkipHostRestore { static bool Prefix() => false; }

[HarmonyPatch(typeof(GameSaveService), "ApplyHostPlayerLinksAsync")]
internal static class SkipHostLinks
{
    static bool Prefix(ref UniTask __result) { __result = UniTask.CompletedTask; return false; }
}
