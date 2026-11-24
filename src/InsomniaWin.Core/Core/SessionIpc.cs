using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InsomniaWin.Core.Models;
using InsomniaWin.Core.Store;

namespace InsomniaWin.Core.Core;

public sealed record SessionIpcRequest(string Command, int? Seconds = null);

public sealed record SessionIpcResponse(bool Ok, string Message);

public sealed record CliCommandOutput(string Line, int ExitCode);

/// <summary>
/// Named Pipe IPC server & CLI handler for:
///   InsomniaWin.exe --session start &lt;seconds&gt; | extend &lt;seconds&gt; | end | status
/// </summary>
public sealed class SessionIpcServer : IDisposable
{
    private const int PipeBufferSize = 4096;
    private const int MaxFrameBytes = 65536;

    private readonly AppPaths _paths;
    private readonly SessionManager _manager;
    private readonly IClock _clock;
    private readonly IAppLogger _log;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public SessionIpcServer(AppPaths paths, SessionManager manager, IClock clock, IAppLogger log)
    {
        _paths = paths;
        _manager = manager;
        _clock = clock;
        _log = log;
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        var initialServer = CreateServerInstance();
        _loopTask = Task.Run(() => AcceptLoopAsync(initialServer, _cts.Token));
    }

    private NamedPipeServerStream CreateServerInstance() =>
        new(
            _paths.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: PipeBufferSize,
            outBufferSize: PipeBufferSize);

    private async Task AcceptLoopAsync(NamedPipeServerStream? nextServer, CancellationToken ct)
    {
        var server = nextServer;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                server ??= CreateServerInstance();
                await server.WaitForConnectionAsync(ct);
                var connected = server;
                server = null;
                await HandleClientAsync(connected, ct);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                server = null;
                _log.Error($"Session IPC server error: {ex.Message}");
                try
                {
                    await Task.Delay(250, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        await using (server)
        {
            using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ioCts.CancelAfter(TimeSpan.FromSeconds(30));

            var payload = await ReadFrameAsync(server, MaxFrameBytes, ioCts.Token);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return;
            }

            SessionIpcResponse response;
            try
            {
                var req = JsonSerializer.Deserialize<SessionIpcRequest>(payload, SessionCli.WireJsonOptions);
                response = req is null
                    ? new SessionIpcResponse(false, "Invalid request.")
                    : await ExecuteRequestAsync(req);
            }
            catch (Exception ex)
            {
                response = new SessionIpcResponse(false, ex.Message);
            }

            var respJson = JsonSerializer.Serialize(response, SessionCli.WireJsonOptions);
            await WriteFrameAsync(server, respJson, ioCts.Token);
        }
    }

    private async Task<SessionIpcResponse> ExecuteRequestAsync(SessionIpcRequest req)
    {
        switch (req.Command.ToLowerInvariant())
        {
            case "start":
                if (req.Seconds is not > 0)
                    return new SessionIpcResponse(false, "start requires positive seconds.");
                var (okStart, errStart) = await _manager.TryStartAsync(TimeSpan.FromSeconds(req.Seconds.Value));
                return okStart
                    ? new SessionIpcResponse(true, SessionCli.Describe(_manager.CurrentSnapshot.ActiveSession, _clock.Now))
                    : new SessionIpcResponse(false, errStart ?? "Could not start session.");

            case "extend":
                if (req.Seconds is not > 0)
                    return new SessionIpcResponse(false, "extend requires positive seconds.");
                var (okExt, errExt) = await _manager.TryExtendAsync(TimeSpan.FromSeconds(req.Seconds.Value));
                return okExt
                    ? new SessionIpcResponse(true, SessionCli.Describe(_manager.CurrentSnapshot.ActiveSession, _clock.Now))
                    : new SessionIpcResponse(false, errExt ?? "Could not extend session.");

            case "end":
                var outcome = await _manager.EndAsync(EndReason.User);
                return outcome == EndOutcome.Restored
                    ? new SessionIpcResponse(true, "no session is running")
                    : new SessionIpcResponse(false, _manager.CurrentSnapshot.LastError ?? $"End finished with status: {outcome}");

            case "status":
                return new SessionIpcResponse(true, SessionCli.Describe(_manager.CurrentSnapshot.ActiveSession, _clock.Now));

            default:
                return new SessionIpcResponse(false, $"Unknown command '{req.Command}'.");
        }
    }

    internal static async Task WriteFrameAsync(Stream stream, string text, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), payload.Length);
        Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    internal static async Task<string?> ReadFrameAsync(Stream stream, int maxFrameBytes, CancellationToken ct)
    {
        var header = new byte[4];
        var headerOk = await ReadExactOrEofAsync(stream, header, ct);
        if (!headerOk) return null;

        var len = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (len <= 0 || len > maxFrameBytes)
        {
            throw new InvalidDataException($"Invalid IPC frame length: {len}");
        }

        var payload = new byte[len];
        var bodyOk = await ReadExactOrEofAsync(stream, payload, ct);
        if (!bodyOk) return null;

        return Encoding.UTF8.GetString(payload);
    }

    private static async Task<bool> ReadExactOrEofAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}

public static class SessionCli
{
    public const int ExitOk = 0;
    public const int ExitFailure = 1;
    public const int ExitUsage = 64;
    private const int MaxFrameBytes = 65536;

    internal static readonly JsonSerializerOptions WireJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<CliCommandOutput> ExecuteAsync(
        string[] sessionArgs,
        AppPaths paths,
        JsonStore store,
        IClock clock)
    {
        if (sessionArgs.Length == 0)
        {
            return new CliCommandOutput("usage: InsomniaWin.exe --session start <seconds> | extend <seconds> | end | status", ExitUsage);
        }

        var verb = sessionArgs[0].ToLowerInvariant();
        if (verb == "status" && sessionArgs.Length == 1)
        {
            return store.LoadSession() switch
            {
                SessionLoadResult.Missing => new CliCommandOutput("no session is running", ExitOk),
                SessionLoadResult.Loaded loaded => new CliCommandOutput(Describe(loaded.Session, clock.Now), ExitOk),
                SessionLoadResult.Unreadable bad => new CliCommandOutput($"cannot read session.json: {bad.Error}", ExitFailure),
                _ => new CliCommandOutput("no session is running", ExitOk)
            };
        }

        SessionIpcRequest? request = null;
        if (verb == "end" && sessionArgs.Length == 1)
        {
            request = new SessionIpcRequest("end");
        }
        else if ((verb is "start" or "extend") && sessionArgs.Length == 2
                 && int.TryParse(sessionArgs[1], out var seconds) && seconds > 0)
        {
            request = new SessionIpcRequest(verb, seconds);
        }

        if (request is null)
        {
            return new CliCommandOutput("usage: InsomniaWin.exe --session start <seconds> | extend <seconds> | end | status", ExitUsage);
        }

        try
        {
            using var client = new NamedPipeClientStream(".", paths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000);

            using var ioCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var reqJson = JsonSerializer.Serialize(request, WireJsonOptions);
            await SessionIpcServer.WriteFrameAsync(client, reqJson, ioCts.Token);

            var respJson = await SessionIpcServer.ReadFrameAsync(client, MaxFrameBytes, ioCts.Token);
            if (string.IsNullOrWhiteSpace(respJson))
            {
                return new CliCommandOutput("InsomniaWin did not return a response.", ExitFailure);
            }

            var resp = JsonSerializer.Deserialize<SessionIpcResponse>(respJson, WireJsonOptions);
            if (resp is null)
            {
                return new CliCommandOutput("InsomniaWin returned an invalid response.", ExitFailure);
            }

            return new CliCommandOutput(resp.Message, resp.Ok ? ExitOk : ExitFailure);
        }
        catch (TimeoutException)
        {
            return new CliCommandOutput("InsomniaWin is not running, so nothing took the command; open the app and try again.", ExitFailure);
        }
        catch (OperationCanceledException)
        {
            return new CliCommandOutput("InsomniaWin did not answer within 30 s.", ExitFailure);
        }
        catch (IOException ex)
        {
            return new CliCommandOutput($"Could not reach InsomniaWin: {ex.Message}", ExitFailure);
        }
    }

    public static string Describe(Session? session, DateTimeOffset now)
    {
        if (session is null || session.IsExpired(now))
        {
            return "no session is running";
        }

        var localEnd = session.EndsAt.ToLocalTime();
        var roundedSeconds = Math.Ceiling(session.RemainingSeconds(now) / 60.0) * 60.0;
        return $"running until {localEnd:O} ({SessionMath.FormatRemaining(roundedSeconds)} left)";
    }
}
