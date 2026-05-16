using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Markup.Xaml;
using dir2site.ViewModels;
using dir2site.Views;
using WebViewControl;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;

namespace dir2site;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            
            if(RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                WebView.Settings.LogFile = "cef.log";
                
                WebView.Settings.AddCommandLineSwitch("use-mock-keychain", null);
                WebView.Settings.AddCommandLineSwitch("password-store", "basic");
                WebView.Settings.AddCommandLineSwitch("allow-file-access-from-files", null);
                WebView.Settings.AddCommandLineSwitch("disable-web-security", null);
            }

            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = new MainWindow
            {
            };
            
            desktop.ShutdownRequested += (s, e) =>
            {
                CefRuntime.Shutdown();
                ((ImageViewModel?)desktop.MainWindow?.DataContext)?.SaveOverlaysCommand.Execute(null);
            };
            
#if DEBUG
            this.AttachDevTools();
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }
    
    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach(var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}