using System;
using System.Collections;
using System.IO;
using BepInEx;
using Epic.OnlineServices;
using Epic.OnlineServices.Lobby;
using EpicTransport;
using EvilCore.Networking;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NomadDrive.VoiceRoomProbe;

// Observes the real game's EOS calls. Does not join rooms, capture audio or modify options.
[BepInPlugin("odin.nomaddrive.voiceroomprobe", "Isolated Voice Room Probe", "0.1.0")]
public sealed class Probe : BaseUnityPlugin
{
    internal static Probe Instance;
    private string root;
    private Harmony patches;
    private uint expectedMembers;
    private readonly JObject evidence = new JObject { ["mediaVerified"] = false };

    private void Awake()
    {
        root = Environment.GetEnvironmentVariable("ND_VOICE_TEST_ROOT");
        if (string.IsNullOrEmpty(root)) { enabled = false; return; }
        root = Path.GetFullPath(root);
        var data = Environment.GetEnvironmentVariable("NOMADDRIVE_SERVER_DATA");
        if (!root.StartsWith("E:\\Odin\\scratch\\nomad-drive\\", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(Paths.GameRootPath).StartsWith("E:\\Odin\\scratch\\nomad-drive\\", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(data) || !string.Equals(Path.GetFullPath(data), Path.Combine(root, "data"), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(Paths.GameRootPath, ".nomaddrive-server-runtime")))
            throw new InvalidOperationException("Voice room probe requires isolated scratch data and runtime.");
        RejectLinks(root);
        RejectLinks(Paths.GameRootPath);
        var config = Environment.GetEnvironmentVariable("NOMADDRIVE_SERVER_CONFIG");
        if (string.IsNullOrEmpty(config)) throw new InvalidOperationException("Voice room probe requires the supervisor startup configuration.");
        RejectLinks(Path.GetFullPath(config));
        var settings = JObject.Parse(File.ReadAllText(config));
        var humans=(int?)settings["lobby"]?["maxPlayers"];
        if ((bool?)settings["lobby"]?["public"] != false || (humans!=15&&humans!=63))
            throw new InvalidOperationException("Voice room probe requires a private 15- or 63-human configuration.");
        expectedMembers=(uint)humans.Value+1;
        evidence["configuredHumanSlots"]=humans.Value;
        Instance = this;
        patches = new Harmony("odin.nomaddrive.voiceroomprobe");
        patches.PatchAll(typeof(Probe).Assembly);
        StartCoroutine(Observe());
    }

    private static void RejectLinks(string path)
    {
        for (var cursor = path; cursor != null; cursor = Path.GetDirectoryName(cursor))
            if ((Directory.Exists(cursor) || File.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Voice room probe cannot traverse links.");
    }

    internal void RecordCreate(CreateLobbyOptions options)
    {
        evidence["requestedMaxLobbyMembers"] = options.MaxLobbyMembers;
        evidence["requestedEnableRTCRoom"] = options.EnableRTCRoom;
        evidence["requestedJoinAction"] = options.RTCRoomJoinActionType.ToString();
        Write();
    }

    private IEnumerator Observe()
    {
        var deadline = Time.realtimeSinceStartupAsDouble + 180;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            var manager = UnityEngine.Object.FindAnyObjectByType<EOSLobbyManager>();
            var lobbyId = manager == null ? null : ((IEOSLobbyManager)manager).CurrentLobbyId;
            var api = EOSSDKComponent.GetLobbyInterface();
            if (api != null && !string.IsNullOrEmpty(lobbyId))
            {
                var copy = new CopyLobbyDetailsHandleOptions { LobbyId = lobbyId, LocalUserId = EOSSDKComponent.LocalUserProductId };
                var copyResult = api.CopyLobbyDetailsHandle(ref copy, out var details);
                evidence["copyLobbyDetailsResult"] = copyResult.ToString();
                if (copyResult == Result.Success && details != null)
                {
                    try
                    {
                        var infoOptions = new LobbyDetailsCopyInfoOptions();
                        var infoResult = details.CopyInfo(ref infoOptions, out var info);
                        evidence["lobbyInfoResult"] = infoResult.ToString();
                        if (infoResult == Result.Success && info.HasValue)
                        {
                            evidence["actualMaxMembers"] = info.Value.MaxMembers;
                        }
                    }
                    finally { details.Release(); }
                }
                var roomOptions = new GetRTCRoomNameOptions { LobbyId = lobbyId, LocalUserId = EOSSDKComponent.LocalUserProductId };
                var roomResult = api.GetRTCRoomName(ref roomOptions, out var roomName);
                evidence["roomNameResult"] = roomResult.ToString();
                evidence["roomNamePresent"] = !string.IsNullOrEmpty(roomName);
                var connectedOptions = new IsRTCRoomConnectedOptions { LobbyId = lobbyId, LocalUserId = EOSSDKComponent.LocalUserProductId };
                var connectedResult = api.IsRTCRoomConnected(ref connectedOptions, out var connected);
                evidence["connectionQueryResult"] = connectedResult.ToString();
                evidence["hostRTCConnected"] = connected;
                evidence["roomCreationPassed"] = (uint?)evidence["requestedMaxLobbyMembers"] == expectedMembers &&
                    (bool?)evidence["requestedEnableRTCRoom"] == true && (uint?)evidence["actualMaxMembers"] == expectedMembers &&
                    roomResult == Result.Success && !string.IsNullOrEmpty(roomName);
                Write();
                if ((bool)evidence["roomCreationPassed"] && connectedResult == Result.Success && connected)
                {
                    evidence["finished"] = true;
                    Write();
                    yield break;
                }
            }
            yield return new WaitForSecondsRealtime(1);
        }
        evidence["finished"] = true;
        evidence["timedOut"] = true;
        Write();
    }

    private void Write() => File.WriteAllText(Path.Combine(root, "voice-room.json"), evidence.ToString(Formatting.Indented));

    private void OnDestroy() { patches?.UnpatchSelf(); Instance = null; }
}

[HarmonyPatch(typeof(EOSLobbyManager), "CreateLobbyOnceAsync")]
internal static class ObserveCreateOptions
{
    static void Prefix(CreateLobbyOptions createOptions) => Probe.Instance?.RecordCreate(createOptions);
}
