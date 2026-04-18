using Execor.Inference.Services;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Execor.UI.Views;

public partial class DatabasePage : Page
{
    private readonly DatabaseSchemaService _dbService = new();
    private readonly Action<string?, string?> _onSchemaGenerated;
    private string? _schemaBuffer = null;
    private string? _connectionStringBuffer = null;

    public DatabasePage(Action<string?, string?> onSchemaGenerated)
    {
        InitializeComponent();
        _onSchemaGenerated = onSchemaGenerated;
    }

    private void AuthToggle_Changed(object sender, RoutedEventArgs e)
    {
        CredentialsPanel.Visibility = WindowsAuthToggle.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        ConnectBtn.IsEnabled = false;
        StatusText.Text = "Connecting and extracting schema...";

        // 1. FIXED: Extract all UI values into local string variables on the UI thread first.
        string server = ServerInput.Text.Trim();
        string dbName = DatabaseInput.Text.Trim();
        string username = UsernameInput.Text.Trim();
        string password = PasswordInput.Password;
        bool useWindowsAuth = WindowsAuthToggle.IsChecked == true;

        try
        {
            string connectionString = _dbService.BuildConnectionString(
                server,
                dbName,
                username,
                password,
                useWindowsAuth
            );

            _connectionStringBuffer = connectionString;

            // 2. Pass the local string variables into the background thread. 
            string schemaMd = await Task.Run(() => _dbService.ExtractSchemaToMarkdownAsync(connectionString, dbName));

            StatusText.Text = $"✅ Schema successfully extracted! You can now ask the AI to write queries for '{dbName}'.";

            // Pass the schema back to the main chat window
            _onSchemaGenerated?.Invoke(schemaMd, _connectionStringBuffer);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"❌ Error: {ex.Message}";
        }
        finally
        {
            ConnectBtn.IsEnabled = true;
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        _onSchemaGenerated?.Invoke(_schemaBuffer, _connectionStringBuffer);
    }
}