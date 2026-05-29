using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WpfMusicEditor.Forms;
using WpfMusicEditor.Forms.Services;
using WpfMusicEditor.Forms.UI.Views;
using WpfMusicEditor.Forms.ViewModels;
using WpfMusicEditor.Main.Audio;

namespace WpfMusicEditor;

public class App : Application
{
    private IHost? _host;

    public static IServiceProvider? ServiceProvider { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                ConfigureServices(services);
            })
            .Build();

        ServiceProvider = _host.Services;
        AppServices.Current = _host.Services;

        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }

    private void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAudioEditor, NAudioEditor>();
        services.AddSingleton<AudioPlayer>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
    }
}
