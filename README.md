# DotNetMon
Windows only tool for monitoring dotnet process memory started by Jetbrains Rider JetBrains.Roslyn.Worker.exe.
If the dotnet process exceeds defined memory limit, it gets killed and Rider will start a new one automatically.

Start automatically after start using windows task scheduler.

Written as a workaround for:
RIDER-113736 Process dotnet.exe gradually consumes all available memory
RIDER-121666 JetBrains.Roslyn.Worker.exe high memory consumption
