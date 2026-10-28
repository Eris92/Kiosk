using System.Buffers;
using System.Text;

namespace Kiosk.Client;

/// <summary>Reads one pipe message without allocating an unbounded line before checking its size.</summary>
internal static class BoundedPipeLine
{
    internal static string? Read(StreamReader reader, int maxChars, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        return ReadAsync(reader, maxChars, cancellation.Token).GetAwaiter().GetResult();
    }

    internal static async Task<string?> ReadAsync(StreamReader reader, int maxChars, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);
        var line = new StringBuilder(Math.Min(maxChars, 256));
        var buffer = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            while (true)
            {
                var pending = reader.ReadAsync(buffer.AsMemory(0, 4096), cancellationToken);
                var count = pending.IsCompletedSuccessfully ? pending.Result : await pending.ConfigureAwait(false);
                if (count == 0) return line.Length == 0 ? null : line.ToString();

                var end = buffer.AsSpan(0, count).IndexOfAny('\r', '\n');
                var length = end < 0 ? count : end;
                if (length > maxChars - line.Length) throw new InvalidDataException("Wiadomość potoku przekracza dozwoloną długość.");
                line.Append(buffer, 0, length);
                if (end >= 0) return line.ToString();
            }
        }
        finally { ArrayPool<char>.Shared.Return(buffer, clearArray: true); }
    }
}
