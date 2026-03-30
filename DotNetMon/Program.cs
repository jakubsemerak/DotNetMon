using System.Diagnostics;
using System.CommandLine;
using System.Runtime.InteropServices;

var shouldKill = true;
long limitInGb = 2;
var checkDelayInMs = 10000;

var shouldKillOption = new Option<bool>(
    name: "--shouldKill",
    description: "Whether to kill the process if it exceeds the memory limit",
    getDefaultValue: () => shouldKill);

var limitInGbOption = new Option<long>(
    name: "--limitInGb",
    description: "Memory limit in GB",
    getDefaultValue: () => limitInGb);

var checkDelayInMsOption = new Option<int>(
    name: "--checkDelayInMs",
    description: "Delay between checks in milliseconds",
    getDefaultValue: () => checkDelayInMs);

var rootCommand = new RootCommand("Monitor dotnet processes and notify when memory limit is exceeded.")
{
    shouldKillOption,
    limitInGbOption,
    checkDelayInMsOption,
};

rootCommand.SetHandler((shouldKillArg, limitInGbArg, checkDelayInMsArg) =>
{
    shouldKill = shouldKillArg;
    limitInGb = limitInGbArg;
    checkDelayInMs = checkDelayInMsArg;
}, shouldKillOption, limitInGbOption, checkDelayInMsOption);

await rootCommand.InvokeAsync(args);

// if --h do not execute the loop below and just print help
if (args.Contains("--help") || args.Contains("-h"))
{
    return;
}

HashSet<int> exceededProcesses = [];

Console.WriteLine("Monitoring JetBrains Roslyn Worker processes...");
while (true)
{
    var dotnetProcesses = Process.GetProcessesByName("dotnet");
    foreach (var process in dotnetProcesses)
    {
        var memoryUsage = GetVirtualMemoryBytes(process);
        var calculatedLimit = limitInGb * 1024 * 1024 * 1024;

        if (memoryUsage > calculatedLimit && IsJetBrainsRoslynWorker(process))
        {
            if (exceededProcesses.Contains(process.Id))
            {
                continue;
            }

            ShowNotification(process.Id, shouldKill, limitInGb, memoryUsage);

            if (shouldKill)
            {
                process.Kill();
                continue;
            }

            exceededProcesses.Add(process.Id);
        }
        else
        {
            exceededProcesses.Remove(process.Id);
        }
    }

    Thread.Sleep(checkDelayInMs);
}


static long GetVirtualMemoryBytes(Process process)
{
    try
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Get resident memory size (RSS column in KB) using ps
            // RSS represents actual physical memory used by the process
            var startInfo = new ProcessStartInfo("ps")
            {
                Arguments = $"-p {process.Id} -o rss=",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var psProcess = Process.Start(startInfo);
            var output = psProcess?.StandardOutput.ReadToEnd().Trim();
            psProcess?.WaitForExit();

            // ps returns RSS in kilobytes on macOS
            if (long.TryParse(output, out var rssInKb))
            {
                return rssInKb * 1024; // Convert to bytes
            }
        }
    }
    catch
    {
        // Fallback to WorkingSet64 if ps fails
    }

    return process.WorkingSet64;
}
static bool IsJetBrainsRoslynWorker(Process process)
{
    try
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // On macOS, use 'ps' command to get command line
            var startInfo = new ProcessStartInfo("ps")
            {
                Arguments = $"-p {process.Id} -o command=",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var psProcess = Process.Start(startInfo);
            var output = psProcess?.StandardOutput.ReadToEnd();
            psProcess?.WaitForExit();
            return output?.Contains("JetBrains.Roslyn.Worker") ?? false;
        }
    }
    catch
    {
        // Ignore errors (process might have exited)
    }

    return false;
}


static void ShowNotification(int processId, bool shouldKill, long limitInGb, long memoryUsage = 0)
{
    string title;
    string message;

    if (processId == 0)
    {
        title = "DotNetMon is running";
        message = $"Monitoring JetBrains Roslyn Worker processes. Limit: {limitInGb} GB. {(shouldKill ? "Offending processes will be terminated." : "Processes will NOT be killed.")}";
    }
    else
    {
        var memoryUsageInMb = memoryUsage / (1024.0 * 1024.0);
        var memoryUsageInGb = memoryUsage / (1024.0 * 1024.0 * 1024.0);
        
        // Show MB if less than 1 GB, otherwise show GB
        string memoryDisplay = memoryUsageInGb >= 1.0 
            ? $"{memoryUsageInGb:F2} GB" 
            : $"{memoryUsageInMb:F0} MB";
        
        title = "Roslyn Worker memory limit exceeded";
        message = $"Process {processId} used {memoryDisplay} (limit: {limitInGb} GB).{(shouldKill ? " Terminating..." : " Not terminating.")}";
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        // Use osascript to show native macOS notification. Note: Do not wrap the -e script in single quotes when UseShellExecute=false
        try
        {
            string EscapeAppleScript(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var escapedMsg = EscapeAppleScript(message);
            var escapedTitle = EscapeAppleScript(title);
            var startInfo = new ProcessStartInfo("/usr/bin/osascript")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            // Important: pass -e and the whole AppleScript as a single argument to avoid it being split into multiple tokens
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add($"display notification \"{escapedMsg}\" with title \"{escapedTitle}\" sound name \"Basso\"");
            using var p = Process.Start(startInfo);
            bool needFallback = false;
            if (p == null)
            {
                needFallback = true;
            }
            else
            {
                p.WaitForExit(3000);
                if (p.ExitCode != 0)
                {
                    var err = p.StandardError.ReadToEnd();
                    Console.WriteLine($"Failed to show notification (exit {p.ExitCode}): {err}");
                    needFallback = true;
                }
            }

            // Fallback to a short dialog if notification fails (more visible)
            if (needFallback)
            {
                var fallback = new ProcessStartInfo("/usr/bin/osascript")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                fallback.ArgumentList.Add("-e");
                fallback.ArgumentList.Add($"display dialog \"{escapedMsg}\" with title \"{escapedTitle}\" giving up after 5");
                Process.Start(fallback);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to show notification: {ex.Message}");
        }
    }
    
    // Always log to console as well
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ALERT: {title}: {message}");
}