using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NomadDrive.Server;

public static partial class SafeFiles
{
    public static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Inside(string path, string root) => Full(path).StartsWith(Full(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static bool Overlaps(string a, string b) => Full(a).Equals(Full(b), StringComparison.OrdinalIgnoreCase) || Inside(a, b) || Inside(b, a);

    public static void RejectReparseAncestors(string path)
    {
        var current = Full(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Reparse points are not permitted in server paths: {current}");
            current = Path.GetDirectoryName(current);
        }
    }

    public static void CheckTree(string root)
    {
        RejectReparseAncestors(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Reparse point in input tree: {entry}");
            if (Directory.Exists(entry)) CheckTree(entry);
        }
    }

    public static void CopyTree(string source, string destination)
    {
        CheckTree(source);
        RejectReparseAncestors(destination);
        if (Overlaps(source, destination)) throw new IOException("Source and destination trees must not overlap.");
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Copy destination already exists.");
        CopyChecked(source, destination);
    }

    private static void CopyChecked(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException($"Reparse point in input tree: {entry}");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyChecked(entry, target);
            else File.Copy(entry, target, false);
        }
    }

    public static void AtomicJson<T>(string path, T value)
    {
        RejectReparseAncestors(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value, ServerConfig.Json);
            stream.Flush(true);
        }
        // Readers and filesystem scanners can briefly deny replacement on Windows.
        // Retry the same flushed file; never delete or partially overwrite the target.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                RejectReparseAncestors(path);
                File.Move(temporary, path, true);
                return;
            }
            catch (Exception ex) when (attempt < 40 && OperatingSystem.IsWindows() &&
                ex is IOException or UnauthorizedAccessException && (ex.HResult & 0xffff) is 5 or 32 or 33)
            {
                Thread.Sleep(50);
            }
        }
    }

    public static FileStream AcquireLock(string data)
    {
        RejectReparseAncestors(data);
        Directory.CreateDirectory(data);
        try { return new FileStream(Path.Combine(data, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("This data directory is already in use by a supervisor or import operation.", ex); }
    }

    public static string ImportSlot(string source, string data)
    {
        using var instanceLock = AcquireLock(data);
        RuntimeGuard.RejectLiveGame(data);
        source = Full(source);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        CheckTree(source);
        if (!File.Exists(Path.Combine(source, "save.dat")) && !File.Exists(Path.Combine(source, "autosave.dat")))
            throw new InvalidDataException("Import source must be a save slot directory containing save.dat or autosave.dat.");
        var worldPath = Path.Combine(data, "world.json");
        if (File.Exists(worldPath)) throw new IOException("This data directory already has a world. Use a new data directory to import another world.");
        var slotId = "slot_" + Guid.NewGuid().ToString("N");
        CopyTree(source, Path.Combine(data, "saves", slotId));
        AtomicJson(worldPath, new WorldSelection { SlotId = slotId });
        return slotId;
    }

    public static WorldSelection? ReadWorld(string data)
    {
        var path = Path.Combine(data, "world.json");
        if (!File.Exists(path)) return null;
        var world = JsonSerializer.Deserialize<WorldSelection>(File.ReadAllText(path), ServerConfig.Json)
            ?? throw new InvalidDataException("Invalid world.json.");
        if (!SlotPattern().IsMatch(world.SlotId)) throw new InvalidDataException("Invalid slotId in world.json.");
        var slot = Path.Combine(data, "saves", world.SlotId);
        if (!Directory.Exists(slot)) throw new InvalidDataException("The selected save slot is missing. Refusing to create a replacement world.");
        CheckTree(slot);
        return world;
    }

    public static async Task WithVerifiedCheckpointAsync(string data, string slotId, Func<Task> action)
    {
        if (ReadWorld(data)?.SlotId != slotId || string.IsNullOrEmpty(slotId))
            throw new InvalidDataException("The offline host did not identify the selected world.");
        var slot = Path.Combine(data, "saves", slotId);
        // Prevent a native writer or replacement from changing the verified pair before the child exits.
        using var metadata = File.OpenRead(Path.Combine(slot, "meta.json"));
        var saved = JsonSerializer.Deserialize<CheckpointMetadata>(metadata, Protocol.Json);
        if (saved?.SlotId != slotId || saved.ContentFile != "save.dat" || saved.ContentSize <= 0)
            throw new InvalidDataException("The selected world has no complete save.dat metadata.");
        using var content = File.OpenRead(Path.Combine(slot, "save.dat"));
        if (content.Length != saved.ContentSize ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(content)), saved.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected save.dat does not match its size and SHA-256 metadata.");
        await action();
    }

    private sealed record CheckpointMetadata(string SlotId, string ContentFile, long ContentSize, string ContentHash);

    [GeneratedRegex("^slot_[a-zA-Z0-9_-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex SlotPattern();
}

public sealed class WorldSelection
{
    public string SlotId { get; set; } = "";
}

public sealed partial class BackupStore(string root, int keep)
{
    public string Create(string snapshotPath, string runDirectory, DateTimeOffset now)
    {
        ValidateSnapshotPath(snapshotPath, runDirectory);
        SafeFiles.CheckTree(snapshotPath);
        if (!Directory.EnumerateFiles(snapshotPath, "*", SearchOption.AllDirectories).Any()) throw new InvalidDataException("Snapshot is empty.");
        SafeFiles.RejectReparseAncestors(root);
        Directory.CreateDirectory(root);
        var name = $"nomaddrive-{now:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.zip";
        var destination = Path.Combine(root, name);
        var temporary = destination + ".partial";
        ZipFile.CreateFromDirectory(snapshotPath, temporary, CompressionLevel.Optimal, false);
        using (var archive = ZipFile.OpenRead(temporary))
        {
            if (archive.Entries.Count == 0) throw new InvalidDataException("Backup archive is empty.");
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                var original = Path.GetFullPath(Path.Combine(snapshotPath, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!SafeFiles.Inside(original, snapshotPath)) throw new InvalidDataException("Backup archive entry escaped the snapshot.");
                using var stream = entry.Open();
                using var source = File.OpenRead(original);
                if (!SHA256.HashData(stream).SequenceEqual(SHA256.HashData(source)))
                    throw new InvalidDataException("Backup verification failed for " + entry.FullName);
            }
        }
        File.Move(temporary, destination);
        Prune(destination);
        RemoveArchivedSnapshot(snapshotPath, runDirectory);
        return destination;
    }

    private static void ValidateSnapshotPath(string snapshotPath, string runDirectory)
    {
        var snapshots = Path.Combine(runDirectory, "snapshots");
        if (!string.Equals(SafeFiles.Full(Path.GetDirectoryName(SafeFiles.Full(snapshotPath))!), SafeFiles.Full(snapshots), StringComparison.OrdinalIgnoreCase) ||
            !Protocol.ValidId(Path.GetFileName(SafeFiles.Full(snapshotPath))))
            throw new InvalidDataException("Plugin snapshot must be a request-ID directory directly inside this launch's snapshots directory.");
    }

    private static void RemoveArchivedSnapshot(string path, string runDirectory)
    {
        ValidateSnapshotPath(path, runDirectory);
        SafeFiles.CheckTree(path);
        var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
        var directories = Directory.GetDirectories(path, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length).ToArray();
        foreach (var file in files)
        {
            SafeFiles.RejectReparseAncestors(file);
            File.Delete(file);
        }
        foreach (var directory in directories)
        {
            SafeFiles.RejectReparseAncestors(directory);
            Directory.Delete(directory, recursive: false);
        }
        SafeFiles.RejectReparseAncestors(path);
        Directory.Delete(path, recursive: false);
    }

    public void Prune(string? preserve = null)
    {
        if (keep < 1) throw new InvalidDataException("Backup retention must keep at least one archive.");
        SafeFiles.RejectReparseAncestors(root);
        if (!Directory.Exists(root)) return;
        var owned = Directory.EnumerateFiles(root, "nomaddrive-*.zip")
            .Where(p => ArchivePattern().IsMatch(Path.GetFileName(p)))
            .OrderByDescending(p => p.Equals(preserve, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(Path.GetFileName, StringComparer.Ordinal).ToArray();
        foreach (var path in owned.Skip(keep))
        {
            SafeFiles.RejectReparseAncestors(path);
            File.Delete(path);
        }
    }

    [GeneratedRegex(@"^nomaddrive-\d{8}T\d{13}Z-[0-9a-f]{32}\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex ArchivePattern();
}
