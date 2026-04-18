using Execor.Core;
using Execor.Inference.Services;
using Execor.UI.Services;
using Execor.UI.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using System.Windows;

namespace Execor.UI;

public partial class App : Application
{
    public static ServiceProvider? ServiceProvider { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ==========================================
        // CLI ARGUMENT INTERCEPTION & SELF-INSTALLER
        // ==========================================
        if (e.Args.Length > 0)
        {
            string command = e.Args[0].ToLowerInvariant();

            if (command == "install")
            {
                InstallExecorGlobally();
                Application.Current.Shutdown();
                return;
            }
            else if (command == "run")
            {
                // Let the application continue to launch the UI normally
            }
            else if (command == "--help" || command == "-h")
            {
                MessageBox.Show("Usage: \n  execor run    - Launches the Execor UI\n  execor install - Installs Execor globally to your system PATH", "Execor CLI");
                Application.Current.Shutdown();
                return;
            }
            else
            {
                MessageBox.Show($"Unknown command: {command}\nUse 'execor run' to start the application.", "Execor CLI Error");
                Application.Current.Shutdown();
                return;
            }
        }
        // ==========================================

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

    private void InstallExecorGlobally()
    {
        try
        {
            // 1. Get current exe location and target directory
            string? currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExe)) throw new Exception("Could not locate running executable.");

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string installDir = Path.Combine(userProfile, ".execor", "bin");
            string targetExe = Path.Combine(installDir, "execor.exe");

            // 2. Create directory and copy the single-file executable
            if (!Directory.Exists(installDir))
            {
                Directory.CreateDirectory(installDir);
            }
            File.Copy(currentExe, targetExe, overwrite: true);

            // 3. Inject the path into the Windows Environment Variables (User Scope)
            string currentPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            if (!currentPath.Contains(installDir, StringComparison.OrdinalIgnoreCase))
            {
                string newPath = currentPath + (currentPath.EndsWith(";") ? "" : ";") + installDir;
                Environment.SetEnvironmentVariable("Path", newPath, EnvironmentVariableTarget.User);
            }

            MessageBox.Show(
                "✅ Execor successfully installed!\n\n" +
                $"The application was copied to:\n{installDir}\n\n" +
                "You can now close this window, open a fresh terminal anywhere on your PC, and type 'execor run'.",
                "Installation Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Installation failed: {ex.Message}", "Installation Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var mainWindow = ServiceProvider?.GetService<MainWindow>();
        mainWindow?.Close();
        base.OnExit(e);
    }
}