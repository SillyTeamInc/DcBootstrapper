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

        while (!tasks.All(t => t.IsCompleted))
        {
            Console.Write($"\r[*] Downloading... {BytesDownloaded * 100 / TotalBytes}%   ");
            await Task.Delay(100);
        }

        await Task.WhenAll(tasks);
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