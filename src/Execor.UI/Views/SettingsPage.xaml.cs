using Execor.Core;
using Microsoft.Win32;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;
using Execor.Models;

namespace Execor.UI.Views;

public partial class SettingsPage : Page
{
    private readonly IModelManager _modelManager;
    private readonly Action<bool> _onClose;

    public SettingsPage(IModelManager modelManager, List<McpTool> mcpTools, Action<bool> onClose)
    {
        InitializeComponent();
        _modelManager = modelManager;
        _onClose = onClose;

        ModelsPathInput.Text = _modelManager.GetModelsPath();
        ContextSizeInput.Text = "4096"; // Can be expanded to read from config later

        McpToolsList.ItemsSource = mcpTools;
    }

    private void BrowseFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Models Directory"
        };

        if (dialog.ShowDialog() == true)
        {
            ModelsPathInput.Text = dialog.FolderName;
        }
    }

    private void SaveSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var newPath = ModelsPathInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(newPath) || !Directory.Exists(newPath))
        {
            MessageBox.Show("Please select a valid existing directory.", "Invalid Path", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Update runtime manager
        _modelManager.UpdateModelsPath(newPath);

        // Persist to appsettings.json
        UpdateAppSettings("ExecorSettings", "ModelsPath", newPath);

        if (int.TryParse(ContextSizeInput.Text, out int contextSize))
        {
            UpdateAppSettings("ExecorSettings", "ContextSize", contextSize);
        }

        _onClose?.Invoke(true); // true = settings were changed
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        _onClose?.Invoke(false); // false = no changes saved
    }

    private void UpdateAppSettings(string section, string key, object value)
    {
        try
        {
            var filePath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(filePath)) return;

            var jsonString = File.ReadAllText(filePath);
            var jsonNode = JsonNode.Parse(jsonString);

            if (jsonNode != null && jsonNode[section] != null)
            {
                jsonNode[section][key] = JsonValue.Create(value);

                // FIXED: Changed .ToString(...) to .ToJsonString(...)
                File.WriteAllText(filePath, jsonNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch { /* Ignore or log file lock/parsing errors */ }
    }
}