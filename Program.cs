// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
﻿using Avalonia;
using System;
using System.IO;
using System.Threading.Tasks;
using Velopack;

namespace dir2site;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "dir2site-crash.txt"),
                $"[{DateTime.Now}] Unhandled exception (terminating={e.IsTerminating}):\n{e.ExceptionObject}\n");
        }
        catch { }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "dir2site-crash.txt"),
                $"[{DateTime.Now}] Unobserved task exception:\n{e.Exception}\n\n");
            e.SetObserved();
        }
        catch { }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}