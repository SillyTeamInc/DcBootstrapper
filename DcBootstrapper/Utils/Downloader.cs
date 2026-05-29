using EmniProgress.Backends;
using EmniProgress.Backends.KDE;
using EmniProgress.Factory;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace DcBootstrapper.Utils;

public class Downloader(string url, string filePath, bool isMultithreaded = false)
{
    private static readonly HttpClient Client = new();

    private readonly ConcurrentDictionary<int, long> _chunkProgress = new();

    private long BytesDownloaded => _chunkProgress.Values.Sum();
    public long TotalBytes { get; private set; }

    public async Task DownloadFileMultithreaded(int taskCount = 2)
    {
        var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
        headRequest.Headers.UserAgent.ParseAdd("DcBootstrapper");
        var headResponse = await Client.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead);
        headResponse.EnsureSuccessStatusCode();

        TotalBytes = headResponse.Content.Headers.ContentLength
                     ?? throw new Exception("Could not determine file size.");

        // scoping it...
        {
            await using var prealloc = new FileStream(
                filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            prealloc.SetLength(TotalBytes);
        }

        await using var proggers = (CompositeProgressBackend)EmniFactory.Create();
        var kde = proggers.GetBackend<KdeProgressBackend>();
        await proggers.StartAsync(
            "Downloading",
            Path.GetFileName(filePath),
            $"Discord {ConfigManager.CurrentConfig?.ProperBranch} Bootstrapper {Updater.GetCurrentTag()}",
            "download");

        if (kde != null)
            await kde.UpdateDescriptionFieldAsync(1, "File", Path.GetFileName(filePath));

        long chunkSize = TotalBytes / taskCount;
        Console.WriteLine(
            $"[*] Starting download with {taskCount} threads, chunk size: {Bootstrapper.FormatBytes(chunkSize)}");

        var tasks = Enumerable.Range(0, taskCount).Select(i =>
        {
            _chunkProgress[i] = 0;
            long start = i * chunkSize;
            long end = i == taskCount - 1 ? TotalBytes - 1 : start + chunkSize - 1;
            return DownloadChunkAsync(start, end, i);
        }).ToList();

        Console.WriteLine("[*] Downloading...");

        var speedSamples = new Queue<KeyValuePair<DateTime, long>>();

        while (!tasks.All(t => t.IsCompleted))
        {
            var currentBytes = BytesDownloaded;
            var now = DateTime.UtcNow;
            speedSamples.Enqueue(new KeyValuePair<DateTime, long>(now, currentBytes));

            while (speedSamples.Count > 1 && (now - speedSamples.Peek().Key).TotalSeconds > 1)
                speedSamples.Dequeue();

            var oldestSample = speedSamples.Peek();
            var elapsedSeconds = (now - oldestSample.Key).TotalSeconds;
            var bytesDelta = currentBytes - oldestSample.Value;
            var currentSpeed = elapsedSeconds > 0 ? bytesDelta / elapsedSeconds : 0;

            if (kde != null)
            {
                await kde.UpdateAmountAsync((ulong)TotalBytes, (ulong)currentBytes);
                await kde.UpdateSpeedAsync((ulong)Math.Max(0, currentSpeed));
                await kde.UpdateAsync(new Dictionary<string, object>
                {
                    { "infoMessage", "File:  " + Path.GetFileName(filePath) }
                });
            }

            double percent = TotalBytes > 0 ? (currentBytes * 100.0 / TotalBytes) : 0;
            await proggers.UpdateAsync((float)percent,
                $"Downloading - {Bootstrapper.FormatBytes((long)currentSpeed)}/s");

            await Task.Delay(50);
        }

        await Task.WhenAll(tasks);

        await proggers.UpdateAsync(100, "Downloaded " + Path.GetFileName(filePath));
        await proggers.CancelAsync("Download complete.");
        Console.WriteLine("\r[*] Download complete.                    ");
    }

    private async Task DownloadChunkAsync(long start, long end, int id)
    {
        const int bufferSize = 81920;
        const int channelCapacity = 64;
    
        // dumb and stinky i hate
        await using var fileStream = new FileStream(
            filePath, FileMode.Open, FileAccess.Write, FileShare.Write, bufferSize, FileOptions.Asynchronous);
        fileStream.Seek(start, SeekOrigin.Begin);

        var channel = Channel.CreateBounded<(byte[] data, int count)>(new BoundedChannelOptions(channelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        var writeTask = WriteChunkAsync(channel.Reader, fileStream, id);

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Discord-Updater/1"); // lol
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);

            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[bufferSize];
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
            {
                var copy = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, copy, 0, bytesRead);
                await channel.Writer.WriteAsync((copy, bytesRead));
            }
        }
        finally
        {
            channel.Writer.Complete();
        }

        await writeTask;
    }

    private async Task WriteChunkAsync(ChannelReader<(byte[] data, int count)> reader, FileStream fileStream, int id)
    {
        await foreach (var (data, count) in reader.ReadAllAsync())
        {
            await fileStream.WriteAsync(data.AsMemory(0, count));
            _chunkProgress[id] += count;
        }
    }
}