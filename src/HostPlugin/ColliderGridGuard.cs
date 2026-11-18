using System.Collections.Generic;
using HarmonyLib;
using NomadDrive.Features.ColliderLOD;
using UnityEngine;

namespace NomadDrive.DedicatedServer;

[HarmonyPatch(typeof(ColliderLodSpatialGrid), nameof(ColliderLodSpatialGrid.QueryRing))]
internal static class ColliderGridGuard
{
    private static bool Prefix(ColliderLodSpatialGrid __instance, Vector3 center, float radius,
        HashSet<ColliderLodEntry> results, Dictionary<long, List<ColliderLodEntry>> ____cells)
    {
        var cellRadius = radius / __instance.CellSize;
        if (!(cellRadius > 8f)) return true;

        // A position jump can turn the velocity margin into a world-sized query.
        // Visit existing buckets instead of walking every empty cell in that square.
        if (cellRadius >= int.MaxValue)
        {
            foreach (var bucket in ____cells.Values) results.UnionWith(bucket);
            return false;
        }

        var ring = Mathf.CeilToInt(cellRadius);
        var centerX = Mathf.FloorToInt(center.x / __instance.CellSize);
        var centerZ = Mathf.FloorToInt(center.z / __instance.CellSize);
        foreach (var bucket in ____cells)
        {
            // Match the native packed cell keys, including signed coordinate wrap.
            var dx = unchecked((int)(bucket.Key >> 32) - centerX);
            var dz = unchecked((int)bucket.Key - centerZ);
            if (dx >= -ring && dx <= ring && dz >= -ring && dz <= ring)
                results.UnionWith(bucket.Value);
        }
        return false;
    }
}
