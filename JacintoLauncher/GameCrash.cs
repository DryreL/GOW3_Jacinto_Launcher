#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace JacintoLauncher;

/// <summary>
/// Finds game crashes from what the engine leaves in GearGame\Logs: a minidump (unreal-v*.dmp) or a fatal line in Launch.log.
/// Only text goes into a report; the dump's memory stays on the machine.
/// </summary>
internal static class GameCrash
{
    internal sealed record Report(string Title, string Fingerprint, string? ExceptionCode, string? ExceptionAddress, string LogTail);
    private const int TailLines = 150;
    private static readonly string[] FatalMarks = { "Critical:", "Assertion failed", "Fatal error" };

    /// <summary>A crash newer than <paramref name="since"/>, or null. <paramref name="seen"/> is the time to check from next.</summary>
    internal static Report? Find(string gameDir, DateTime since, out DateTime seen)
    {
        seen = since;
        var logs = Path.Combine(gameDir, "GearGame", "Logs");
        if (!Directory.Exists(logs)) return null;
        var dump = new DirectoryInfo(logs).EnumerateFiles("*.dmp").Where(f => f.LastWriteTimeUtc > since).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        var log = new FileInfo(Path.Combine(logs, "Launch.log"));
        var fatal = (string?)null; var tail = "";
        if (log.Exists && log.LastWriteTimeUtc > since) (fatal, tail) = ReadLog(log.FullName);
        if (dump is not null && dump.LastWriteTimeUtc > seen) seen = dump.LastWriteTimeUtc;
        if (log.Exists && log.LastWriteTimeUtc > seen) seen = log.LastWriteTimeUtc;
        if (dump is null && fatal is null) return null;
        var (code, address) = dump is null ? (null, null) : ReadDumpException(dump.FullName);
        var title = fatal ?? (code is null ? "Game crash" : "Game crash " + code + " at " + address);
        // Engine asserts all raise the same code at the same address, so the message comes first. Its numbers differ per run; the same bug must land in one issue.
        var fingerprint = fatal is not null ? new string(fatal.Where(c => !char.IsDigit(c)).ToArray()) : code + "@" + address;
        return new Report(title.Length > 300 ? title[..300] : title, fingerprint, code, address, tail);
    }

    private static (string? Fatal, string Tail) ReadLog(string path)
    {
        var lines = new Queue<string>();
        string? fatal = null;
        // The game may still hold the file; StreamReader detects its UTF-16 byte order mark.
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), detectEncodingFromByteOrderMarks: true);
        for (string? line; (line = reader.ReadLine()) is not null;)
        {
            fatal ??= FatalMarks.Any(mark => line.Contains(mark, StringComparison.OrdinalIgnoreCase)) ? line.Trim() : null;
            lines.Enqueue(line);
            if (lines.Count > TailLines) lines.Dequeue();
        }
        return (fatal, string.Join("\n", lines));
    }

    // Minidump layout: header with the stream directory, then the exception stream (type 6): thread id, padding, then the exception record.
    internal static (string? Code, string? Address) ReadDumpException(string path)
    {
        try
        {
            using var reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            if (reader.ReadUInt32() != 0x504D444D) return (null, null); // "MDMP"
            reader.BaseStream.Position = 8;
            var count = reader.ReadUInt32(); var directory = reader.ReadUInt32();
            for (uint i = 0; i < count; i++)
            {
                reader.BaseStream.Position = directory + i * 12;
                var type = reader.ReadUInt32(); reader.ReadUInt32(); var offset = reader.ReadUInt32();
                if (type != 6) continue;
                reader.BaseStream.Position = offset + 8;
                var code = reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt64(); var address = reader.ReadUInt64();
                return ("0x" + code.ToString("X8", CultureInfo.InvariantCulture), "0x" + address.ToString("X", CultureInfo.InvariantCulture));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { LauncherDiagnostics.Info("Crash dump unreadable: " + exception.Message); }
        return (null, null);
    }
}
