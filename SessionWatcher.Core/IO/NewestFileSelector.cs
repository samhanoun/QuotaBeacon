namespace SessionWatcher.Core.IO;

internal static class NewestFileSelector
{
    public static IReadOnlyList<FileInfo> FindNewest(
        string root,
        string extension,
        int maxFiles,
        int maxEntries,
        bool oldestFirst,
        CancellationToken cancellationToken)
    {
        var selected = new PriorityQueue<FileInfo, long>();
        var pending = new Stack<string>();
        pending.Push(root);
        var inspectedEntries = 0;
        var budgetExhausted = false;

        while (pending.Count > 0 && !budgetExhausted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    inspectedEntries++;
                    if (inspectedEntries > maxEntries)
                    {
                        budgetExhausted = true;
                        break;
                    }

                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entry);
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    if (!string.Equals(Path.GetExtension(entry), extension, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var file = new FileInfo(entry);
                    var priority = file.LastWriteTimeUtc.Ticks;
                    if (selected.Count < maxFiles)
                    {
                        selected.Enqueue(file, priority);
                    }
                    else if (selected.TryPeek(out _, out var oldestPriority) && priority > oldestPriority)
                    {
                        _ = selected.Dequeue();
                        selected.Enqueue(file, priority);
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // One inaccessible branch must not hide readable session data.
            }
        }

        var files = selected.UnorderedItems.Select(item => item.Element);
        return oldestFirst
            ? files.OrderBy(file => file.LastWriteTimeUtc).ToArray()
            : files.OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
    }
}
