using Execor.Core;
using Execor.Inference.Services;
using Execor.Models;
using Execor.UI.Services;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
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
    private CancellationTokenSource? _dashboardCts;
    private readonly IModelManager _modelManager;
    private readonly IChatService _chatService;
    private enum BlockType { Text, Code }

    public MainWindow(IModelManager modelManager, IChatService chatService)
    {
        InitializeComponent();

        _modelManager = modelManager;
        _chatService = chatService;

        // Wire up all events (mirroring original Avalonia wiring)
        SendButton.Click         += SendButton_Click;
        RefreshModelsButton.Click += (_, _) => LoadModels();
        NewChatButton.Click      += (_, _) => CreateNewChat();
        StopButton.Click         += (_, _) => _cts?.Cancel();

        PromptInput.KeyDown    += PromptInput_KeyDown;
        PromptInput.TextChanged += PromptInput_TextChanged;
        SearchChatsInput.KeyUp  += (_, _) => RefreshConversationSidebar();

        _systemMonitor = new SystemMonitorService();
        _metrics       = new InferenceMetricsService();
        _gpuMonitor    = new GpuMonitorService();

        // Input border focus visual
        PromptInput.GotFocus  += (_, _) => InputBorder.BorderBrush =
            (SolidColorBrush)FindResource("AccentBlue");
        PromptInput.LostFocus += (_, _) => InputBorder.BorderBrush =
            (SolidColorBrush)FindResource("BorderColor");

        StartDashboardMonitoring();
        LoadModels();
        LoadChatSessions();
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
        }
        // ==========================================

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

            await foreach (var chunk in _chatService.StreamChatAsync(prompt, webContext)) // Assuming webContext added
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
                _metrics.AddToken(cleanChunk);

                await Dispatcher.InvokeAsync(() =>
                {
                    SyncStreamingUI(assistantBlock, accumulatedText);
                    ChatScrollViewer.ScrollToEnd();
                });

                await Task.Delay(cleanChunk.Contains(".") || cleanChunk.Contains(",") ? 40 : 10);
            }

            await Dispatcher.InvokeAsync(() =>
            {
                SyncStreamingUI(assistantBlock, accumulatedText, isFinished: true);
                ChatScrollViewer.ScrollToEnd();
            });
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
        _isGenerating         = false;
        SendButton.IsEnabled  = true;
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
        //1. Parse the cleaned text into UI blocks
        var parsedBlocks = ParseMarkdown(text);

        while (panel.Children.Count < parsedBlocks.Count)
        {
            var block = parsedBlocks[panel.Children.Count];
            if (block.Type == BlockType.Text)
            {
                panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15, Foreground = Brushes.White, LineHeight = 22 });
            }
            else
            {
                panel.Children.Add(CreateSyntaxHighlightedCodeBox("", block.Language));
            }
        }

        // Clean up previous blocks
        for (int i = 0; i < parsedBlocks.Count - 1; i++)
        {
            if (parsedBlocks[i].Type == BlockType.Text)
                ((TextBlock)panel.Children[i]).Text = parsedBlocks[i].Text;
            else
            {
                var editor = FindTextEditor(panel.Children[i]);
                if (editor != null) editor.Text = parsedBlocks[i].Text;
            }
        }

        // Update the active (last) block
        if (parsedBlocks.Any())
        {
            var lastParsed = parsedBlocks.Last();
            // Important: we only want to update the standard blocks here, not the OptionsPanel if it exists
            var lastUI = panel.Children.OfType<FrameworkElement>().LastOrDefault(c => c.Name != "OptionsPanel");

            string cursor = isFinished ? "" : "▋";

            if (lastUI is TextBlock tb)
            {
                tb.Text = lastParsed.Text + cursor;
            }
            else if (lastUI != null)
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
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 40,
            MaxHeight = 400
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
            .Replace("<|user|>",      "")
            .Replace("<|system|>",    "")
            .Replace("<|end|>",       "")
            .Replace("</s>",          "")
            .Replace("<s>",           "")
            .Replace("<assistant>",   "")
            .Replace("</assistant>",  "");
    }

    private static string DetectLanguage(string code)
    {
        string s = code.ToLower();

        if (s.Contains("def ") || s.Contains("import ") || s.Contains("print("))
            return "Python";
        if (s.Contains("using ") || s.Contains("namespace ") || s.Contains("Console.WriteLine"))
            return "C#";
        if (s.Contains("<html") || s.Contains("<body") || s.Contains("<div"))
            return "HTML";
        if (s.Contains("function ") || s.Contains("const ") || s.Contains("let "))
            return "JavaScript";
        if (s.Contains("{") && s.Contains("}") && s.Contains(":"))
            return "JSON";
        if (s.Contains("SELECT ") || s.Contains("FROM "))
            return "SQL";

        return "C#";
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
        "/web - Force Web Search",
        "/sys - Analyze PC Performance",
        "/clear - Clear Chat History"
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
                Margin          = new Thickness(0, 2, 0, 2),
                CornerRadius    = new CornerRadius(8),
                Background      = isActive
                    ? (Brush)FindResource("BgSelected")
                    : Brushes.Transparent,
                Padding         = new Thickness(4, 2, 4, 2)
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
                Background      = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground      = isActive
                    ? (Brush)FindResource("TextPrimary")
                    : (Brush)FindResource("TextSecondary"),
                FontSize        = 13,
                Padding         = new Thickness(6, 6, 6, 6),
                Cursor          = Cursors.Hand,
                ToolTip         = chat.Title
            };
            // Clip the title inside the button via TextBlock
            if (openBtn.Content is string titleStr)
            {
                openBtn.Content = null;
                openBtn.Content = new TextBlock
                {
                    Text          = titleStr,
                    TextTrimming  = TextTrimming.CharacterEllipsis,
                    MaxWidth      = 140
                };
            }
            openBtn.Click += (_, _) => OpenChat(chat);

            // ---- Rename ----
            var renameBtn = new Button
            {
                Content         = "✏",
                Style           = (Style)FindResource("GhostButton"),
                ToolTip         = "Rename"
            };
            renameBtn.Click += (_, _) => RenameChat(chat);

            // ---- Pin ----
            var pinBtn = new Button
            {
                Content  = chat.IsPinned ? "📍" : "📌",
                Style    = (Style)FindResource("GhostButton"),
                ToolTip  = chat.IsPinned ? "Unpin" : "Pin"
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
                Style   = (Style)FindResource("DangerButton"),
                ToolTip = "Delete"
            };
            deleteBtn.Click += (_, _) => DeleteChat(chat);

            Grid.SetColumn(openBtn,    0);
            Grid.SetColumn(renameBtn,  1);
            Grid.SetColumn(pinBtn,     2);
            Grid.SetColumn(deleteBtn,  3);

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
        chat.Title      = chat.Title + " (Renamed)";
        chat.UpdatedAt  = DateTime.Now;
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
                var gpu     = await _gpuMonitor.GetGpuStatsAsync();
                var sys     = await _systemMonitor.GetSystemStatsAsync();
                float tokSec  = _metrics.GetTokensPerSecond();
                float elapsed = _metrics.GetElapsedSeconds();

                await Dispatcher.InvokeAsync(() =>
                {
                    GpuNameText.Text     = gpu.gpuName;
                    GpuUsageText.Text    = $"{gpu.usage}%";
                    GpuMemoryText.Text   = $"{gpu.usedMB} / {gpu.totalMB} MB";
                    CudaStatusText.Text  = gpu.status;
                    CpuUsageText.Text    = $"{sys.cpuUsage:F1}%";
                    RamUsageText.Text    = $"{sys.usedRamGB:F1} / {sys.totalRamGB:F1} GB";
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
    private IHighlightingDefinition GetHighlightingDefinition(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return HighlightingManager.Instance.GetDefinition("C#");

        // Normalize to lowercase for safe matching
        language = language.ToLowerInvariant();

        string defName = language switch
        {
            "c#" or "cs" or "csharp" => "C#",
            "c++" or "cpp" => "C++",
            "javascript" or "js" => "JavaScript",
            "typescript" or "ts" => "JavaScript", // Fallback for TS
            "html" or "htm" => "HTML",
            "xml" => "XML",
            "css" => "CSS",
            "python" or "py" => "Python",
            "java" => "Java",
            "php" => "PHP",
            "sql" => "TSQL",
            "json" => "JavaScript", // JSON highlights well as JS
            "markdown" or "md" => "MarkDown",
            "powershell" or "ps1" => "PowerShell",
            "bash" or "sh" or "shell" => "PowerShell", // Fallback for bash
            "vb" or "vbnet" => "VB",
            _ => null
        };

        if (defName != null)
        {
            var definition = HighlightingManager.Instance.GetDefinition(defName);
            if (definition != null) return definition;
        }

        // Try exact match as a last resort
        return HighlightingManager.Instance.GetDefinition(language)
            ?? HighlightingManager.Instance.GetDefinition("C#");
    }

    private void PromptInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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

        // 2. Navigate and pass the callback
        SettingsFrame.Navigate(new SettingsPage(_modelManager, (settingsChanged) =>
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
        var parts = text.Split(new[] { "```" }, StringSplitOptions.None);

        for (int i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 0) // Even indices are normal text
            {
                blocks.Add(new ParsedBlock { Type = BlockType.Text, Text = parts[i] });
            }
            else // Odd indices are inside code blocks
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
                    lang = codePart.Trim(); // Still typing language name
                    codePart = "";
                }

                blocks.Add(new ParsedBlock { Type = BlockType.Code, Text = codePart, Language = lang });
            }
        }
        return blocks;
    }
}
