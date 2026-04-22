using Execor.Core;
using Execor.Inference.Services;
using Execor.Models;
using Execor.UI.Services;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using Markdig;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Execor.UI.Views;

public partial class MainWindow : Window
{
    private bool _isGenerating = false;
    private CancellationTokenSource? _cts;
    private readonly ChatMemoryService _chatMemory = new();
    private List<ChatSessionModel> _chatSessions = new();
    private ChatSessionModel? _currentChat;
    private readonly SystemMonitorService _systemMonitor;
    private readonly InferenceMetricsService _metrics;
    private readonly GpuMonitorService _gpuMonitor;
    private readonly WorkspaceIntelligenceService _workspaceService = new();
    private CancellationTokenSource? _dashboardCts;
    private readonly IModelManager _modelManager;
    private readonly IChatService _chatService;
    private readonly McpClientService _mcpService = new();
    private bool _isCodeReviewMode = false;
    private string _reviewBranchName = "";
    private string _reviewRepoPath = "";
    private string _activeDatabaseSchema = "";
    private string _activeConnectionString = "";
    private string? _currentImagePath = null;
    private enum BlockType { Text, Code, Think }

    public MainWindow(IModelManager modelManager, IChatService chatService)
    {
        InitializeComponent();

        _modelManager = modelManager;
        _chatService = chatService;

        // Wire up all events (mirroring original Avalonia wiring)
        SendButton.Click += SendButton_Click;
        RefreshModelsButton.Click += (_, _) => LoadModels();
        NewChatButton.Click += (_, _) => CreateNewChat();
        StopButton.Click += (_, _) => _cts?.Cancel();

        PromptInput.KeyDown += PromptInput_KeyDown;
        PromptInput.TextChanged += PromptInput_TextChanged;
        SearchChatsInput.KeyUp += (_, _) => RefreshConversationSidebar();

        _systemMonitor = new SystemMonitorService();
        _metrics = new InferenceMetricsService();
        _gpuMonitor = new GpuMonitorService();

        // Input border focus visual
        PromptInput.GotFocus += (_, _) => InputBorder.BorderBrush =
            (SolidColorBrush)FindResource("AccentBlue");
        PromptInput.LostFocus += (_, _) => InputBorder.BorderBrush =
            (SolidColorBrush)FindResource("BorderColor");

        StartDashboardMonitoring();
        LoadModels();
        LoadChatSessions();
        InitializeDefaultMcpServers();
    }

    // ────────────────────────────────────────────────────────
    //  Window chrome (custom titlebar — WindowStyle=None)
    // ────────────────────────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            ToggleMaximize();
        else
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    // ────────────────────────────────────────────────────────
    //  Model loading
    // ────────────────────────────────────────────────────────

    private async void LoadModels()
    {
        try
        {
            var models = _modelManager.GetInstalledModels();

            ModelSelector.ItemsSource = models;
            ModelSelector.DisplayMemberPath = "Name";

            var activeModel = _modelManager.GetActiveModel();

            if (activeModel != null)
            {
                ModelSelector.SelectedItem = models.FirstOrDefault(m => m.Name == activeModel.Name);
            }
            else if (models.Any())
            {
                ModelSelector.SelectedIndex = 0;
            }
        }
        catch
        {
            AddMessageBubble("Unable to load models from backend.", false);
        }
    }

    private void LoadAttachedImage(string imagePath)
    {
        _currentImagePath = imagePath;
        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(_currentImagePath);
        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // Prevents file locking
        bitmap.EndInit();

        AttachedImagePreview.Source = bitmap;
        ImagePreviewContainer.Visibility = Visibility.Visible;
    }

    // Replace your current AttachButton_Click with this cleaner version:
    private void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select an Image",
            Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp"
        };

        if (dialog.ShowDialog() == true)
        {
            LoadAttachedImage(dialog.FileName);
        }
    }

    private void PromptInput_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void PromptInput_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0)
            {
                string firstFile = files[0];
                string ext = System.IO.Path.GetExtension(firstFile).ToLower();

                if (ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp" || ext == ".webp")
                {
                    LoadAttachedImage(firstFile);
                }
            }
        }
    }

    private void RemoveImageBtn_Click(object sender, RoutedEventArgs e)
    {
        _currentImagePath = null;
        AttachedImagePreview.Source = null;
        ImagePreviewContainer.Visibility = Visibility.Collapsed;
    }

    // ────────────────────────────────────────────────────────
    //  Send / Generate
    // ────────────────────────────────────────────────────────

    private async void SendButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isGenerating)
            return;

        var prompt = PromptInput.Text;
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        // ==========================================
        // 1. TOOL ROUTER (Slash Command Interception)
        // ==========================================
        if (prompt.StartsWith("/"))
        {
            if (prompt.StartsWith("/clear"))
            {
                CreateNewChat();
                PromptInput.Text = "";
                return; // Stop execution, don't send to LLM
            }

            if (prompt.StartsWith("/sys"))
            {
                // FIXED: Explicitly tell it NOT to write code, just analyze the provided numbers.
                prompt = $"Here are my current PC stats:\n" +
                         $"CPU: {CpuUsageText.Text}, RAM: {RamUsageText.Text}, GPU: {GpuUsageText.Text}, VRAM: {GpuMemoryText.Text}.\n" +
                         $"Analyze these numbers and tell me if my PC is struggling. Do NOT write code to fetch stats, just answer based on these numbers.";
            }

            if (prompt.StartsWith("/web"))
            {
                // Force web toggle on, and remove the "/web " prefix from the search string
                WebSearchToggle.IsChecked = true;
                prompt = prompt.Replace("/web", "").Trim();
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    PromptInput.Text = "";
                    return;
                }
            }

            if (prompt.StartsWith("/codereview"))
            {
                var repoPath = prompt.Replace("/codereview", "").Trim();

                if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
                {
                    AddMessageBubble("Please provide a valid repository path. Example: /codereview C:\\Projects\\MyRepo", isUser: false);
                    PromptInput.Text = "";
                    ResetSendState();
                    return;
                }

                PromptInput.Text = "";
                _ = RunCodeReviewAsync(repoPath); // Fire and forget the chunking loop
                return; // Stop normal chat execution
            }

            if (prompt.StartsWith("/db"))
            {
                var dbQuery = prompt.Replace("/db", "").Trim();

                if (string.IsNullOrWhiteSpace(_activeConnectionString) || string.IsNullOrWhiteSpace(_activeDatabaseSchema))
                {
                    AddMessageBubble("No database connected. Please click 'Connect Database' first.", isUser: false);
                    PromptInput.Text = "";
                    ResetSendState();
                    return;
                }

                PromptInput.Text = "";
                _ = RunDatabaseChatAsync(dbQuery);
                return; // Stop normal chat execution
            }

            if (prompt.StartsWith("/exit"))
            {
                Close();
                return;
            }

            string finalPrompt = PromptInput.Text.Trim();

            if (finalPrompt.StartsWith("/workspace", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = finalPrompt.Split(' ', 2);
                string path = parts.Length > 1 ? parts[1].Trim() : "";

                if (path.ToLower() == "clear")
                {
                    _workspaceService.Clear();
                    // TODO: Append a system message to your chat UI indicating "Workspace Cleared"
                    PromptInput.Clear();
                    return;
                }

                if (!Directory.Exists(path))
                {
                    // TODO: Append system message to UI indicating "Directory not found."
                    return;
                }

                // Safely locate the models directory regardless of Debug or Release mode
                string currentDir = AppDomain.CurrentDomain.BaseDirectory;
                string modelsDir = System.IO.Path.Combine(currentDir, "models");

                // Walk up the directory tree to find the root 'models' folder
                while (!System.IO.Directory.Exists(modelsDir) && System.IO.Directory.GetParent(currentDir) != null)
                {
                    currentDir = System.IO.Directory.GetParent(currentDir)!.FullName;
                    modelsDir = System.IO.Path.Combine(currentDir, "models");
                }

                if (!System.IO.Directory.Exists(modelsDir))
                {
                    // Optionally append a message to the UI here
                    return;
                }

                string? embedModel = Directory.GetFiles(modelsDir, "*embed*.gguf").FirstOrDefault();
                if (embedModel == null)
                {
                    // TODO: Append system message: "Missing embedding model (e.g., mxbai-embed-large.gguf) in models folder."
                    return;
                }

                // TODO: Append system message: $"Indexing {path} using {System.IO.Path.GetFileName(embedModel)}..."

                PromptInput.Clear();

                // Setup Progress tracking UI
                WorkspaceProgressText.Visibility = Visibility.Visible;
                WorkspaceProgressText.Text = "Initializing embedding model...";

                // The Progress class automatically marshals updates back to the WPF UI thread
                var progress = new Progress<string>(status =>
                {
                    WorkspaceProgressText.Text = status;
                });

                // Run indexing asynchronously
                _ = Task.Run(async () =>
                {
                    await _workspaceService.InitializeAsync(embedModel);
                    int chunks = await _workspaceService.IndexDirectoryAsync(path, progress);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        // Temporarily turn the text green to indicate success
                        WorkspaceProgressText.Foreground = new System.Windows.Media.SolidColorBrush(
                            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4CAF50"));

                        WorkspaceProgressText.Text = $"✓ Workspace indexed! {chunks} chunks stored in RAM.";

                        // Hide the progress text automatically after 4 seconds
                        Task.Delay(4000).ContinueWith(_ =>
                            Dispatcher.InvokeAsync(() =>
                            {
                                WorkspaceProgressText.Visibility = Visibility.Collapsed;
                                // Reset color back to default AccentBlue for the next run
                                WorkspaceProgressText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBlue");
                            })
                        );
                    });
                });

                return; // Stop normal execution; this was a system command.
            }

            if (prompt.StartsWith("/clear"))
            {
                CreateNewChat();
                PromptInput.Text = "";
                return; // Stop execution, don't send to LLM
            }

            // ADD THIS NEW BLOCK:
            if (prompt.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
            {
                // Show the user's message
                AddMessageBubble(prompt, isUser: true);
                PromptInput.Text = "";

                // Format the available commands into a nice Markdown list
                string helpText = "**Available Execor Commands:**\n\n";
                foreach (var cmd in _availableCommands)
                {
                    var parts = cmd.Split('-');
                    if (parts.Length == 2)
                    {
                        helpText += $"- `{parts[0].Trim()}` : {parts[1].Trim()}\n";
                    }
                }

                // Show the AI's response with the formatted list
                AddMessageBubble(helpText, isUser: false);
                ResetSendState();
                return; // Stop execution, don't send to LLM
            }

            if (prompt.StartsWith("/scaffold", StringComparison.OrdinalIgnoreCase))
            {
                var scaffoldQuery = prompt.Substring(9).Trim(); // Remove "/scaffold"

                if (string.IsNullOrWhiteSpace(scaffoldQuery))
                {
                    AddMessageBubble("Please provide a scaffolding request. Example: /scaffold Create a Python FastAPI authentication middleware.", isUser: false);
                    PromptInput.Text = "";
                    ResetSendState();
                    return;
                }

                if (!_workspaceService.IsInitialized || string.IsNullOrEmpty(_workspaceService.ActiveWorkspacePath))
                {
                    AddMessageBubble("⚠️ No workspace loaded. Please run `/workspace [path]` first so I know where to write the files.", isUser: false);
                    PromptInput.Text = "";
                    ResetSendState();
                    return;
                }

                PromptInput.Text = "";
                _ = RunScaffoldAsync(scaffoldQuery);
                return; // Stop normal chat execution
            }
        }
        // ==========================================

        var activeTools = SelectRelevantTools(prompt);

        _metrics.Start();

        _isGenerating = true;
        SendButton.IsEnabled = false;
        StopButton.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();

        UpdateChatTitle(prompt);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            ResetSendState();
            return;
        }

        AddMessageBubble(prompt, isUser: true);

        _currentChat!.Messages.Add(new ChatMessageModel
        {
            IsUser = true,
            Text = prompt
        });

        PromptInput.Text = "";

        var assistantBlock = AddMessageBubble("", isUser: false);

        _currentChat!.UpdatedAt = DateTime.Now;

        bool thinking = true;
        bool firstToken = true;

        var thinkingBlock = new TextBlock { FontSize = 15, Foreground = Brushes.White, LineHeight = 22 };
        assistantBlock.Children.Add(thinkingBlock);

        var animTask = Task.Run(async () =>
        {
            int dots = 0;
            while (thinking)
            {
                dots = (dots + 1) % 4;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (firstToken) thinkingBlock.Text = "Thinking" + new string('.', dots);
                });
                await Task.Delay(400);
            }
        });

        try
        {
            // --- NEW: WEB SEARCH LOGIC ---
            string webContext = "";
            if (WebSearchToggle.IsChecked == true)
            {
                await Dispatcher.InvokeAsync(() => TypingStatus.Text = "Searching web...");
                try
                {
                    // Assuming SearchService is implemented as suggested
                    var searchService = new Execor.Inference.Services.SearchService();
                    webContext = await searchService.GetWebContextAsync(prompt);
                }
                catch (Exception ex)
                {
                    webContext = "Search failed: " + ex.Message;
                }
                await Dispatcher.InvokeAsync(() => TypingStatus.Text = "");
            }

            var selectedModel = ModelSelector.SelectedItem as ModelInfo;
            if (selectedModel != null)
            {
                var activeModel = _modelManager.GetActiveModel();
                if (activeModel == null || activeModel.Name != selectedModel.Name)
                {
                    await Task.Run(() =>
                    {
                        _modelManager.SetActiveModel(selectedModel.Name);
                        _chatService.LoadActiveModel();
                    });
                }
            }

            string accumulatedText = "";

            string finalPrompt = prompt;
            if (!string.IsNullOrEmpty(_activeDatabaseSchema))
            {
                // We wrap the prompt to give the AI the database context
                finalPrompt = $"You are an expert Database Administrator. Use the following database schema to answer the user's request. If writing SQL, ensure you use the exact table and column names provided.\n\n" +
                              $"{_activeDatabaseSchema}\n\n" +
                              $"USER REQUEST: {prompt}";
            }

            string? imageToProcess = _currentImagePath; // Capture before clearing UI

            // Clear UI immediately
            _currentImagePath = null;
            await Dispatcher.InvokeAsync(() =>
            {
                ImagePreviewContainer.Visibility = Visibility.Collapsed;
                AttachedImagePreview.Source = null;
            });

            string? ragContext = null;
            if (_workspaceService.IsInitialized && !string.IsNullOrEmpty(_workspaceService.ActiveWorkspacePath))
            {
                // Pull the top 3 most semantically similar code chunks for the given prompt
                ragContext = await _workspaceService.SearchAsync(finalPrompt, topK: 3);
            }

            // Merge Web Search Context (if any) with Local RAG Context
            string combinedContext = "";
            if (!string.IsNullOrEmpty(webContext)) combinedContext += webContext + "\n";
            if (!string.IsNullOrEmpty(ragContext)) combinedContext += ragContext + "\n";

            string? finalContext = string.IsNullOrEmpty(combinedContext) ? null : combinedContext;

            // ADD THIS BEFORE THE LOOP:
            var lastUiUpdate = DateTime.Now;

            await foreach (var chunk in _chatService.StreamChatAsync(finalPrompt, finalContext, imageToProcess, activeTools))
            {
                if (_cts.Token.IsCancellationRequested) break;
                var cleanChunk = CleanToken(chunk);
                if (string.IsNullOrEmpty(cleanChunk)) continue;

                if (firstToken)
                {
                    firstToken = false;
                    thinking = false;
                    await Dispatcher.InvokeAsync(() => assistantBlock.Children.Clear());
                }

                accumulatedText += cleanChunk;

                // File: src/Execor.UI/Views/MainWindow.xaml.cs

                if (accumulatedText.Contains("</tool_call>"))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        accumulatedText,
                        @"<tool_call>\s*(.*?)\s*</tool_call>",
                        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    if (match.Success)
                    {
                        string toolJson = match.Groups[1].Value.Trim();

                        // 1. Sanitize Markdown backticks if the AI accidentally wraps the JSON
                        toolJson = System.Text.RegularExpressions.Regex.Replace(toolJson, @"^```[a-zA-Z]*\s*", "");
                        toolJson = System.Text.RegularExpressions.Regex.Replace(toolJson, @"\s*```$", "");

                        // 2. CRITICAL FIX: Remove the raw XML tag from the UI buffer entirely
                        accumulatedText = accumulatedText.Replace(match.Value, "");

                        try
                        {
                            var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                            var toolPayload = System.Text.Json.JsonSerializer.Deserialize<Execor.Models.McpCallToolRequest>(toolJson, options);

                            if (toolPayload != null)
                            {
                                // 3. Inject a standard Markdown blockquote into the stream so the UI parser renders it naturally
                                accumulatedText += $"\n\n> ⚙️ **Executing MCP Tool:** `{toolPayload.Name}`...\n\n";

                                // Force an immediate UI render so the user sees the loading status
                                string statusText = accumulatedText;
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    SyncStreamingUI(assistantBlock, statusText);
                                    ChatScrollViewer.ScrollToEnd();
                                });

                                // 4. Call the tool securely
                                string toolResult = await _mcpService.CallToolAsync(toolPayload.Name, toolPayload.Arguments);

                                // Add a completion checkmark to the UI
                                accumulatedText += $"> ✅ **Tool execution complete.**\n\n";

                                // 5. Send the retrieved data back to the LLM to finalize its response
                                // 5. Send the retrieved data back to the LLM to finalize its response
                                string followUpPrompt =
                                    $"TOOL RESULT:\n{toolResult}\n\n" +
                                    $"Finalize your response based on this data. " +
                                    $"CRITICAL FORMATTING RULES FOR FILE LISTS:\n" +
                                    $"1. You MUST include EVERY SINGLE ITEM from the tool result. DO NOT skip, summarize, or omit any files or folders.\n" +
                                    $"2. DO NOT invent nesting. Keep the exact flat structure provided by the tool.\n" +
                                    $"3. Format the list inside a ```text markdown block.\n" +
                                    $"4. Replace '[DIR]' with '📁 ' and '[FILE]' with '📄 ' to make it look clean.";

                                // Note: Make sure 'relevantTools' is the variable you created in the previous step
                                await foreach (var followupChunk in _chatService.StreamChatAsync(followUpPrompt, null, null, activeTools))
                                {
                                    accumulatedText += CleanToken(followupChunk);

                                    // Standard 30 FPS render throttle
                                    if ((DateTime.Now - lastUiUpdate).TotalMilliseconds > 33)
                                    {
                                        string textToRender = accumulatedText;
                                        await Dispatcher.InvokeAsync(() =>
                                        {
                                            SyncStreamingUI(assistantBlock, textToRender);
                                            ChatScrollViewer.ScrollToEnd();
                                        }, System.Windows.Threading.DispatcherPriority.Render);
                                        lastUiUpdate = DateTime.Now;
                                    }
                                }
                                break; // Exit the primary streaming loop
                            }
                        }
                        catch (Exception ex)
                        {
                            // If the AI hallucinates bad JSON, display the error neatly in the chat
                            accumulatedText += $"\n\n> ⚠️ **Tool Execution Failed:** {ex.Message}\n\n";
                        }
                    }
                }

                _metrics.AddToken(cleanChunk);

                // 🔥 THE MAGIC SAUCE: Throttle updates to ~30 FPS (every 33ms)
                if ((DateTime.Now - lastUiUpdate).TotalMilliseconds > 33)
                {
                    string textToRender = accumulatedText; // Clone string reference
                    await Dispatcher.InvokeAsync(() =>
                    {
                        SyncStreamingUI(assistantBlock, textToRender);
                        ChatScrollViewer.ScrollToEnd();
                    }, DispatcherPriority.Render); // Use Render priority for smoother painting

                    lastUiUpdate = DateTime.Now;
                }
            }

            // Final flush to catch the last few tokens
            await Dispatcher.InvokeAsync(() =>
            {
                SyncStreamingUI(assistantBlock, accumulatedText, isFinished: true);
                ChatScrollViewer.ScrollToEnd();
            });

            if (_isCodeReviewMode)
            {
                GenerateWordDocument(accumulatedText);
            }
        }
        catch (Exception ex)
        {
            thinking = false;
            await Dispatcher.InvokeAsync(() =>
            {
                assistantBlock.Children.Clear();
                assistantBlock.Children.Add(new TextBlock { Text = $"Error: {ex.Message}", Foreground = Brushes.Red });
            });
        }
        finally
        {
            thinking = false;
            ResetSendState();
        }
    }

    private void ResetSendState()
    {
        _isGenerating = false;
        SendButton.IsEnabled = true;
        StopButton.Visibility = Visibility.Collapsed;
    }

    // ────────────────────────────────────────────────────────
    //  Message bubbles
    // ────────────────────────────────────────────────────────

    private StackPanel AddMessageBubble(string text, bool isUser)
    {
        var contentPanel = new StackPanel { Margin = new Thickness(0) };

        // Pre-fill if loading past messages
        if (!string.IsNullOrEmpty(text))
        {
            SyncStreamingUI(contentPanel, text, isFinished: true);
        }

        var bubble = new Border
        {
            Background = isUser ? (Brush)FindResource("UserBubble") : (Brush)FindResource("BotBubble"),
            CornerRadius = isUser ? new CornerRadius(18, 4, 18, 18) : new CornerRadius(4, 18, 18, 18),
            Padding = new Thickness(14, 10, 14, 10),
            MaxWidth = 720,
            Child = contentPanel
        };

        var dot = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = isUser ? (Brush)FindResource("AccentBlue") : new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock
            {
                Text = isUser ? "U" : "AI",
                FontSize = isUser ? 12 : 9,
                FontWeight = FontWeight.FromOpenTypeWeight(700),
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var row = new Grid { Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Stretch };

        if (isUser)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bubble.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(bubble, 0); Grid.SetColumn(dot, 1);
            dot.Margin = new Thickness(10, 0, 0, 0);
            row.Children.Add(bubble); row.Children.Add(dot);
        }
        else
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bubble.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(dot, 0); Grid.SetColumn(bubble, 1);
            dot.Margin = new Thickness(0, 0, 10, 0);
            row.Children.Add(dot); row.Children.Add(bubble);
        }

        MessagesPanel.Children.Add(row);
        return contentPanel;
    }

    private void SyncStreamingUI(StackPanel panel, string text, bool isFinished = false)
    {
        var parsedBlocks = ParseMarkdown(text);

        while (panel.Children.Count < parsedBlocks.Count)
        {
            var block = parsedBlocks[panel.Children.Count];
            if (block.Type == BlockType.Text)
                panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15, Foreground = Brushes.White, LineHeight = 22 });
            else if (block.Type == BlockType.Code)
                panel.Children.Add(CreateSyntaxHighlightedCodeBox("", block.Language));
            else if (block.Type == BlockType.Think)
                panel.Children.Add(CreateThinkingBox(""));
        }

        // Clean up previous blocks
        for (int i = 0; i < parsedBlocks.Count - 1; i++)
        {
            if (parsedBlocks[i].Type == BlockType.Text)
                ((TextBlock)panel.Children[i]).Text = parsedBlocks[i].Text;
            else if (parsedBlocks[i].Type == BlockType.Code)
            {
                var editor = FindTextEditor(panel.Children[i]);
                if (editor != null) editor.Text = parsedBlocks[i].Text;
            }
            else if (parsedBlocks[i].Type == BlockType.Think)
            {
                var tb = FindThinkingTextBlock(panel.Children[i]);
                if (tb != null) tb.Text = parsedBlocks[i].Text;

                // Auto-collapse previous thoughts when the AI moves on to the final answer
                if (panel.Children[i] is Expander exp) exp.IsExpanded = false;
            }
        }

        // Update the active (last) block
        if (parsedBlocks.Any())
        {
            var lastParsed = parsedBlocks.Last();
            var lastUI = panel.Children.OfType<FrameworkElement>().LastOrDefault(c => c.Name != "OptionsPanel");
            string cursor = isFinished ? "" : "▋";

            if (lastUI is TextBlock tb && lastParsed.Type == BlockType.Text)
            {
                tb.Text = lastParsed.Text + cursor;
            }
            else if (lastUI is Expander exp && lastParsed.Type == BlockType.Think)
            {
                var thinkTb = FindThinkingTextBlock(exp);
                if (thinkTb != null) thinkTb.Text = lastParsed.Text + cursor;
                exp.IsExpanded = true; // Keep expanded while actively typing
            }
            else if (lastUI != null && lastParsed.Type == BlockType.Code)
            {
                var editor = FindTextEditor(lastUI);
                if (editor != null)
                {
                    editor.Text = lastParsed.Text + cursor;
                    editor.ScrollToEnd();
                }

                var badge = FindLanguageBadge(lastUI);
                if (badge != null && !string.IsNullOrWhiteSpace(lastParsed.Language))
                {
                    badge.Text = lastParsed.Language.ToUpperInvariant();
                    if (editor != null) editor.SyntaxHighlighting = GetHighlightingDefinition(lastParsed.Language);
                }
            }
        }
    }

    private TextEditor? FindTextEditor(UIElement element) =>
        (element as Border)?.Child is StackPanel sp ? sp.Children.OfType<TextEditor>().FirstOrDefault() : null;

    private TextBlock? FindLanguageBadge(UIElement element) =>
        ((element as Border)?.Child as StackPanel)?.Children.OfType<Grid>().FirstOrDefault()?.Children.OfType<Border>().FirstOrDefault()?.Child as TextBlock;

    private UIElement CreateSyntaxHighlightedCodeBox(string code, string? explicitLang = null)
    {
        code = code?.Trim() ?? "";

        string lang = string.IsNullOrWhiteSpace(explicitLang)
            ? DetectLanguage(code)
            : explicitLang.Trim();

        // 👉 Use the new mapper here instead of directly calling Instance.GetDefinition
        var highlighting = GetHighlightingDefinition(lang);

        int lineCount = code.Split('\n').Length;
        double estimatedHeight = lineCount * 22;

        var editor = new TextEditor
        {
            Text = code,
            IsReadOnly = true,
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
            SyntaxHighlighting = highlighting,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MinHeight = 40,
        };

        var copyButton = new Button
        {
            Content = "Copy",
            Padding = new Thickness(10, 4, 10, 4),
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            FontSize = 12,
            Template = BuildRoundedButtonTemplate(8)
        };
        copyButton.Click += (_, __) =>
            Clipboard.SetText(code);

        var langBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 3),
            Child = new TextBlock
            {
                Text = lang.ToUpperInvariant(),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 11
            }
        };

        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(langBadge, 0);
        Grid.SetColumn(copyButton, 2);
        header.Children.Add(langBadge);
        header.Children.Add(copyButton);

        var codeContainer = new StackPanel { Margin = new Thickness(0) };
        codeContainer.Children.Add(header);
        codeContainer.Children.Add(editor);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 8, 0, 8),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E)),
            BorderThickness = new Thickness(1),
            Child = codeContainer
        };
    }

    private UIElement CreateThinkingBox(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
            FontSize = 14,
            FontStyle = FontStyles.Italic,
            LineHeight = 22
        };

        return new Expander
        {
            Header = new TextBlock { Text = "🤔 Thought Process", Foreground = Brushes.LightGray, FontWeight = FontWeights.SemiBold },
            Content = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(10, 5, 0, 5),
                Margin = new Thickness(5, 5, 0, 10),
                Child = tb
            },
            IsExpanded = true, // Keep expanded while generating
            Margin = new Thickness(0, 5, 0, 10)
        };
    }

    private TextBlock? FindThinkingTextBlock(UIElement element)
    {
        if (element is Expander exp && exp.Content is Border b && b.Child is TextBlock tb)
            return tb;
        return null;
    }

    /// Builds a simple rounded ControlTemplate for a Button used in code (avoids XAML duplication).
    private static ControlTemplate BuildRoundedButtonTemplate(double radius)
    {
        var template = new ControlTemplate(typeof(Button));
        var bdFactory = new FrameworkElementFactory(typeof(Border));
        bdFactory.SetBinding(Border.BackgroundProperty,
            new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        bdFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        bdFactory.SetBinding(Border.PaddingProperty,
            new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        cpFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cpFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bdFactory.AppendChild(cpFactory);
        template.VisualTree = bdFactory;
        return template;
    }

    // ────────────────────────────────────────────────────────
    //  Token cleaning / language detection  (identical logic)
    // ────────────────────────────────────────────────────────

    private static string CleanToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return "";

        return token
            .Replace("<|assistant|>", "")
            .Replace("<|user|>", "")
            .Replace("<|system|>", "")
            .Replace("<|end|>", "")
            .Replace("<|eot_id|>", "") // Strip Llama 3 stop token
            .Replace("<|im_end|>", "") // Strip ChatML stop token
            .Replace("</s>", "")
            .Replace("<s>", "")
            .Replace("<assistant>", "")
            .Replace("</assistant>", "");
    }

    private static string DetectLanguage(string code)
    {
        string s = code.ToLower();

        if (s.Contains("def ") || s.Contains("import ") || s.Contains("print(")) return "Python";
        if (s.Contains("using ") || s.Contains("namespace ") || s.Contains("Console.WriteLine")) return "C#";
        if (s.Contains("<html") || s.Contains("<body") || s.Contains("<div")) return "HTML";
        if (s.Contains("function ") || s.Contains("const ") || s.Contains("let ")) return "JavaScript";
        if (s.Contains("{") && s.Contains("}") && s.Contains(":")) return "JSON";
        if (s.Contains("SELECT ") || s.Contains("FROM ")) return "SQL";

        // Catch file trees
        if (s.Contains("├──") || s.Contains("└──") || s.Contains("[file]") || s.Contains("[dir]")) return "Text";

        // CHANGED: Default to Text instead of C#
        return "Text";
    }

    // ────────────────────────────────────────────────────────
    //  Input handling
    // ────────────────────────────────────────────────────────

    private void PromptInput_KeyDown(object sender, KeyEventArgs e)
    {
        // Check if the key pressed was Enter
        if (e.Key == Key.Enter)
        {
            // Check if either Shift key is currently held down
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            {
                // Shift+Enter was pressed. 
                // Do nothing here. Because AcceptsReturn="True" in XAML, 
                // the TextBox will automatically insert a new line.
                return;
            }

            // Plain Enter was pressed. Intercept it to prevent a new line and send the message.
            e.Handled = true;

            if (!_isGenerating)
            {
                SendButton_Click(null, null!);
            }
        }
    }

    // Add this list at the top of your MainWindow class, near your other private fields
    private readonly List<string> _availableCommands = new()
    {
        "/help - List all available commands",
        "/web - Force Web Search",
        "/sys - Analyze PC Performance",
        "/clear - Clear Chat History",
        "/codereview - Review Git changes and export to Word",
        "/db - Chat with the database",
        "/exit - Exit the application",
        "/workspace - Chat with the files",
        "/scaffold [prompt] - Generate and write project files directly to disk",
    };

    private void PromptInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        PromptPlaceholder.Visibility = string.IsNullOrEmpty(PromptInput.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        var text = PromptInput.Text;

        // Detect if the user is typing a command
        if (text.StartsWith("/"))
        {
            var query = text.ToLower();
            var matches = _availableCommands.Where(c => c.ToLower().StartsWith(query)).ToList();

            if (matches.Any())
            {
                CommandListBox.ItemsSource = matches;
                SlashCommandPopup.IsOpen = true;
            }
            else
            {
                SlashCommandPopup.IsOpen = false;
            }
        }
        else
        {
            SlashCommandPopup.IsOpen = false;
        }
    }

    // Add this handler for when the user clicks a command in the popup
    private void CommandListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CommandListBox.SelectedItem is string selectedCmd)
        {
            // Extract just the command part (e.g., "/web" from "/web - Force Web Search")
            string cmd = selectedCmd.Split('-')[0].Trim();

            PromptInput.Text = cmd + " ";
            PromptInput.CaretIndex = PromptInput.Text.Length; // Move cursor to end
            PromptInput.Focus();
            SlashCommandPopup.IsOpen = false;
            CommandListBox.SelectedItem = null; // Reset selection
        }
    }

    // ────────────────────────────────────────────────────────
    //  Chat sessions  (all logic preserved 1-to-1)
    // ────────────────────────────────────────────────────────

    private void LoadChatSessions()
    {
        _chatSessions = _chatMemory.LoadChats();

        if (!_chatSessions.Any())
            CreateNewChat();
        else
            OpenChat(_chatSessions.First());

        RefreshConversationSidebar();
    }

    private void CreateNewChat()
    {
        var chat = new ChatSessionModel();
        _chatSessions.Insert(0, chat);
        _currentChat = chat;

        MessagesPanel.Children.Clear();

        SaveChats();
        RefreshConversationSidebar();
    }

    private void UpdateChatTitle(string? prompt)
    {
        if (_currentChat == null || string.IsNullOrWhiteSpace(prompt)) return;

        if (_currentChat.Title == "New Chat")
        {
            _currentChat.Title = prompt.Length > 35
                ? prompt.Substring(0, 35) + "..."
                : prompt;

            RefreshConversationSidebar();
        }
    }

    private void RefreshConversationSidebar()
    {
        ConversationListPanel.Children.Clear();

        var search = SearchChatsInput.Text?.ToLower() ?? "";

        var chats = _chatSessions
            .Where(c => c.Title.ToLower().Contains(search))
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.UpdatedAt)
            .ToList();

        foreach (var chat in chats)
        {
            var isActive = chat == _currentChat;

            var row = new Border
            {
                Margin = new Thickness(0, 2, 0, 2),
                CornerRadius = new CornerRadius(8),
                Background = isActive
                    ? (Brush)FindResource("BgSelected")
                    : Brushes.Transparent,
                Padding = new Thickness(4, 2, 4, 2)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // ---- Open / title button ----
            var openBtn = new Button
            {
                Content = chat.IsPinned ? $"📌 {chat.Title}" : chat.Title,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = isActive
                    ? (Brush)FindResource("TextPrimary")
                    : (Brush)FindResource("TextSecondary"),
                FontSize = 13,
                Padding = new Thickness(6, 6, 6, 6),
                Cursor = Cursors.Hand,
                ToolTip = chat.Title
            };
            // Clip the title inside the button via TextBlock
            if (openBtn.Content is string titleStr)
            {
                openBtn.Content = null;
                openBtn.Content = new TextBlock
                {
                    Text = titleStr,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 140
                };
            }
            openBtn.Click += (_, _) => OpenChat(chat);

            // ---- Rename ----
            var renameBtn = new Button
            {
                Content = "✏",
                Style = (Style)FindResource("GhostButton"),
                ToolTip = "Rename"
            };
            renameBtn.Click += (_, _) => RenameChat(chat);

            // ---- Pin ----
            var pinBtn = new Button
            {
                Content = chat.IsPinned ? "📍" : "📌",
                Style = (Style)FindResource("GhostButton"),
                ToolTip = chat.IsPinned ? "Unpin" : "Pin"
            };
            pinBtn.Click += (_, _) =>
            {
                chat.IsPinned = !chat.IsPinned;
                SaveChats();
                RefreshConversationSidebar();
            };

            // ---- Delete ----
            var deleteBtn = new Button
            {
                Content = "🗑",
                Style = (Style)FindResource("DangerButton"),
                ToolTip = "Delete"
            };
            deleteBtn.Click += (_, _) => DeleteChat(chat);

            Grid.SetColumn(openBtn, 0);
            Grid.SetColumn(renameBtn, 1);
            Grid.SetColumn(pinBtn, 2);
            Grid.SetColumn(deleteBtn, 3);

            grid.Children.Add(openBtn);
            grid.Children.Add(renameBtn);
            grid.Children.Add(pinBtn);
            grid.Children.Add(deleteBtn);

            row.Child = grid;

            // Hover effect
            row.MouseEnter += (_, _) =>
            {
                if (chat != _currentChat)
                    row.Background = (Brush)FindResource("BgHover");
            };
            row.MouseLeave += (_, _) =>
            {
                if (chat != _currentChat)
                    row.Background = Brushes.Transparent;
            };

            ConversationListPanel.Children.Add(row);
        }
    }

    private void OpenChat(ChatSessionModel chat)
    {
        _currentChat = chat;
        MessagesPanel.Children.Clear();

        foreach (var msg in chat.Messages)
            AddMessageBubble(msg.Text, msg.IsUser);

        RefreshConversationSidebar();
    }

    private void SaveChats() => _chatMemory.SaveChats(_chatSessions);

    private void RenameChat(ChatSessionModel chat)
    {
        // Simple rename (appends marker — same behaviour as original)
        chat.Title = chat.Title + " (Renamed)";
        chat.UpdatedAt = DateTime.Now;
        SaveChats();
        RefreshConversationSidebar();
    }

    private void DeleteChat(ChatSessionModel chat)
    {
        _chatSessions.Remove(chat);

        if (_currentChat == chat)
        {
            if (_chatSessions.Any()) OpenChat(_chatSessions.First());
            else CreateNewChat();
        }

        SaveChats();
        RefreshConversationSidebar();
    }

    // ────────────────────────────────────────────────────────
    //  Dashboard monitoring  (all logic preserved)
    // ────────────────────────────────────────────────────────

    private void StartDashboardMonitoring()
    {
        _dashboardCts = new CancellationTokenSource();

        Task.Run(async () =>
        {
            while (!_dashboardCts.Token.IsCancellationRequested)
            {
                var gpu = await _gpuMonitor.GetGpuStatsAsync();
                var sys = await _systemMonitor.GetSystemStatsAsync();
                float tokSec = _metrics.GetTokensPerSecond();
                float elapsed = _metrics.GetElapsedSeconds();

                await Dispatcher.InvokeAsync(() =>
                {
                    GpuNameText.Text = gpu.gpuName;
                    GpuUsageText.Text = $"{gpu.usage}%";
                    GpuMemoryText.Text = $"{gpu.usedMB} / {gpu.totalMB} MB";
                    CudaStatusText.Text = gpu.status;
                    CpuUsageText.Text = $"{sys.cpuUsage:F1}%";
                    RamUsageText.Text = $"{sys.usedRamGB:F1} / {sys.totalRamGB:F1} GB";
                    TokensPerSecText.Text = $"{tokSec:F1} tok/s";
                    ModelLoadTimeText.Text = $"{elapsed:F1} sec";
                });

                await Task.Delay(1000);
            }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _dashboardCts?.Cancel();
        base.OnClosed(e);
    }

    // 1. Add this new method to map Markdown languages to AvalonEdit definitions
    private IHighlightingDefinition? GetHighlightingDefinition(string language)
    {
        // CHANGED: If no language is provided, or it's "text", return null to disable highlighting
        if (string.IsNullOrWhiteSpace(language) || language.Equals("text", StringComparison.OrdinalIgnoreCase))
            return null;

        language = language.ToLowerInvariant();

        string? defName = language switch
        {
            "c#" or "cs" or "csharp" => "C#",
            "c++" or "cpp" => "C++",
            "javascript" or "js" => "JavaScript",
            "typescript" or "ts" => "JavaScript",
            "html" or "htm" => "HTML",
            "xml" => "XML",
            "css" => "CSS",
            "python" or "py" => "Python",
            "java" => "Java",
            "php" => "PHP",
            "sql" => "TSQL",
            "json" => "JavaScript",
            "markdown" or "md" => "MarkDown",
            "powershell" or "ps1" => "PowerShell",
            "bash" or "sh" or "shell" => "PowerShell",
            "vb" or "vbnet" => "VB",
            "text" or "tree" or "txt" => null, // Explicitly map to null
            _ => null
        };

        if (defName != null)
        {
            var definition = HighlightingManager.Instance.GetDefinition(defName);
            if (definition != null) return definition;
        }

        // CHANGED: Return the exact match if found, otherwise return null (No default C# fallback)
        return HighlightingManager.Instance.GetDefinition(language);
    }

    private void PromptInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            // Case A: User copied an actual image file from Windows Explorer
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count > 0)
                {
                    string firstFile = files[0]!;
                    string ext = System.IO.Path.GetExtension(firstFile).ToLower();
                    if (ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp" || ext == ".webp")
                    {
                        LoadAttachedImage(firstFile);
                        e.Handled = true;
                        return;
                    }
                }
            }
            // Case B: User used Snipping Tool or right-clicked "Copy Image" on the web
            else if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image != null)
                {
                    // LlamaSharp needs a file, so we quietly save the clipboard image to the Windows Temp folder
                    string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"execor_clip_{Guid.NewGuid()}.png");
                    using (var fileStream = new System.IO.FileStream(tempPath, System.IO.FileMode.Create))
                    {
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                        encoder.Save(fileStream);
                    }

                    LoadAttachedImage(tempPath);
                    e.Handled = true;
                    return; // Prevent the TextBox from trying to paste text
                }
            }
        }

        // Check if the key pressed was Enter
        if (e.Key == Key.Enter)
        {
            // If holding either Shift key, do nothing. 
            // AcceptsReturn="True" will natively insert the new line.
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            {
                return;
            }

            // Plain Enter was pressed. Intercept it immediately to prevent the newline.
            e.Handled = true;

            // Trigger the send logic
            if (!_isGenerating)
            {
                SendButton_Click(null, null!);
            }
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // 1. Show the frame
        SettingsFrame.Visibility = Visibility.Visible;

        // 2. Navigate and pass the callback (REPLACE THIS LINE)
        SettingsFrame.Navigate(new SettingsPage(_modelManager, _mcpService.AvailableTools, (settingsChanged) =>
        {
            // 3. Use Dispatcher.InvokeAsync to safely close the frame 
            // after the page's button click event has fully resolved.
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                SettingsFrame.Visibility = Visibility.Collapsed;
                SettingsFrame.Content = null; // Free up memory and clear the frame

                if (settingsChanged)
                {
                    LoadModels(); // Reload models if the path was changed
                }
            });
        }));
    }

    private class ParsedBlock
    {
        public BlockType Type { get; set; }
        public string Text { get; set; } = "";
        public string Language { get; set; } = "";
    }

    private List<ParsedBlock> ParseMarkdown(string text)
    {
        var blocks = new List<ParsedBlock>();

        // 1. Extract <think> blocks first using Regex
        var thinkParts = System.Text.RegularExpressions.Regex.Split(text, @"(<think>.*?</think>|<think>.*$)", System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (var thinkPart in thinkParts)
        {
            if (string.IsNullOrEmpty(thinkPart)) continue;

            if (thinkPart.StartsWith("<think>"))
            {
                string thinkContent = thinkPart.Replace("<think>", "").Replace("</think>", "").TrimStart('\n', '\r');
                blocks.Add(new ParsedBlock { Type = BlockType.Think, Text = thinkContent });
            }
            else
            {
                // 2. Parse code blocks normally within the remaining text
                var parts = thinkPart.Split(new[] { "```" }, StringSplitOptions.None);
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i % 2 == 0) // Normal text
                    {
                        if (!string.IsNullOrEmpty(parts[i]))
                            blocks.Add(new ParsedBlock { Type = BlockType.Text, Text = parts[i] });
                    }
                    else // Inside a code block
                    {
                        var codePart = parts[i];
                        string lang = "";
                        int newlineIdx = codePart.IndexOf('\n');

                        if (newlineIdx != -1 && newlineIdx < 20)
                        {
                            lang = codePart.Substring(0, newlineIdx).Trim();
                            codePart = codePart.Substring(newlineIdx + 1);
                        }
                        else if (codePart.Length < 20 && !codePart.Contains(" "))
                        {
                            lang = codePart.Trim();
                            codePart = "";
                        }

                        blocks.Add(new ParsedBlock { Type = BlockType.Code, Text = codePart, Language = lang });
                    }
                }
            }
        }
        return blocks;
    }

    private async Task RunCodeReviewAsync(string repoPath)
    {
        _isGenerating = true;
        SendButton.IsEnabled = false;

        // Render initial UI state
        AddMessageBubble($"/codereview {repoPath}", isUser: true);
        var assistantBlock = AddMessageBubble("🔍 Initializing Code Review Engine...", isUser: false);

        // We will update this single TextBlock to show progress instead of streaming code to the UI
        var statusText = new TextBlock { FontSize = 15, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        assistantBlock.Children.Add(statusText);

        try
        {
            _reviewRepoPath = repoPath;
            _reviewBranchName = RunGitCommandSafe(repoPath, "branch --show-current").Output.Trim();

            // 1. Get working directory & staged files
            var workingFiles = RunGitCommandSafe(repoPath, "diff --name-only HEAD").Output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            // 2. Get stashed files (Safely ignores if no stash exists)
            var stashedFiles = RunGitCommandSafe(repoPath, "stash show --name-only").Output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            // 3. Combine and deduplicate
            var allFiles = workingFiles.Concat(stashedFiles).Distinct().ToList();

            if (!allFiles.Any())
            {
                statusText.Text = $"No uncommitted or stashed changes found in branch '{_reviewBranchName}'.";
                return;
            }

            // Ensure model is loaded before starting
            var selectedModel = ModelSelector.SelectedItem as ModelInfo;
            if (selectedModel != null)
            {
                var activeModel = _modelManager.GetActiveModel();
                if (activeModel == null || activeModel.Name != selectedModel.Name)
                {
                    await Task.Run(() =>
                    {
                        _modelManager.SetActiveModel(selectedModel.Name);
                        _chatService.LoadActiveModel();
                    });
                }
            }

            // ==========================================
            // PASS 1: GENERATE GLOBAL CONTEXT (The Blueprint)
            // ==========================================
            await Dispatcher.InvokeAsync(() => statusText.Text = "🧠 Pass 1: Analyzing global repository context...");

            // Grab just the names of the files and maybe the commit messages to build a tiny summary
            string fileListStr = string.Join("\n- ", allFiles);
            string globalSummaryPrompt = $"You are a senior architect. Look at this list of files modified in the branch '{_reviewBranchName}':\n- {fileListStr}\n\n" +
                                         $"In 3 short sentences, guess the overall goal of this feature or bug fix. Do not write code.";

            string globalContext = "";
            await foreach (var chunk in _chatService.StreamChatAsync(globalSummaryPrompt, null))
            {
                if (_cts?.IsCancellationRequested == true) break;
                globalContext += CleanToken(chunk);
            }

            string masterMarkdown = $"# Code Review for `{_reviewBranchName}`\n\n**AI Global Context Analysis:**\n> {globalContext.Trim()}\n\n---\n\n";
            int count = 1;

            // ==========================================
            // PASS 2: THE DEEP CHUNKED REVIEW
            // ==========================================

            // Safe character limit to prevent exceeding a 4096 context window
            int maxChunkLength = 4000;

            foreach (var file in allFiles)
            {
                string workingDiff = RunGitCommandSafe(repoPath, $"diff HEAD -- \"{file}\"").Output;
                string stashDiff = RunGitCommandSafe(repoPath, $"stash show -p -- \"{file}\"").Output;
                string combinedDiff = (workingDiff + "\n" + stashDiff).Trim();

                if (string.IsNullOrWhiteSpace(combinedDiff)) continue;

                // 1. Break the diff into smaller chunks if it's massive
                var diffChunks = new List<string>();
                if (combinedDiff.Length <= maxChunkLength)
                {
                    diffChunks.Add(combinedDiff);
                }
                else
                {
                    // Split the text into parts
                    for (int i = 0; i < combinedDiff.Length; i += maxChunkLength)
                    {
                        int length = Math.Min(maxChunkLength, combinedDiff.Length - i);
                        diffChunks.Add(combinedDiff.Substring(i, length));
                    }
                }

                // 2. Process each chunk independently
                for (int i = 0; i < diffChunks.Count; i++)
                {
                    _chatService.ClearHistory(); // Wipe short-term memory to prevent overflow

                    string chunk = diffChunks[i];
                    string partInfo = diffChunks.Count > 1 ? $" (Part {i + 1} of {diffChunks.Count})" : "";

                    await Dispatcher.InvokeAsync(() =>
                        statusText.Text = $"📝 Pass 2: Reviewing file {count} of {allFiles.Count}{partInfo}:\n`{file}`..."
                    );

                    string prompt = $"You are reviewing code. Keep in mind the overarching goal of this branch is:\n'{globalContext.Trim()}'\n\n" +
                                    $"Review these specific changes for the file '{file}'{partInfo}.\n" +
                                    $"Identify bugs, suggest improvements, and format clearly.\n\n" +
                                    $"### GIT DIFF:\n```diff\n{chunk}\n```";

                    string fileReview = "";
                    await foreach (var token in _chatService.StreamChatAsync(prompt, null))
                    {
                        if (_cts?.IsCancellationRequested == true) break;
                        fileReview += CleanToken(token);
                    }

                    masterMarkdown += $"## File: `{file}`{partInfo}\n{fileReview}\n\n---\n\n";
                }

                count++;
            }

            await Dispatcher.InvokeAsync(() => statusText.Text = "📄 Compiling Word Document...");

            // 5. Export to Word
            GenerateWordDocument(masterMarkdown);

            await Dispatcher.InvokeAsync(() =>
                statusText.Text = $"✅ Review complete! Analyzed {allFiles.Count} files.\n💾 Document successfully saved to your Desktop."
            );
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => statusText.Text = $"❌ Error during review: {ex.Message}");
        }
        finally
        {
            ResetSendState();
        }
    }

    private (bool Success, string Output) RunGitCommandSafe(string workingDirectory, string arguments)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // Return success only if ExitCode is 0, safely absorbing errors like "No stash entries found"
            return (process.ExitCode == 0, output);
        }
        catch
        {
            return (false, "");
        }
    }

    private async void GenerateWordDocument(string markdownContent)
    {
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        string htmlContent = Markdown.ToHtml(markdownContent, pipeline);

        string wordHtml = $@"
        <html xmlns:o='urn:schemas-microsoft-com:office:office' xmlns:w='urn:schemas-microsoft-com:office:word' xmlns='http://www.w3.org/TR/REC-html40'>
        <head>
            <meta charset='utf-8'>
            <title>Code Review</title>
            <style>
                body {{ font-family: 'Calibri', sans-serif; font-size: 11pt; line-height: 1.5; }}
                pre {{ background-color: #f6f8fa; padding: 12px; border: 1px solid #d0d7de; border-radius: 6px; }}
                code {{ font-family: 'Consolas', monospace; font-size: 10pt; color: #24292f; }}
                h1, h2, h3 {{ color: #0969da; margin-bottom: 8px; }}
                h1 {{ font-size: 18pt; border-bottom: 2px solid #0969da; padding-bottom: 4px; }}
                h2 {{ font-size: 14pt; background-color: #f0f8ff; padding: 4px 8px; }}
                hr {{ border-top: 1px solid #d0d7de; margin: 20px 0; }}
            </style>
        </head>
        <body>
            <h1>Code Review Report</h1>
            <p><strong>Branch:</strong> {_reviewBranchName}</p>
            <p><strong>Repository:</strong> {_reviewRepoPath}</p>
            <p><strong>Date Generated:</strong> {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>
            <hr/>
            {htmlContent}
        </body>
        </html>";

        string fileName = $"CodeReview_{_reviewBranchName}_{DateTime.Now:yyyyMMdd_HHmmss}.doc";
        string filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);

        await File.WriteAllTextAsync(filePath, wordHtml);
    }

    private void DatabaseButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsFrame.Visibility = Visibility.Visible;

        // Update callback to receive both schema and connection string
        SettingsFrame.Navigate(new DatabasePage((schemaMd, connString) =>
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!string.IsNullOrEmpty(schemaMd))
                {
                    _activeDatabaseSchema = schemaMd;
                    _activeConnectionString = connString; // Save the connection string!
                    AddMessageBubble("Database connected! Use /db [query] to chat with the database.", isUser: false);
                }

                SettingsFrame.Visibility = Visibility.Collapsed;
                SettingsFrame.Content = null;
            });
        }));
    }

    private async Task RunDatabaseChatAsync(string userRequest)
    {
        _isGenerating = true;
        SendButton.IsEnabled = false;

        // 1. Setup the UI for the background process
        AddMessageBubble($"/db {userRequest}", isUser: true);
        var assistantBlock = AddMessageBubble("🧠 Analyzing schema and generating SQL...", isUser: false);
        var statusText = new TextBlock { FontSize = 15, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        assistantBlock.Children.Add(statusText);

        try
        {
            // 2. Build the strict, read-only Prompt
            string sqlPrompt = $"You are an expert, read-only MS SQL Database Administrator. \n" +
                               $"Using this exact database schema:\n{_activeDatabaseSchema}\n\n" +
                               $"Write a valid MS SQL SELECT query to answer the user's request: '{userRequest}'.\n" +
                               $"CRITICAL RULES:\n" +
                               $"- You are strictly forbidden from writing UPDATE, INSERT, DELETE, DROP, or ALTER queries.\n" +
                               $"- Output ONLY the raw SQL code wrapped in ```sql and ``` blocks. \n" +
                               $"- Do not explain your thought process. Do not write any conversational text.";

            string generatedText = "";

            // ==========================================
            // NEW: Ensure model is loaded into VRAM before continuing
            // ==========================================
            var selectedModel = ModelSelector.SelectedItem as Execor.Models.ModelInfo;
            if (selectedModel != null)
            {
                var activeModel = _modelManager.GetActiveModel();
                if (activeModel == null || activeModel.Name != selectedModel.Name)
                {
                    await Task.Run(() =>
                    {
                        _modelManager.SetActiveModel(selectedModel.Name);
                        _chatService.LoadActiveModel();
                    });
                }
            }
            // ==========================================

            // 3. Wipe short-term memory to give the model maximum VRAM for writing the query
            _chatService.ClearHistory();

            // 4. Stream the SQL generation silently in the background
            await foreach (var chunk in _chatService.StreamChatAsync(sqlPrompt, null))
            {
                if (_cts?.IsCancellationRequested == true) break;
                generatedText += CleanToken(chunk);
            }

            // 5. Extract the SQL using Regex (in case the model disobeys and adds conversational text)
            string rawSql = generatedText.Trim();
            var match = System.Text.RegularExpressions.Regex.Match(
                generatedText,
                @"```sql\s*(.*?)\s*```",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success)
            {
                rawSql = match.Groups[1].Value.Trim();
            }
            else if (rawSql.StartsWith("```") && rawSql.EndsWith("```"))
            {
                // Fallback for poorly formatted code blocks
                rawSql = rawSql.Replace("```sql", "").Replace("```", "").Trim();
            }

            await Dispatcher.InvokeAsync(() => statusText.Text = $"📝 Executing Query:\n{rawSql}\n\n⚙️ Waiting for database response...");

            // 6. Execute the generated SQL against your actual database via the secure service
            var dbService = new Execor.Inference.Services.DatabaseSchemaService();
            string queryResults = await dbService.ExecuteReadOnlyQueryAsync(_activeConnectionString, rawSql);

            /// 7. Format the final output safely and force a tabular monospace render
            string finalOutput = "**Generated Query:**\n" +
                                 "```sql\n" + rawSql + "\n```\n\n" +
                                 "**Database Results:**\n" +
                                 "```text\n" +
                                 queryResults +
                                 "\n```";

            // 8. Render the results back to the UI chat bubble
            await Dispatcher.InvokeAsync(() =>
            {
                assistantBlock.Children.Clear(); // Remove the loading status text
                SyncStreamingUI(assistantBlock, finalOutput, isFinished: true);
                ChatScrollViewer.ScrollToEnd();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => statusText.Text = $"❌ Execution Error: {ex.Message}");
        }
        finally
        {
            // 9. Clean up state so the user can send the next message
            ResetSendState();
        }
    }

    private async Task RunScaffoldAsync(string userRequest)
    {
        _isGenerating = true;
        SendButton.IsEnabled = false;

        // Render initial UI state
        AddMessageBubble($"/scaffold {userRequest}", isUser: true);
        var assistantBlock = AddMessageBubble("🏗️ Analyzing architecture and scaffolding files...", isUser: false);
        var statusText = new TextBlock { FontSize = 15, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        assistantBlock.Children.Add(statusText);

        try
        {
            // 1. Ensure Model is Loaded
            var selectedModel = ModelSelector.SelectedItem as ModelInfo;
            if (selectedModel != null)
            {
                var activeModel = _modelManager.GetActiveModel();
                if (activeModel == null || activeModel.Name != selectedModel.Name)
                {
                    await Task.Run(() =>
                    {
                        _modelManager.SetActiveModel(selectedModel.Name);
                        _chatService.LoadActiveModel();
                    });
                }
            }

            // 2. Gather Local RAG Context to match coding styles/namespaces
            string? ragContext = await _workspaceService.SearchAsync($"Architecture conventions for: {userRequest}", topK: 3);
            string baseDir = _workspaceService.ActiveWorkspacePath;

            // 3. The Strict Scaffolding Prompt
            string scaffoldPrompt =
                $"You are an expert software architect. The user wants to scaffold the following inside their current project: '{userRequest}'.\n\n" +
                $"### CONTEXT (Current Project Code):\n{ragContext}\n\n" +
                $"### INSTRUCTIONS:\n" +
                $"1. Determine the necessary files to create. Adapt to the project's language and framework.\n" +
                $"2. If this is a MASSIVE request, DO NOT generate thousands of lines of code. Instead, generate the foundational/core files, AND generate a `scaffolding_plan.md` file containing the exact instructions and architecture for the remaining components.\n" +
                $"3. You MUST format EVERY file you want to create using this EXACT syntax:\n\n" +
                $"### FILE: relative/path/to/filename.ext\n" +
                $"```language\n" +
                $"// full code here\n" +
                $"```\n\n" +
                $"Do not deviate from this format. I am parsing your output with a script.";

            // Wipe history to maximize VRAM for code generation
            _chatService.ClearHistory();

            string generatedOutput = "";
            await foreach (var chunk in _chatService.StreamChatAsync(scaffoldPrompt, null))
            {
                if (_cts?.IsCancellationRequested == true) break;
                generatedOutput += CleanToken(chunk);
            }

            await Dispatcher.InvokeAsync(() => statusText.Text = "💾 Parsing generated code and writing to disk...");

            // 4. Parse the output using Regex to extract File Paths and Code
            // This regex captures the file path after "### FILE:" and the code block content
            var regex = new System.Text.RegularExpressions.Regex(
                @"### FILE:\s*([^\n\r]+)[\s\S]*?```[a-zA-Z0-9]*\s*\n([\s\S]*?)```",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var matches = regex.Matches(generatedOutput);
            int filesCreated = 0;
            string reportMarkdown = "**Scaffolding Complete!**\n\nThe following files were created/modified:\n\n";

            if (matches.Count == 0)
            {
                reportMarkdown = "⚠️ **Warning:** No files were generated. The AI did not follow the scaffolding syntax. Here was its raw response:\n\n" + generatedOutput;
            }
            else
            {
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    string relativePath = match.Groups[1].Value.Trim();
                    string fileContent = match.Groups[2].Value.TrimEnd();

                    // Sanitize path to prevent directory traversal attacks (e.g., ../../../Windows/System32)
                    relativePath = relativePath.Replace("..\\", "").Replace("../", "");

                    string fullPath = Path.Combine(baseDir, relativePath);

                    // Ensure directory exists
                    string? dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    // Write the file
                    await File.WriteAllTextAsync(fullPath, fileContent);
                    filesCreated++;

                    reportMarkdown += $"- ✅ `{relativePath}`\n";
                }

                reportMarkdown += $"\n*Total files successfully written to `{baseDir}`: {filesCreated}*";
            }

            // 5. Render Final Output
            await Dispatcher.InvokeAsync(() =>
            {
                assistantBlock.Children.Clear();
                SyncStreamingUI(assistantBlock, reportMarkdown, isFinished: true);
                ChatScrollViewer.ScrollToEnd();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => statusText.Text = $"❌ Scaffolding Error: {ex.Message}");
        }
        finally
        {
            ResetSendState();
        }
    }

    private async void InitializeDefaultMcpServers()
    {
        try
        {
            // UPDATED: Point this to the root project folder so the AI can see UI, API, Core, etc.
            string targetDir = @"A:\Projects\";

            await Dispatcher.InvokeAsync(() =>
            {
                AddMessageBubble("🔌 Booting Filesystem MCP Server...", isUser: false);
            });

            // UPDATED: Added "ProjectRoot" as the server name to match the new McpClientService signature
            await _mcpService.ConnectAsync("ProjectRoot", "cmd.exe", $"/c npx -y @modelcontextprotocol/server-filesystem \"{targetDir}\"");

            await Dispatcher.InvokeAsync(() =>
            {
                AddMessageBubble($"✅ MCP Connected! {_mcpService.AvailableTools.Count} tools loaded for the entire project.", false);
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                AddMessageBubble($"❌ MCP Auto-Connect Failed: {ex.Message}", false);
            });
        }
    }

    private List<McpTool> SelectRelevantTools(string prompt)
    {
        var query = prompt.ToLower();
        var allTools = _mcpService.AvailableTools;

        if (allTools.Count <= 5) return allTools;

        var scoredTools = allTools
            .Select(t => new
            {
                Tool = t,
                // Improved matching: split names like "list_directory" to match standard English words
                Score = (t.Name.ToLower().Split('_').Any(part => query.Contains(part)) ? 5 : 0) +
                        (t.Description.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                      .Count(w => w.Length > 3 && query.Contains(w)))
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        // FALLBACK: Always return the top 5 tools so the AI is never left blind
        return scoredTools.Take(5).Select(x => x.Tool).ToList();
    }
}
