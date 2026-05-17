using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using dir2site.ViewModels;
using dir2site.Views;
using WebViewControl;
using Xilium.CefGlue;

namespace dir2site;

public partial class App : Application
{
    public override void Initialize()
    {
        // Before any WebView is instantiated (e.g. App.axaml.cs or Program.cs)
        WebView.Settings.AddCommandLineSwitch("use-mock-keychain", null);        // macOS: suppress keychain popup
        WebView.Settings.AddCommandLineSwitch("password-store", "basic");        // Linux: skip kwallet/libsecret
        WebView.Settings.AddCommandLineSwitch("allow-file-access-from-files", null); // allow local file:// cross-origin
        WebView.Settings.AddCommandLineSwitch("disable-web-security", null);     // broader: disable CORS for local preview
        WebView.Settings.AddCommandLineSwitch("disable-extensions", null);
        WebView.Settings.AddCommandLineSwitch("disable-sync", null);
        WebView.Settings.AddCommandLineSwitch("disable-background-networking", null);
        
        WebView.Settings.LogFile = "cef.log";
        WebView.Settings.CachePath = Path.Combine(Path.GetTempPath(), "dir2site-cache");

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
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