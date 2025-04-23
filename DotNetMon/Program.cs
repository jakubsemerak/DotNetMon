using System.Diagnostics;
using Microsoft.Toolkit.Uwp.Notifications;
using System.CommandLine;

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

while (true)
{
    var dotnetProcesses = Process.GetProcessesByName("dotnet");
    foreach (var process in dotnetProcesses)
    {
        var memoryUsage = process.PrivateMemorySize64;
        var calculatedLimit = limitInGb * 1024 * 1024 * 1024;

        if (memoryUsage > calculatedLimit && IsJetBrainsRoslynWorker(process))
        {
            if (exceededProcesses.Contains(process.Id))
            {
                continue;
            }

            ShowNotification(process.Id, shouldKill);

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


static bool IsJetBrainsRoslynWorker(Process process)
{
    var startInfo = new ProcessStartInfo("wmic")
    {
        Arguments = $"process where processid=\"{process.Id}\" get CommandLine",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    using var wmicProcess = Process.Start(startInfo);
    var output = wmicProcess?.StandardOutput.ReadToEnd();
    return output?.Contains("JetBrains.Roslyn.Worker.exe") ?? false;
}


static void ShowNotification(int processId, bool shouldKill)
{
    var text =
        $"Roslyn Worker dotnet process \"{processId}\" has exceeded the memory limit.{(shouldKill ? " Killing..." : "")}";

    new ToastContentBuilder()
        .AddText("Memory Limit Exceeded")
        .AddText(text)
        .AddAudio(new ToastAudio
        {
            Silent = false,
            Loop = false,
            Src = new Uri("ms-winsoundevent:Notification.Looping.Alarm10")
        })
        .Show();
}