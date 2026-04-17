using EmniProgress.Backends;
using EmniProgress.Backends.KDE;
using EmniProgress.Factory;

namespace DcBootstrapper.Utils;

using System.Collections.Concurrent;

public class Downloader(string url, string filePath, bool isMultithreaded = false)
{
    private readonly ConcurrentDictionary<int, long> _chunkProgress = new();

    private long BytesDownloaded => _chunkProgress.Values.Sum();
    public long TotalBytes { get; private set; }

    public async Task DownloadFileMultithreaded(int taskCount = 4)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DcBootstrapper");
        
        await using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
            fs.SetLength(TotalBytes);

        await using var proggers = (CompositeProgressBackend)EmniFactory.Create();
        var kde = proggers.GetBackend<KdeProgressBackend>();
        await proggers.StartAsync("Downloading", Path.GetFileName(filePath), $"Discord {ConfigManager.CurrentConfig?.ProperBranch} Bootstrapper {Updater.GetCurrentTag()}", "download");
        if (kde != null)
        {
            await kde.UpdateDescriptionFieldAsync(1, "File", Path.GetFileName(filePath));
        }
        
        var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        TotalBytes = response.Content.Headers.ContentLength
                     ?? throw new Exception("Could not determine file size.");

        long chunkSize = TotalBytes / taskCount;

        var tasks = Enumerable.Range(0, taskCount).Select(i =>
        {
            _chunkProgress[i] = 0;
            long start = i * chunkSize;
            long end   = i == taskCount - 1 ? TotalBytes - 1 : start + chunkSize - 1;
            return Task.Run(() => DownloadChunkAsync(start, end, i));
        }).ToList();
        
        KeyValuePair<long, DateTime> oldProgress = new(0, DateTime.UtcNow);

        while (!tasks.All(t => t.IsCompleted))
        {
            var currentBytes = BytesDownloaded;
            var now = DateTime.UtcNow;
            var elapsedSeconds = (now - oldProgress.Value).TotalSeconds;
            var bytesDelta = currentBytes - oldProgress.Key;
            var currentSpeed = elapsedSeconds > 0 ? bytesDelta / elapsedSeconds : 0;

            oldProgress = new KeyValuePair<long, DateTime>(currentBytes, now);

            if (kde != null)
            {
                await kde.UpdateAmountAsync((ulong)TotalBytes, (ulong)currentBytes);
                await kde.UpdateSpeedAsync((ulong)Math.Max(0, currentSpeed));
                await kde.UpdateAsync( new Dictionary<string, object>()
                {
                    { "infoMessage", "File:  " + Path.GetFileName(filePath) },
                });
            }
            
            
            double percent = TotalBytes > 0 ? (currentBytes * 100.0 / TotalBytes) : 0;
            await proggers.UpdateAsync((float)percent, $"Downloading " + Path.GetFileName(filePath));
            
            await Task.Delay(50);
        }
        
        await Task.WhenAll(tasks);
        await proggers.CancelAsync("Download complete.");
        Console.WriteLine($"\r[*] Download complete.                    ");
    }

    private async Task DownloadChunkAsync(long start, long end, int id)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DcBootstrapper");

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var stream     = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Write);
        fileStream.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            _chunkProgress[id] += bytesRead;
        }
    }
}