#nullable enable
using System;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace JacintoLauncher;

/// <summary>
/// Tells a running Discord that this is Gears of War 3. Discord lists "geargame.exe" as Gears of War 4, which is why the launcher
/// starts the game under another file name (GameRunner.GameExecutable) and names the game here, with Discord's own id for it.
/// Purely cosmetic, so any failure is ignored.
/// </summary>
internal static class DiscordPresence
{
    private const string GearsOfWar3 = "1445163391604752586";
    private static Stream? connection;

    internal static void Start(int gameProcess) => _ = Task.Run(async () =>
    {
        try
        {
            var stream = await ConnectAsync();
            if (stream is null) { LauncherDiagnostics.Info("Discord presence: Discord is not running."); return; }
            await SendAsync(stream, 0, new JsonObject { ["v"] = 1, ["client_id"] = GearsOfWar3 });
            await ReadAsync(stream);
            await SendAsync(stream, 1, new JsonObject
            {
                ["cmd"] = "SET_ACTIVITY", ["nonce"] = Guid.NewGuid().ToString(),
                ["args"] = new JsonObject { ["pid"] = gameProcess, ["activity"] = new JsonObject { ["timestamps"] = new JsonObject { ["start"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() } } }
            });
            var reply = await ReadAsync(stream);
            LauncherDiagnostics.Info("Discord presence: " + (reply.Contains("\"evt\":\"ERROR\"", StringComparison.Ordinal) ? reply : "set to Gears of War 3."));
            connection = stream; // Discord drops the activity when this closes.
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or SocketException or UnauthorizedAccessException or OperationCanceledException) { LauncherDiagnostics.Info("Discord presence skipped: " + exception.Message); }
    });

    internal static void Stop() { var open = connection; connection = null; try { open?.Dispose(); } catch (IOException) { } }

    private static async Task<Stream?> ConnectAsync()
    {
        for (var i = 0; i < 10; i++)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var pipe = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                    try { await pipe.ConnectAsync(300); return pipe; } catch { pipe.Dispose(); throw; }
                }
                var directory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath();
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(Path.Combine(directory, "discord-ipc-" + i))); return new NetworkStream(socket, true); } catch { socket.Dispose(); throw; }
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or SocketException) { }
        }
        return null;
    }

    private static async Task SendAsync(Stream stream, int opcode, JsonObject body)
    {
        var json = Encoding.UTF8.GetBytes(body.ToJsonString());
        var frame = new byte[8 + json.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0), opcode);
        BitConverter.TryWriteBytes(frame.AsSpan(4), json.Length);
        json.CopyTo(frame, 8);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    private static async Task<string> ReadAsync(Stream stream)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var body = new byte[BitConverter.ToInt32(header, 4)];
        await stream.ReadExactlyAsync(body).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return Encoding.UTF8.GetString(body);
    }
}
