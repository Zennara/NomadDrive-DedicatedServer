using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using Mirror;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.LowLevel;

namespace NomadDrive.RuntimeHealthProbe;

// Diagnostic only. Never install on a public runtime or point at a live save.
[BepInPlugin("odin.nomaddrive.runtimehealthprobe", "Isolated Runtime Health Probe", "0.1.0")]
public sealed class Probe : BaseUnityPlugin
{
    private string root;
    private readonly List<object> results=new List<object>();
    private double lastFrame;
    private readonly List<double> frames=new List<double>();
    private bool measuring;
    private readonly Dictionary<string,LoopTiming> loopTimings=new Dictionary<string,LoopTiming>();
    private sealed class LoopTiming { public long start,total,calls,max; }
    private struct BeginTiming { }
    private struct EndTiming { }
    private void Awake()
    {
        root=Environment.GetEnvironmentVariable("ND_RUNTIME_HEALTH_TEST_ROOT");
        if(string.IsNullOrEmpty(root)){enabled=false;return;}
        root=Path.GetFullPath(root);
        var data=Environment.GetEnvironmentVariable("NOMADDRIVE_SERVER_DATA");
        if(!root.StartsWith("E:\\Odin\\scratch\\nomad-drive\\",StringComparison.OrdinalIgnoreCase)||
            string.IsNullOrEmpty(data)||!string.Equals(Path.GetFullPath(data),Path.Combine(root,"data"),StringComparison.OrdinalIgnoreCase)||
            !File.Exists(Path.Combine(Paths.GameRootPath,".nomaddrive-server-runtime")))
            throw new InvalidOperationException("Runtime health probe requires an isolated scratch data directory.");
        RejectLinks(root);
        StartCoroutine(Run());
    }
    private static void RejectLinks(string path)
    {
        for(var cursor=path;cursor!=null;cursor=Path.GetDirectoryName(cursor))
            if((File.Exists(cursor)||Directory.Exists(cursor))&&(File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidOperationException("Diagnostic isolation cannot traverse links.");
        var pending=new Stack<string>();pending.Push(path);
        while(pending.Count!=0)
        foreach(var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
        {
            var attributes=File.GetAttributes(entry);
            if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Diagnostic data cannot contain links.");
            if((attributes&FileAttributes.Directory)!=0)pending.Push(entry);
        }
    }
    private void Update()
    {
        var now=Time.realtimeSinceStartupAsDouble;
        if(measuring&&lastFrame>0)
        {
            frames.Add(now-lastFrame);
        }
        lastFrame=now;
    }
    private IEnumerator Run()
    {
        var host=AccessTools.TypeByName("NomadDrive.DedicatedServer.Plugin");
        while(!NetworkServer.active||host==null||AccessTools.Field(host,"Instance")?.GetValue(null)==null||
            !(bool)(AccessTools.Field(host,"ready")?.GetValue(AccessTools.Field(host,"Instance").GetValue(null))??false))yield return null;
        yield return new WaitForSecondsRealtime(3);
        PlayerLoop.SetPlayerLoop(AddTimings(PlayerLoop.GetCurrentPlayerLoop()));
        var bodies=UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Include,FindObjectsSortMode.None);
        var dynamic=bodies.Where(b=>b!=null&&b.gameObject.activeInHierarchy&&!b.isKinematic).ToArray();
        results.Add(new {phase="inventory",rigidbodies=bodies.Length,dynamicBodies=dynamic.Length,
            awake=dynamic.Count(b=>!b.IsSleeping()),colliders=UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Include,FindObjectsSortMode.None).Length,
            networkObjects=NetworkServer.spawned.Count,frameLimit=Application.targetFrameRate,vsync=QualitySettings.vSyncCount,
            busyCells=dynamic.GroupBy(b=>new Vector3Int(Mathf.FloorToInt(b.position.x/10),Mathf.FloorToInt(b.position.y/10),Mathf.FloorToInt(b.position.z/10)))
                .OrderByDescending(g=>g.Count()).Take(8).Select(g=>new {cell=g.Key.ToString(),count=g.Count(),sample=g.Take(3).Select(b=>b.name).ToArray()}).ToArray()});
        yield return Measure("baseline",15);
        var saved=dynamic.Select(b=>new {body=b,velocity=b.linearVelocity,spin=b.angularVelocity}).ToArray();
        try
        {
            foreach(var item in saved)if(item.body!=null)item.body.isKinematic=true;
            yield return Measure("dynamic-bodies-frozen",15);
        }
        finally{foreach(var item in saved)if(item.body!=null){item.body.isKinematic=false;item.body.linearVelocity=item.velocity;item.body.angularVelocity=item.spin;}}
        yield return Measure("physics-restored",15);
        File.WriteAllText(Path.Combine(root,"runtime-health.json"),JsonConvert.SerializeObject(results,Formatting.Indented));
        Logger.LogInfo("Runtime health probe completed; original dynamic bodies restored.");
    }
    private IEnumerator Measure(string phase,int seconds)
    {
        frames.Clear();lastFrame=0;measuring=true;
        foreach(var timing in loopTimings.Values){timing.start=timing.total=timing.calls=timing.max=0;}
        var process=System.Diagnostics.Process.GetCurrentProcess();
        var cpu=process.TotalProcessorTime.TotalSeconds;var start=Time.realtimeSinceStartupAsDouble;
        yield return new WaitForSecondsRealtime(seconds);
        measuring=false;process.Refresh();var elapsed=Time.realtimeSinceStartupAsDouble-start;
        results.Add(new {phase,elapsed,frames=frames.Count,maxFrameSeconds=frames.Count==0?0:frames.Max(),
            framesPerSecond=frames.Count/elapsed,cpuPercent=(process.TotalProcessorTime.TotalSeconds-cpu)/elapsed/Environment.ProcessorCount*100,
            loopTimings=loopTimings.Select(p=>new {name=p.Key,calls=p.Value.calls,totalMilliseconds=p.Value.total*1000.0/System.Diagnostics.Stopwatch.Frequency,
                maxMilliseconds=p.Value.max*1000.0/System.Diagnostics.Stopwatch.Frequency}).ToArray()});
        File.WriteAllText(Path.Combine(root,"runtime-health.json"),JsonConvert.SerializeObject(results,Formatting.Indented));
        process.Dispose();
    }
    private PlayerLoopSystem AddTimings(PlayerLoopSystem system)
    {
        if(system.subSystemList==null)return system;
        var children=new List<PlayerLoopSystem>();
        foreach(var original in system.subSystemList)
        {
            var child=AddTimings(original);
            var name=child.type?.FullName??"";
            var timed=name.StartsWith("UnityEngine.PlayerLoop.",StringComparison.Ordinal);
            if(!timed){children.Add(child);continue;}
            if(!loopTimings.TryGetValue(name,out var timing)){timing=new LoopTiming();loopTimings.Add(name,timing);}
            children.Add(new PlayerLoopSystem {type=typeof(BeginTiming),updateDelegate=()=>{if(measuring)timing.start=System.Diagnostics.Stopwatch.GetTimestamp();}});
            children.Add(child);
            children.Add(new PlayerLoopSystem {type=typeof(EndTiming),updateDelegate=()=>{
                if(!measuring||timing.start==0)return;
                var ticks=System.Diagnostics.Stopwatch.GetTimestamp()-timing.start;
                timing.start=0;timing.total+=ticks;timing.calls++;timing.max=Math.Max(timing.max,ticks);
            }});
        }
        system.subSystemList=children.ToArray();return system;
    }
    private static PlayerLoopSystem RemoveTimings(PlayerLoopSystem system)
    {
        if(system.subSystemList!=null)system.subSystemList=system.subSystemList
            .Where(s=>s.type!=typeof(BeginTiming)&&s.type!=typeof(EndTiming)).Select(RemoveTimings).ToArray();
        return system;
    }
    private void OnDestroy(){PlayerLoop.SetPlayerLoop(RemoveTimings(PlayerLoop.GetCurrentPlayerLoop()));}
}
