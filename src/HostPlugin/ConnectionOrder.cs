#nullable enable
using System;
using System.Collections.Generic;

namespace NomadDrive.DedicatedServer;

// Connection IDs may be recycled; order belongs to this connection lifetime.
internal sealed class ConnectionOrder<T> where T : class
{
    private readonly List<T> connections = new List<T>();

    internal void Add(T connection)
    {
        if (connection == null) throw new ArgumentNullException(nameof(connection));
        foreach (var existing in connections)
            if (ReferenceEquals(existing, connection)) return;
        connections.Add(connection);
    }

    internal void Remove(T connection) => connections.RemoveAll(existing => ReferenceEquals(existing, connection));

    internal T? FirstEligible(Func<T, bool> eligible)
    {
        foreach (var connection in connections)
            if (eligible(connection)) return connection;
        return null;
    }

    internal T[] Snapshot() => connections.ToArray();
}
