using System.Text;

namespace Kiosk.Common;

/// <summary>Reads a text file line by line without materializing an oversized line.</summary>
internal sealed class BoundedLineReader
{
    private readonly TextReader reader;
    private readonly int maxChars;
    private readonly char[] buffer = new char[4096];
    private int position;
    private int count;
    private bool skipLf;

    internal BoundedLineReader(TextReader reader, int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);
        this.reader = reader;
        this.maxChars = maxChars;
    }

    internal string? ReadLine(CancellationToken cancellation)
    {
        StringBuilder? line = null;
        var tooLong = false;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (position == count)
            {
                count = reader.Read(buffer, 0, buffer.Length);
                position = 0;
                if (count == 0)
                {
                    if (tooLong) throw new InvalidDataException("Wiersz dziennika statystyk przekracza dozwoloną długość.");
                    return line?.ToString();
                }
            }
            if (skipLf)
            {
                skipLf = false;
                if (buffer[position] == '\n')
                {
                    position++;
                    continue;
                }
            }
            var remaining = buffer.AsSpan(position, count - position);
            var end = remaining.IndexOfAny('\r', '\n');
            var segment = end < 0 ? remaining : remaining[..end];
            if (!tooLong)
            {
                if (segment.Length > maxChars - (line?.Length ?? 0)) tooLong = true;
                else if (line != null) line.Append(segment);
                else if (end >= 0)
                {
                    position += segment.Length;
                    skipLf = buffer[position++] == '\r';
                    return segment.Length == 0 ? string.Empty : new string(segment);
                }
                else
                {
                    line = new StringBuilder(Math.Min(maxChars, Math.Max(256, segment.Length)));
                    line.Append(segment);
                }
            }
            position += segment.Length;
            if (end < 0) continue;
            skipLf = buffer[position++] == '\r';
            if (tooLong) throw new InvalidDataException("Wiersz dziennika statystyk przekracza dozwoloną długość.");
            return line?.ToString() ?? string.Empty;
        }
    }
}
