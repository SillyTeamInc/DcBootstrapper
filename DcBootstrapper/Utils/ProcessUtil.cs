using System.Diagnostics;

namespace DcBootstrapper.Utils;

public class ProcessUtil
{
    public static int RunProcess(string fileName, string args = "", string workingDirectory = "", bool waitForExit = true,
        bool throwOnError = true, bool notify = true, Dictionary<string, string>? envVars = null)
    {
        if (string.IsNullOrEmpty(workingDirectory))
        {
            try { workingDirectory = Directory.GetCurrentDirectory(); }
            catch { workingDirectory = ""; }
        }
        if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
            workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (envVars != null)
            foreach (var (key, value) in envVars)
                psi.Environment[key] = value;

        if (notify) Console.WriteLine($"[i] Running: {fileName} {args}");
        using var proc = Process.Start(psi);
        if (proc == null) throw new Exception($"Failed to start: {fileName}");
        if (waitForExit)
        {
            proc.WaitForExit();
            if (throwOnError && proc.ExitCode != 0) throw new Exception($"{fileName} failed: {proc.ExitCode}");
            return proc.ExitCode;
        }
        return 0;
    }
    
}