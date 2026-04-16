using Execor.Core;
using Execor.Inference.Services;
using Execor.UI.Services;
using Execor.UI.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Windows;

namespace Execor.UI;

public partial class App : Application
{
    public static ServiceProvider? ServiceProvider { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Build Configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        // 2. Set up Dependency Injection
        var serviceCollection = new ServiceCollection();

        // Register Configuration and Services
        serviceCollection.AddSingleton<IConfiguration>(configuration);
        serviceCollection.AddSingleton<IModelManager, ModelManager>();
        serviceCollection.AddSingleton<IChatService, LlamaService>();

        // Register the MainWindow itself so DI can provide its dependencies
        serviceCollection.AddSingleton<MainWindow>();

        ServiceProvider = serviceCollection.BuildServiceProvider();

        // 3. Manually resolve and show the MainWindow
        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
        mainWindow.Close();
        base.OnExit(e);
    }
}
