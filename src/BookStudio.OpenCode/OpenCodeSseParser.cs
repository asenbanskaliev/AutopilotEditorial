using System.Runtime.CompilerServices;
using System.Text;
using BookStudio.Application.OpenCode;

namespace BookStudio.OpenCode;

public sealed record OpenCodeSseParserOptions(
    int MaximumLineBytes,
    int MaximumEventDataBytes,
    int MaximumFieldCount,
    int MaximumEventTypeBytes,
    int MaximumEventIdBytes,
    TimeSpan StallTimeout)
{
    public const int DefaultMaximumLineBytes = 16 * 1024;
    public const int DefaultMaximumEventDataBytes = 256 * 1024;
    public const int DefaultMaximumFieldCount = 256;
    public const int DefaultMaximumEventTypeBytes = 128;
    public const int DefaultMaximumEventIdBytes = 256;

    public static OpenCodeSseParserOptions Default { get; } = new(
        DefaultMaximumLineBytes,
        DefaultMaximumEventDataBytes,
        DefaultMaximumFieldCount,
        DefaultMaximumEventTypeBytes,
        DefaultMaximumEventIdBytes,
        TimeSpan.FromSeconds(60));

    public void Validate()
    {
        if (MaximumLineBytes is < 64 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumLineBytes));
        }
        if (MaximumEventDataBytes is < 1 or > 4 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumEventDataBytes));
        }
        if (MaximumFieldCount is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFieldCount));
        }
        if (MaximumEventTypeBytes is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumEventTypeBytes));
        }
        if (MaximumEventIdBytes is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumEventIdBytes));
        }
        if (StallTimeout < TimeSpan.FromMilliseconds(50) || StallTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(StallTimeout));
        }
    }
}

internal sealed record OpenCodeSseFrame(
    string? Event,
    string? Id,
    int? RetryMilliseconds,
    byte[] Data);

internal static class OpenCodeSseParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static async IAsyncEnumerable<OpenCodeSseFrame> ParseAsync(
        Stream stream,
        OpenCodeSseParserOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var effective = options ?? OpenCodeSseParserOptions.Default;
        effective.Validate();
        var reader = new BoundedLineReader(stream, effective);
        var dataLines = new List<string>();
        string? eventName = null;
        string? eventId = null;
        int? retry = null;
        var fieldCount = 0;
        var dataBytes = 0;
        var firstLine = true;

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }
            if (firstLine)
            {
                line = RemoveUtf8Bom(line);
                firstLine = false;
            }
            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    var joined = string.Join('\n', dataLines);
                    byte[] data;
                    try
                    {
                        data = StrictUtf8.GetBytes(joined);
                    }
                    catch (EncoderFallbackException)
                    {
                        throw Error(OpenCodeEventErrorCodes.SseUtf8Invalid);
                    }
                    yield return new OpenCodeSseFrame(eventName, eventId, retry, data);
                }
                dataLines.Clear();
                eventName = null;
                eventId = null;
                retry = null;
                fieldCount = 0;
                dataBytes = 0;
                continue;
            }
            if (line[0] == ':')
            {
                continue;
            }
            fieldCount++;
            if (fieldCount > effective.MaximumFieldCount)
            {
                throw Error(OpenCodeEventErrorCodes.SseFieldLimitExceeded);
            }

            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }
            switch (field)
            {
                case "data":
                {
                    var bytes = GetByteCount(value);
                    dataBytes = checked(dataBytes + bytes + (dataLines.Count > 0 ? 1 : 0));
                    if (dataBytes > effective.MaximumEventDataBytes)
                    {
                        throw Error(OpenCodeEventErrorCodes.SseEventTooLarge);
                    }
                    dataLines.Add(value);
                    break;
                }
                case "event":
                    ValidateBoundedValue(value, effective.MaximumEventTypeBytes);
                    eventName = value;
                    break;
                case "id":
                    if (value.Contains('\0'))
                    {
                        throw Error(OpenCodeEventErrorCodes.SsePayloadInvalid);
                    }
                    ValidateBoundedValue(value, effective.MaximumEventIdBytes);
                    eventId = value;
                    break;
                case "retry":
                    if (int.TryParse(value, out var parsed) && parsed is >= 0 and <= 600_000)
                    {
                        retry = parsed;
                    }
                    break;
            }
        }
    }

    private static string RemoveUtf8Bom(string value) =>
        value.Length > 0 && value[0] == '\uFEFF' ? value[1..] : value;

    private static void ValidateBoundedValue(string value, int maximumBytes)
    {
        if (GetByteCount(value) > maximumBytes || value.Any(char.IsControl))
        {
            throw Error(OpenCodeEventErrorCodes.SsePayloadInvalid);
        }
    }

    private static int GetByteCount(string value)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw Error(OpenCodeEventErrorCodes.SseUtf8Invalid);
        }
    }

    private static OpenCodeEventReconciliationException Error(string code) => new(code);

    private sealed class BoundedLineReader
    {
        private readonly Stream _stream;
        private readonly OpenCodeSseParserOptions _options;
        private readonly byte[] _readBuffer = new byte[4096];
        private int _offset;
        private int _length;

        public BoundedLineReader(Stream stream, OpenCodeSseParserOptions options)
        {
            _stream = stream;
            _options = options;
        }

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            using var line = new MemoryStream(Math.Min(_options.MaximumLineBytes, 4096));
            while (true)
            {
                var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (next is null)
                {
                    if (line.Length == 0)
                    {
                        return null;
                    }
                    return Decode(line.ToArray(), stripCr: false);
                }
                if (next.Value == (byte)'\n')
                {
                    var bytes = line.ToArray();
                    return Decode(bytes, stripCr: bytes.Length > 0 && bytes[^1] == (byte)'\r');
                }
                if (line.Length >= _options.MaximumLineBytes)
                {
                    throw Error(OpenCodeEventErrorCodes.SseLineTooLarge);
                }
                line.WriteByte(next.Value);
            }
        }

        private async ValueTask<byte?> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (_offset >= _length)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                stall.CancelAfter(_options.StallTimeout);
                try
                {
                    _length = await _stream.ReadAsync(_readBuffer, stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw Error(OpenCodeEventErrorCodes.SseStalled);
                }
                _offset = 0;
                if (_length == 0)
                {
                    return null;
                }
            }
            return _readBuffer[_offset++];
        }

        private static string Decode(byte[] bytes, bool stripCr)
        {
            var length = stripCr ? bytes.Length - 1 : bytes.Length;
            try
            {
                return StrictUtf8.GetString(bytes, 0, length);
            }
            catch (DecoderFallbackException)
            {
                throw Error(OpenCodeEventErrorCodes.SseUtf8Invalid);
            }
        }
    }
}
