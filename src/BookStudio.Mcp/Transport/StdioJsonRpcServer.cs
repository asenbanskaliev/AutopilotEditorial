using System.Text;
using System.Text.Json;
using BookStudio.Mcp.Protocol;

namespace BookStudio.Mcp.Transport;

/// <summary>UTF-8 newline-delimited MCP JSON-RPC server over standard input and output.</summary>
public sealed class StdioJsonRpcServer
{
    public const int MaximumMessageBytes = 1_048_576;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    };

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly McpSession _session;

    public StdioJsonRpcServer(
        TextReader input,
        TextWriter output,
        TextWriter error,
        McpSession session)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _error = error ?? throw new ArgumentNullException(nameof(error));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return 0;
                }

                if (Encoding.UTF8.GetByteCount(line) > MaximumMessageBytes)
                {
                    await WriteResponseAsync(
                            JsonRpcMessageWriter.Error(
                                null,
                                JsonRpcErrorCodes.InvalidRequest,
                                "Message exceeds the 1 MiB stdio limit."))
                        .ConfigureAwait(false);
                    await WriteDiagnosticAsync("MCP_MESSAGE_TOO_LARGE").ConfigureAwait(false);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    await WriteResponseAsync(
                            JsonRpcMessageWriter.Error(
                                null,
                                JsonRpcErrorCodes.InvalidRequest,
                                "A JSON-RPC message must be a non-empty object."))
                        .ConfigureAwait(false);
                    await WriteDiagnosticAsync("MCP_EMPTY_MESSAGE").ConfigureAwait(false);
                    continue;
                }

                McpDispatchResult result;
                try
                {
                    using var document = JsonDocument.Parse(line, DocumentOptions);
                    result = await _session.DispatchAsync(
                            document.RootElement,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    result = new McpDispatchResult(
                        JsonRpcMessageWriter.Error(
                            null,
                            JsonRpcErrorCodes.ParseError,
                            "Parse error"),
                        "MCP_PARSE_ERROR");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return 0;
                }
                catch (Exception)
                {
                    result = new McpDispatchResult(
                        JsonRpcMessageWriter.Error(
                            null,
                            JsonRpcErrorCodes.InternalError,
                            "Internal error"),
                        "MCP_TRANSPORT_INTERNAL_ERROR");
                }

                if (result.Response is not null)
                {
                    await WriteResponseAsync(result.Response).ConfigureAwait(false);
                }

                if (result.DiagnosticCode is not null)
                {
                    await WriteDiagnosticAsync(result.DiagnosticCode).ConfigureAwait(false);
                }
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            _session.Close();
            await _output.FlushAsync().ConfigureAwait(false);
            await _error.FlushAsync().ConfigureAwait(false);
        }
    }

    private async Task WriteResponseAsync(string response)
    {
        await _output.WriteLineAsync(response).ConfigureAwait(false);
        await _output.FlushAsync().ConfigureAwait(false);
    }

    private async Task WriteDiagnosticAsync(string diagnosticCode)
    {
        var safeCode = new string(diagnosticCode
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            .Take(96)
            .ToArray());
        if (safeCode.Length == 0)
        {
            safeCode = "MCP_DIAGNOSTIC";
        }

        await _error.WriteLineAsync(safeCode).ConfigureAwait(false);
        await _error.FlushAsync().ConfigureAwait(false);
    }
}
