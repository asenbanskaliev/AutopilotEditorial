using System.Security.Cryptography;
using System.Text;

namespace BookStudio.OpenCode;

internal sealed class OpenCodeEventDeduplicator
{
    public const int MaximumDedupeEntries = 100_000;

    private readonly int _capacity;
    private readonly Queue<string> _order = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public OpenCodeEventDeduplicator(int capacity)
    {
        if (capacity is < 1 or > MaximumDedupeEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        _capacity = capacity;
    }

    public bool TryAccept(OpenCodeNormalizedProviderEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var key = item.ProviderEventId is not null
            ? "id:" + item.Source + ":" + item.ProviderEventId
            : BuildPayloadKey(item);
        if (!_keys.Add(key))
        {
            return false;
        }
        _order.Enqueue(key);
        while (_order.Count > _capacity)
        {
            var expired = _order.Dequeue();
            _keys.Remove(expired);
        }
        return true;
    }

    private static string BuildPayloadKey(OpenCodeNormalizedProviderEvent item)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, item.Source);
        Append(hash, item.Directory ?? string.Empty);
        hash.AppendData(item.ExactData);
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }
}
