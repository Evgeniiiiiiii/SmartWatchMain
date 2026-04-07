using System;
using Avalonia;
using SmartWatchProj.Cli;
using SmartWatchProj.Services.Devices;

namespace SmartWatchProj
{
    internal sealed class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static int Main(string[] args)
        {
            if (LinuxExternalYoloRunner.TryRunCli(args, out var linuxYoloExitCode))
            {
                return linuxYoloExitCode;
            }

            if (ComSmokeTestRunner.TryRun(args, out var exitCode))
            {
                return exitCode;
            }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
