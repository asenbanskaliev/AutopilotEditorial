using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BookStudio.Tests.OpenCodeSessionLifecycle;

internal sealed class ContractualOpenCodeSessionServer : IAsyncDisposable
{
    private const int MaximumHeaderLines = 64;
    private const int MaximumLineBytes = 4096;
    private const int MaximumBodyBytes = 2 * 1024 * 1024;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<ContractualSessionRequest, CancellationToken, ValueTask<ContractualSessionResponse>> _handler;
    private readonly ConcurrentQueue<ContractualSessionRequest> _requests = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly Task _acceptLoop;

    public ContractualOpenCodeSessionServer(
        Func<ContractualSessionRequest, CancellationToken, ValueTask<ContractualSessionResponse>> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        BaseUrl = $"http://127.0.0.1:{endpoint.Port}/";
        _acceptLoop = AcceptLoopAsync();
    }

    public string BaseUrl { get; }

    public IReadOnlyList<ContractualSessionRequest> Requests => _requests.ToArray();

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
        using (client)
        await using (var stream = client.GetStream())
        {
            var requestLine = await ReadAsciiLineAsync(stream, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                await WriteResponseAsync(
                        stream,
                        ContractualSessionResponse.Text(400, "bad request"),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
            {
                await WriteResponseAsync(
                        stream,
                        ContractualSessionResponse.Text(400, "bad request"),
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
                            ContractualSessionResponse.Text(400, "bad headers"),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
            if (!terminated)
            {
                await WriteResponseAsync(
                        stream,
                        ContractualSessionResponse.Text(400, "too many headers"),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var bodyLength = 0;
            if (headers.TryGetValue("Content-Length", out var lengthText) &&
                (!int.TryParse(lengthText, out bodyLength) || bodyLength is < 0 or > MaximumBodyBytes))
            {
                await WriteResponseAsync(
                        stream,
                        ContractualSessionResponse.Text(400, "bad content length"),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            var body = bodyLength == 0
                ? Array.Empty<byte>()
                : await ReadExactlyAsync(stream, bodyLength, cancellationToken).ConfigureAwait(false);
            var request = new ContractualSessionRequest(
                parts[0],
                NormalizeTarget(parts[1]),
                headers,
                body);
            _requests.Enqueue(request);

            var response = await _handler(request, cancellationToken).ConfigureAwait(false);
            if (response.Delay > TimeSpan.Zero)
            {
                await Task.Delay(response.Delay, cancellationToken).ConfigureAwait(false);
            }
            await WriteResponseAsync(stream, response, cancellationToken).ConfigureAwait(false);
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

    private static async Task<byte[]> ReadExactlyAsync(
        Stream stream,
        int length,
        CancellationToken cancellationToken)
    {
        var result = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(
                    result.AsMemory(offset, length - offset),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Unexpected EOF while reading request body.");
            }
            offset += read;
        }
        return result;
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        ContractualSessionResponse response,
        CancellationToken cancellationToken)
    {
        var reason = response.StatusCode switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            409 => "Conflict",
            413 => "Payload Too Large",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            _ => "Status",
        };
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {response.StatusCode} {reason}\r\n" +
            $"Content-Type: {response.ContentType}\r\n" +
            $"Content-Length: {response.Body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (response.Body.Length > 0)
        {
            await stream.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeTarget(string target)
    {
        var query = target.IndexOf('?');
        return query >= 0 ? target[..query] : target;
    }
}

internal sealed record ContractualSessionRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body);

internal sealed record ContractualSessionResponse(
    int StatusCode,
    string ContentType,
    byte[] Body,
    TimeSpan Delay)
{
    public static ContractualSessionResponse Json(
        int statusCode,
        byte[] body,
        TimeSpan? delay = null) =>
        new(statusCode, "application/json", body, delay ?? TimeSpan.Zero);

    public static ContractualSessionResponse Text(
        int statusCode,
        string body,
        TimeSpan? delay = null) =>
        new(
            statusCode,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes(body),
            delay ?? TimeSpan.Zero);

    public static ContractualSessionResponse NoContent(TimeSpan? delay = null) =>
        new(204, "application/json", [], delay ?? TimeSpan.Zero);
}
