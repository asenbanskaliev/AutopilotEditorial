using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BookStudio.Tests.OpenCodeCompatibility;

internal sealed class ContractualOpenCodeServer : IAsyncDisposable
{
    private const int MaximumHeaderLines = 64;
    private const int MaximumRequestLineLength = 4096;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<ContractualRequest, CancellationToken, ValueTask<ContractualResponse>> _handler;
    private readonly ConcurrentQueue<ContractualRequest> _requests = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly Task _acceptLoop;

    public ContractualOpenCodeServer(
        Func<ContractualRequest, CancellationToken, ValueTask<ContractualResponse>> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        BaseUrl = $"http://127.0.0.1:{endpoint.Port}/";
        _acceptLoop = AcceptLoopAsync();
    }

    public string BaseUrl { get; }

    public IReadOnlyList<ContractualRequest> Requests => _requests.ToArray();

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
        using (var reader = new StreamReader(
                   stream,
                   new UTF8Encoding(false, true),
                   detectEncodingFromByteOrderMarks: false,
                   bufferSize: 4096,
                   leaveOpen: true))
        {
            var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(requestLine) || requestLine.Length > MaximumRequestLineLength)
            {
                await WriteResponseAsync(
                        stream,
                        ContractualResponse.Text(400, "bad request"),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
            {
                await WriteResponseAsync(
                        stream,
                        ContractualResponse.Text(400, "bad request"),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < MaximumHeaderLines; index++)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null || line.Length > MaximumRequestLineLength)
                {
                    await WriteResponseAsync(
                            stream,
                            ContractualResponse.Text(400, "bad headers"),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                if (line.Length == 0)
                {
                    break;
                }
                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    await WriteResponseAsync(
                            stream,
                            ContractualResponse.Text(400, "bad headers"),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }

            var request = new ContractualRequest(
                parts[0],
                NormalizeTarget(parts[1]),
                headers);
            _requests.Enqueue(request);
            var response = await _handler(request, cancellationToken).ConfigureAwait(false);
            if (response.Delay > TimeSpan.Zero)
            {
                await Task.Delay(response.Delay, cancellationToken).ConfigureAwait(false);
            }
            await WriteResponseAsync(stream, response, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        ContractualResponse response,
        CancellationToken cancellationToken)
    {
        var reason = response.StatusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            500 => "Internal Server Error",
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

internal sealed record ContractualRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers);

internal sealed record ContractualResponse(
    int StatusCode,
    string ContentType,
    byte[] Body,
    TimeSpan Delay)
{
    public static ContractualResponse Json(
        int statusCode,
        byte[] body,
        TimeSpan? delay = null) =>
        new(statusCode, "application/json", body, delay ?? TimeSpan.Zero);

    public static ContractualResponse Text(
        int statusCode,
        string body,
        TimeSpan? delay = null) =>
        new(
            statusCode,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes(body),
            delay ?? TimeSpan.Zero);
}
