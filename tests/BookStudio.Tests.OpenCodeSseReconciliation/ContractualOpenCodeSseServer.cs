using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BookStudio.Tests.OpenCodeSseReconciliation;

internal sealed class ContractualOpenCodeSseServer : IAsyncDisposable
{
    private const int MaximumHeaderLines = 64;
    private const int MaximumLineBytes = 4096;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<ContractualSseRequest, CancellationToken, ValueTask<ContractualSseResponse>> _handler;
    private readonly ConcurrentQueue<ContractualSseRequest> _requests = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly Task _acceptLoop;
    private int _activeConnections;

    public ContractualOpenCodeSseServer(
        Func<ContractualSseRequest, CancellationToken, ValueTask<ContractualSseResponse>> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        BaseUrl = $"http://127.0.0.1:{endpoint.Port}/";
        _acceptLoop = AcceptLoopAsync();
    }

    public string BaseUrl { get; }

    public IReadOnlyList<ContractualSseRequest> Requests => _requests.ToArray();

    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }

        var connections = _connections.ToArray();
        if (connections.Length > 0)
        {
            try
            {
                await Task.WhenAll(connections).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
        }
        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }

            var connection = HandleConnectionAsync(client, _shutdown.Token);
            _connections.Add(connection);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeConnections);
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var requestLine = await ReadAsciiLineAsync(stream, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }
                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3)
                {
                    await WriteResponseAsync(
                            stream,
                            ContractualSseResponse.Text(400, "bad request"),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var terminated = false;
                for (var index = 0; index < MaximumHeaderLines; index++)
                {
                    var line = await ReadAsciiLineAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (line.Length == 0)
                    {
                        terminated = true;
                        break;
                    }
                    var separator = line.IndexOf(':');
                    if (separator <= 0)
                    {
                        await WriteResponseAsync(
                                stream,
                                ContractualSseResponse.Text(400, "bad headers"),
                                cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }
                    headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }
                if (!terminated)
                {
                    return;
                }

                var request = new ContractualSseRequest(
                    parts[0],
                    NormalizeTarget(parts[1]),
                    headers);
                _requests.Enqueue(request);
                var response = await _handler(request, cancellationToken).ConfigureAwait(false);
                await WriteResponseAsync(stream, response, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        ContractualSseResponse response,
        CancellationToken cancellationToken)
    {
        var reason = response.StatusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            _ => "Status",
        };
        var totalBytes = response.Chunks.Sum(chunk => chunk.Body.Length);
        var builder = new StringBuilder()
            .Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(response.ContentType).Append("\r\n")
            .Append("Cache-Control: no-cache\r\n")
            .Append("Connection: close\r\n");
        if (response.IncludeContentLength)
        {
            builder.Append("Content-Length: ").Append(totalBytes).Append("\r\n");
        }
        builder.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        foreach (var chunk in response.Chunks)
        {
            if (chunk.DelayBefore > TimeSpan.Zero)
            {
                await Task.Delay(chunk.DelayBefore, cancellationToken).ConfigureAwait(false);
            }
            if (chunk.Body.Length > 0)
            {
                await stream.WriteAsync(chunk.Body, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        if (response.HoldOpen > TimeSpan.Zero)
        {
            await Task.Delay(response.HoldOpen, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadAsciiLineAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        while (memory.Length <= MaximumLineBytes)
        {
            var one = new byte[1];
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Unexpected EOF while reading request line.");
            }
            if (one[0] == (byte)'\n')
            {
                var bytes = memory.ToArray();
                var length = bytes.Length > 0 && bytes[^1] == (byte)'\r'
                    ? bytes.Length - 1
                    : bytes.Length;
                return Encoding.ASCII.GetString(bytes, 0, length);
            }
            memory.WriteByte(one[0]);
        }
        throw new IOException("Request line exceeded contractual bound.");
    }

    private static string NormalizeTarget(string target)
    {
        var query = target.IndexOf('?');
        return query >= 0 ? target[..query] : target;
    }
}

internal sealed record ContractualSseRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers);

internal sealed record ContractualSseChunk(byte[] Body, TimeSpan DelayBefore)
{
    public static ContractualSseChunk Utf8(string value, TimeSpan? delayBefore = null) =>
        new(Encoding.UTF8.GetBytes(value), delayBefore ?? TimeSpan.Zero);

    public static ContractualSseChunk Bytes(byte[] value, TimeSpan? delayBefore = null) =>
        new(value, delayBefore ?? TimeSpan.Zero);
}

internal sealed record ContractualSseResponse(
    int StatusCode,
    string ContentType,
    IReadOnlyList<ContractualSseChunk> Chunks,
    bool IncludeContentLength,
    TimeSpan HoldOpen)
{
    public static ContractualSseResponse Json(int statusCode, byte[] body) =>
        new(
            statusCode,
            "application/json",
            [ContractualSseChunk.Bytes(body)],
            IncludeContentLength: true,
            HoldOpen: TimeSpan.Zero);

    public static ContractualSseResponse Text(int statusCode, string body) =>
        new(
            statusCode,
            "text/plain; charset=utf-8",
            [ContractualSseChunk.Utf8(body)],
            IncludeContentLength: true,
            HoldOpen: TimeSpan.Zero);

    public static ContractualSseResponse Sse(
        IReadOnlyList<ContractualSseChunk> chunks,
        TimeSpan? holdOpen = null) =>
        new(
            200,
            "text/event-stream; charset=utf-8",
            chunks,
            IncludeContentLength: false,
            HoldOpen: holdOpen ?? TimeSpan.Zero);
}
