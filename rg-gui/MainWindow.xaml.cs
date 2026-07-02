using Peter;
using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FramePFX.Themes;

namespace rg_gui
{
    public partial class MainWindow : Window
    {
        public const string DEFAULT_BASEPATH = "";
        public const string DEFAULT_INCLUDEFILES = "*.*";
        public const string DEFAULT_EXCLUDEFILES = "";
        public const string DEFAULT_CONTAININGTEXT = "";
        public const bool DEFAULT_CASESENSITIVE = false;
        public const bool DEFAULT_RECURSIVE = true;
        public const bool DEFAULT_REGULAREXPRESSION = false;
        public const string DEFAULT_FILEENCODING = "Auto";
        public const int DEFAULT_MAXFILESIZE = 0;
        public const string DEFAULT_MAXFILESIZEUNIT = "None";
        public const int DEFAULT_MAXSEARCHTERMS = 10;
        public const bool DEFAULT_MULTIPLEHIGHLIGHTCOLORS = true;
        public const int DEFAULT_MAXLINEHIGHLIGHTS = 100;
        public const ThemeType DEFAULT_THEME = ThemeType.Dark;

        // Global static settings accessible by all search tabs
        public static string FileViewerPath { get; set; } = string.Empty;
        public static string FileViewerArgs { get; set; } = string.Empty;
        public static int MaxSearchTerms { get; set; } = DEFAULT_MAXSEARCHTERMS;
        public static bool MultipleHighlightColors { get; set; } = DEFAULT_MULTIPLEHIGHLIGHTCOLORS;
        public static int MaxLineHighlights { get; set; } = DEFAULT_MAXLINEHIGHLIGHTS;
        
        private static ThemeType m_currentTheme = DEFAULT_THEME;
        public static ThemeType CurrentTheme
        {
            get => m_currentTheme;
            set
            {
                m_currentTheme = value;
                ThemesController.SetTheme(value);
            }
        }

        private int m_tabCounter = 1;

        public MainWindow(string? basePath, string? includeFiles, string? excludeFiles, string? containingText)
        {
            // Load global configurations first (before InitializeComponent!)
            var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            
            CurrentTheme = Enum.TryParse<ThemeType>(config.AppSettings.Settings["Theme"]?.Value, true, out var themeName) ? themeName : DEFAULT_THEME;
            
            MaxSearchTerms = int.TryParse(config.AppSettings.Settings["MaxSearchTerms"]?.Value, out var maxSearchTerms) ? maxSearchTerms : DEFAULT_MAXSEARCHTERMS;
            MultipleHighlightColors = bool.TryParse(config.AppSettings.Settings["MultipleHighlightColors"]?.Value, out var multipleHighlightColors) ? multipleHighlightColors : DEFAULT_MULTIPLEHIGHLIGHTCOLORS;
            MaxLineHighlights = int.TryParse(config.AppSettings.Settings["MaxLineHighlights"]?.Value, out var maxLineHighlights) ? maxLineHighlights : DEFAULT_MAXLINEHIGHLIGHTS;

            FileViewerPath = config.AppSettings.Settings["FileViewerPath"]?.Value ?? string.Empty;
            FileViewerArgs = config.AppSettings.Settings["FileViewerArgs"]?.Value ?? string.Empty;

            InitializeComponent();

            // Set window geometry (must happen after InitializeComponent so window structures exist)
            if (double.TryParse(config.AppSettings.Settings["MainWindowLeft"]?.Value, out var left)) Left = left;
            if (double.TryParse(config.AppSettings.Settings["MainWindowTop"]?.Value, out var top)) Top = top;
            if (double.TryParse(config.AppSettings.Settings["MainWindowWidth"]?.Value, out var width)) Width = width;
            if (double.TryParse(config.AppSettings.Settings["MainWindowHeight"]?.Value, out var height)) Height = height;
            if (Enum.TryParse<WindowState>(config.AppSettings.Settings["MainWindowState"]?.Value, out var windowState)) WindowState = windowState;

            // Setup first search tab with command-line arguments if present
            AddNewTab($"Search {m_tabCounter++}", basePath, includeFiles, excludeFiles, containingText);
        }

        private void tabControlSearches_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (tabControlSearches.SelectedItem == plusTab)
            {
                AddNewTab($"Search {m_tabCounter++}");
            }
        }

        private void AddNewTab(string headerText, string? basePath = null, string? includeFiles = null, string? excludeFiles = null, string? containingText = null)
        {
            var tabItem = new TabItem();

            // Create custom header with close button
            var headerStack = new StackPanel { Orientation = Orientation.Horizontal };
            var headerTitle = new TextBlock { Text = headerText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
            var closeButton = new Button
            {
                Content = "×",
                Width = 16,
                Height = 16,
                Padding = new Thickness(0, -2, 0, 0),
                Margin = new Thickness(2, 0, 0, 0),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
                ToolTip = "Close Search Tab"
            };

            closeButton.Click += (s, ev) =>
            {
                ev.Handled = true;
                CloseTab(tabItem);
            };

            headerStack.Children.Add(headerTitle);
            headerStack.Children.Add(closeButton);

            tabItem.Header = headerStack;
            
            // Content is an instance of SearchTab control
            var searchTab = new SearchTab();
            if (basePath != null) searchTab.cmbBasePath.Text = basePath;
            if (includeFiles != null) searchTab.cmbIncludeFiles.Text = includeFiles;
            if (excludeFiles != null) searchTab.cmbExcludeFiles.Text = excludeFiles;
            if (containingText != null) searchTab.cmbContainingText.Text = containingText;

            tabItem.Content = searchTab;

            // Insert new tab before the "+" tab
            int plusIndex = tabControlSearches.Items.IndexOf(plusTab);
            if (plusIndex >= 0)
            {
                tabControlSearches.Items.Insert(plusIndex, tabItem);
            }
            else
            {
                tabControlSearches.Items.Add(tabItem);
            }

            // Select the newly created tab
            tabControlSearches.SelectedItem = tabItem;
        }

        private void CloseTab(TabItem tabItem)
        {
            // Keep at least one active search tab
            if (tabControlSearches.Items.Count <= 2) // 1 search tab + 1 "+" tab = 2 items
            {
                MessageBox.Show("At least one active search tab is required.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (tabItem.Content is SearchTab searchTab)
            {
                searchTab.CancelSearch();
            }

            // Temporarily detach SelectionChanged to avoid triggering the '+' tab logic
            tabControlSearches.SelectionChanged -= tabControlSearches_SelectionChanged;

            try
            {
                int closedIndex = tabControlSearches.Items.IndexOf(tabItem);
                int selectedIndex = tabControlSearches.SelectedIndex;

                tabControlSearches.Items.Remove(tabItem);

                // If the closed tab was selected, we need to select a valid tab
                if (selectedIndex == closedIndex)
                {
                    // Select the tab to the left (closedIndex - 1), or the first tab (0)
                    int newSelect = Math.Max(0, closedIndex - 1);
                    // Ensure we don't select the plus tab if there is at least one search tab left
                    if (newSelect >= tabControlSearches.Items.Count - 1)
                    {
                        newSelect = tabControlSearches.Items.Count - 2;
                    }
                    if (newSelect >= 0)
                    {
                        tabControlSearches.SelectedIndex = newSelect;
                    }
                }
            }
            finally
            {
                // Re-attach SelectionChanged
                tabControlSearches.SelectionChanged += tabControlSearches_SelectionChanged;
            }
        }

        public static void SetConfigValue(Configuration config, string key, string value)
        {
            if (config.AppSettings.Settings[key] != null)
            {
                config.AppSettings.Settings[key].Value = value;
            }
            else
            {
                config.AppSettings.Settings.Add(key, value);
            }
        }

        public static void SaveGlobalConfig(Configuration config)
        {
            SetConfigValue(config, "Theme", CurrentTheme.ToString());
            SetConfigValue(config, "MultipleHighlightColors", MultipleHighlightColors.ToString());
            SetConfigValue(config, "MaxLineHighlights", MaxLineHighlights.ToString());
            SetConfigValue(config, "MaxSearchTerms", MaxSearchTerms.ToString());
            SetConfigValue(config, "FileViewerPath", FileViewerPath);
            SetConfigValue(config, "FileViewerArgs", FileViewerArgs);
        }

        private void OnClosing(object? sender, EventArgs e)
        {
            // Cancel searches in all tabs to clean up background processes (like suspended rg.exe)
            foreach (var item in tabControlSearches.Items)
            {
                if (item is TabItem tabItem && tabItem.Content is SearchTab tab)
                {
                    tab.CancelSearch();
                }
            }

            var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            if (WindowState != WindowState.Minimized)
            {
                SetConfigValue(config, "MainWindowLeft", Left.ToString());
                SetConfigValue(config, "MainWindowTop", Top.ToString());
                SetConfigValue(config, "MainWindowWidth", Width.ToString());
                SetConfigValue(config, "MainWindowHeight", Height.ToString());
                SetConfigValue(config, "MainWindowState", ((int)WindowState).ToString());
            }

            SaveGlobalConfig(config);

            // Save the settings from the active search tab
            if (tabControlSearches.SelectedItem is TabItem activeTab && activeTab.Content is SearchTab searchTab)
            {
                searchTab.SaveTabConfig(config);
            }

            try
            {
                config.Save();
                ConfigurationManager.RefreshSection("appSettings");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save configuration: {ex.Message}");
            }
        }
    }

    public class FileSearchResult
    {
        public string Path { get; }
        public string Filename { get; }

        public FileSearchResult(string path, string filename)
        {
            Path = path;
            Filename = filename;
        }
    }

    public class ResultLine
    {
        public int Line { get; }
        public string Content { get; }
        public string File { get; }
        public string Path { get; }

        public ResultLine(int line, string content, string file = "", string path = "")
        {
            Line = line;
            Content = content;
            File = file;
            Path = path;
        }
    }
}
