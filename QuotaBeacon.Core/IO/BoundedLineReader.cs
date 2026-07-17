using System.Runtime.CompilerServices;
using System.Text;

namespace QuotaBeacon.Core.IO;

internal static class BoundedLineReader
{
    public static async IAsyncEnumerable<string> ReadLinesAsync(
        TextReader reader,
        int maxLineChars,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineChars, 1);

        var buffer = new char[Math.Min(maxLineChars, 8 * 1024)];
        var line = new StringBuilder(Math.Min(maxLineChars, 8 * 1024));
        var overLimit = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    if (!overLimit)
                    {
                        if (line.Length > 0 && line[^1] == '\r')
                        {
                            line.Length--;
                        }

                        yield return line.ToString();
                    }

                    line.Clear();
                    overLimit = false;
                    continue;
                }

                if (!overLimit && line.Length < maxLineChars)
                {
                    line.Append(character);
                }
                else
                {
                    overLimit = true;
                }
            }
        }

        if (!overLimit && line.Length > 0)
        {
            if (line[^1] == '\r')
            {
                line.Length--;
            }

            yield return line.ToString();
        }
    }
}
