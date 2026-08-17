using Ookii.Dialogs.Wpf;
using Peter;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static rg_gui.RipGrepWrapper;
using FramePFX.Themes;

namespace rg_gui
{
    public partial class SearchTab : UserControl
    {
        // No history count limit — preserve all history entries
        private const int HIGHLIGHT_COLORS_COUNT = 10;

        private CancellationTokenSource? m_cancellationTokenSource;
        private readonly RipGrepWrapper m_ripGrepWrapper;

        public RangeObservableCollection<FileSearchResult> FileResultItems { get; } = new();
        public RangeObservableCollection<ResultLine> ResultLineItems { get; } = new();

        private string[] m_folderSuggestionValues = Array.Empty<string>();
        private string m_currentInput = string.Empty;
        private string m_currentSuggestion = string.Empty;
        private string m_currentText = string.Empty;
        private int m_selectionStart;
        private int m_selectionLength;

        public SearchTab(string? basePath = null, string? includeFiles = null, string? excludeFiles = null, string? containingText = null)
        {
            InitializeComponent();

            var ripgrepPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty, "rg.exe");
            if (!File.Exists(ripgrepPath))
            {
                MessageBox.Show("rg.exe not found in installation path.", "Error");
                throw new Exception("rg.exe not found in installation path.");
            }

            m_ripGrepWrapper = new RipGrepWrapper(ripgrepPath);
            m_ripGrepWrapper.FileFound += OnFileAdded;
            m_ripGrepWrapper.LineFound += OnLineFound;

            // Load initial config values from local portable configuration file path
            var exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rg-gui.config");
            var fileMap = new ExeConfigurationFileMap { ExeConfigFilename = exePath };
            var config = ConfigurationManager.OpenMappedExeConfiguration(fileMap, ConfigurationUserLevel.None);
            chkCaseSensitive.IsChecked = bool.TryParse(config.AppSettings.Settings["CaseSensitive"]?.Value, out var caseSensitive) ? caseSensitive : MainWindow.DEFAULT_CASESENSITIVE;
            chkRecursive.IsChecked = bool.TryParse(config.AppSettings.Settings["Recursive"]?.Value, out var recursive) ? recursive : MainWindow.DEFAULT_RECURSIVE;
            chkRegularExpression.IsChecked = bool.TryParse(config.AppSettings.Settings["RegularExpression"]?.Value, out var regularExpression) ? regularExpression : MainWindow.DEFAULT_REGULAREXPRESSION;
            chkShowAllLines.IsChecked = bool.TryParse(config.AppSettings.Settings["ShowAllLines"]?.Value, out var showAllLines) ? showAllLines : MainWindow.DEFAULT_SHOWALLLINES;

            var fileEncoding = cmbEncoding.FindName(config.AppSettings.Settings["FileEncoding"]?.Value ?? MainWindow.DEFAULT_FILEENCODING);
            if (fileEncoding != null)
            {
                cmbEncoding.SelectedItem = fileEncoding;
            }
            else
            {
                cmbEncoding.SelectedIndex = 0;
            }

            txtMaxFileSize.Text = (int.TryParse(config.AppSettings.Settings["MaxFileSize"]?.Value, out var maxFileSize) ? maxFileSize : MainWindow.DEFAULT_MAXFILESIZE).ToString();
            var maxFileSizeUnit = cmbFileSizeUnit.FindName(config.AppSettings.Settings["MaxFileSizeUnit"]?.Value ?? MainWindow.DEFAULT_MAXFILESIZEUNIT);
            if (maxFileSizeUnit != null)
            {
                cmbFileSizeUnit.SelectedItem = maxFileSizeUnit;
            }
            else
            {
                cmbFileSizeUnit.SelectedIndex = 0;
            }

            var contextLines = int.TryParse(config.AppSettings.Settings["ContextLines"]?.Value, out var cLines) ? cLines : 0;
            SetContextLines(contextLines);

            chkComboMode.IsChecked = bool.TryParse(config.AppSettings.Settings["ComboMode"]?.Value, out var comboMode) ? comboMode : false;

            // 1. Load history lists (sets selection to default first item)
            LoadHistory(cmbBasePath, "HistoryBasePath");
            LoadHistory(cmbIncludeFiles, "HistoryIncludeFiles", new[] { "*.*" });
            LoadHistory(cmbExcludeFiles, "HistoryExcludeFiles", new[] { "*.exe|*.dll|*.so|*.bin|*.iso" });
            LoadHistory(cmbContainingText, "HistoryContainingText");

            // 2. Set the active text fields (overriding any defaults with config or command-line parameters)
            var configBasePath = config.AppSettings.Settings["BasePath"]?.Value ?? MainWindow.DEFAULT_BASEPATH;
            var configIncludeFiles = config.AppSettings.Settings["IncludeFiles"]?.Value ?? MainWindow.DEFAULT_INCLUDEFILES;
            var configExcludeFiles = config.AppSettings.Settings["ExcludeFiles"]?.Value ?? MainWindow.DEFAULT_EXCLUDEFILES;
            var configContainingText = config.AppSettings.Settings["ContainingText"]?.Value ?? MainWindow.DEFAULT_CONTAININGTEXT;

            SetComboBoxActiveText(cmbBasePath, basePath ?? configBasePath);
            SetComboBoxActiveText(cmbIncludeFiles, includeFiles ?? configIncludeFiles);
            SetComboBoxActiveText(cmbExcludeFiles, excludeFiles ?? configExcludeFiles);
            SetComboBoxActiveText(cmbContainingText, containingText ?? configContainingText);
        }

        private int GetContextLines()
        {
            if (cmbContextLines == null) return 0;
            if (cmbContextLines.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                if (int.TryParse(item.Tag.ToString(), out int tagVal)) return Math.Max(0, tagVal);
            }
            if (int.TryParse(cmbContextLines.Text.Trim(), out int val)) return Math.Max(0, val);
            return 0;
        }

        private void SetContextLines(int lines)
        {
            if (cmbContextLines == null) return;
            foreach (var item in cmbContextLines.Items)
            {
                if (item is ComboBoxItem cbi && cbi.Tag?.ToString() == lines.ToString())
                {
                    cmbContextLines.SelectedItem = cbi;
                    return;
                }
            }
            cmbContextLines.Text = lines.ToString();
        }

        private static void SetComboBoxActiveText(ComboBox comboBox, string text)
        {
            var list = comboBox.ItemsSource as List<string> ?? new List<string>();
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (!list.Contains(text))
                {
                    list.Insert(0, text);
                    comboBox.ItemsSource = null;
                    comboBox.ItemsSource = list;
                }
            }
            comboBox.Text = text;
        }

        public void CancelSearch()
        {
            try
            {
                m_ripGrepWrapper?.Resume();
                m_cancellationTokenSource?.Cancel();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during CancelSearch: {ex.Message}");
            }
        }

        private void LoadHistory(ComboBox comboBox, string configKey, string[]? defaultItems = null)
        {
            try
            {
                var exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rg-gui.config");
                var fileMap = new ExeConfigurationFileMap { ExeConfigFilename = exePath };
                var config = ConfigurationManager.OpenMappedExeConfiguration(fileMap, ConfigurationUserLevel.None);
                var historyStr = config.AppSettings.Settings[configKey]?.Value;
                var items = new List<string>();

                if (!string.IsNullOrEmpty(historyStr))
                {
                    items.AddRange(historyStr.Split('|', StringSplitOptions.RemoveEmptyEntries));
                }
                else if (defaultItems != null)
                {
                    items.AddRange(defaultItems);
                }

                comboBox.ItemsSource = items;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading history for {configKey}: {ex.Message}");
            }
        }

        private void SaveHistory(Configuration config, ComboBox comboBox, string configKey)
        {
            try
            {
                var currentText = comboBox.Text;
                var list = new List<string>();

                if (comboBox.ItemsSource is IEnumerable<string> items)
                {
                    list.AddRange(items);
                }
                else if (comboBox.Items.Count > 0)
                {
                    foreach (var item in comboBox.Items)
                    {
                        if (item != null)
                        {
                            list.Add(item.ToString() ?? string.Empty);
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(currentText))
                {
                    list.Remove(currentText);
                    list.Insert(0, currentText);
                }

                list = list.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();



                comboBox.ItemsSource = list;
                comboBox.Text = currentText;

                var historyStr = string.Join('|', list);
                MainWindow.SetConfigValue(config, configKey, historyStr);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving history for {configKey}: {ex.Message}");
            }
        }

        private void LoadHistoryFromRegistry(ComboBox comboBox, Microsoft.Win32.RegistryKey profileKey, string valueName)
        {
            try
            {
                var val = profileKey.GetValue(valueName);
                if (val == null) return;

                var list = new List<string>();
                if (val is string[] array)
                {
                    foreach (var s in array)
                    {
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            list.Add(s.Trim());
                        }
                    }
                }
                else if (val is string str && !string.IsNullOrWhiteSpace(str))
                {
                    list.Add(str.Trim());
                }

                list = list.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();

                if (list.Count > 0)
                {
                    comboBox.ItemsSource = list;
                    comboBox.Text = list[0];
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading history from registry for {valueName}: {ex.Message}");
            }
        }

        public void SaveTabConfig(Configuration config)
        {
            MainWindow.SetConfigValue(config, "BasePath", cmbBasePath.Text);
            MainWindow.SetConfigValue(config, "IncludeFiles", cmbIncludeFiles.Text);
            MainWindow.SetConfigValue(config, "ExcludeFiles", cmbExcludeFiles.Text);
            MainWindow.SetConfigValue(config, "ContainingText", cmbContainingText.Text);
            MainWindow.SetConfigValue(config, "CaseSensitive", (chkCaseSensitive.IsChecked ?? MainWindow.DEFAULT_CASESENSITIVE).ToString());
            MainWindow.SetConfigValue(config, "Recursive", (chkRecursive.IsChecked ?? MainWindow.DEFAULT_RECURSIVE).ToString());
            MainWindow.SetConfigValue(config, "RegularExpression", (chkRegularExpression.IsChecked ?? MainWindow.DEFAULT_REGULAREXPRESSION).ToString());
            MainWindow.SetConfigValue(config, "ShowAllLines", (chkShowAllLines.IsChecked ?? MainWindow.DEFAULT_SHOWALLLINES).ToString());

            MainWindow.SetConfigValue(config, "FileEncoding", (cmbEncoding.SelectedItem as ComboBoxItem)?.Name ?? MainWindow.DEFAULT_FILEENCODING);
            MainWindow.SetConfigValue(config, "MaxFileSize", txtMaxFileSize.Text);
            MainWindow.SetConfigValue(config, "MaxFileSizeUnit", (cmbFileSizeUnit.SelectedItem as ComboBoxItem)?.Name ?? MainWindow.DEFAULT_MAXFILESIZEUNIT);
            MainWindow.SetConfigValue(config, "ContextLines", GetContextLines().ToString());
            MainWindow.SetConfigValue(config, "ComboMode", (chkComboMode.IsChecked ?? false).ToString());

            SaveHistory(config, cmbBasePath, "HistoryBasePath");
            SaveHistory(config, cmbIncludeFiles, "HistoryIncludeFiles");
            SaveHistory(config, cmbExcludeFiles, "HistoryExcludeFiles");
            SaveHistory(config, cmbContainingText, "HistoryContainingText");
        }

        private void LoadFileSeekSettings(bool force = false)
        {
            // Automatic FileSeek import disabled. FileSeek settings are only imported when manually clicking the import button in SettingsWindow.
        }

        private void OnFileAdded(object? sender, (string path, string filename) result)
        {
            Application.Current.Dispatcher.Invoke(delegate
            {
                if (!FileResultItems.Any(x => x.Path == result.path && x.Filename == result.filename))
                {
                    FileResultItems.Add(new FileSearchResult(result.path, result.filename));
                    txtFileListStatus.Text = $"Found {FileResultItems.Count} files.";
                }
            });
        }

        private void OnLineFound(object? sender, (string path, string filename, int lineNumber, string lineContent, IEnumerable<TermResult> termResults) e)
        {
            if (!IsLoaded) return;
            if (chkShowAllLines == null) return;
            if (chkShowAllLines.IsChecked != true) return;

            try
            {
                Application.Current.Dispatcher.Invoke(delegate
                {
                    ResultLineItems.Add(new ResultLine(
                        e.lineNumber,
                        GetColorizedString(e.lineContent, e.termResults).Trim(),
                        e.filename,
                        e.path
                    ));
                    txtResultLineStatus.Text = $"{ResultLineItems.Count} lines matched.";
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in OnLineFound: {ex.Message}");
            }
        }

        private void gridFileResults_MouseDown(object? sender, MouseEventArgs e)
        {
            if ((e.RightButton == MouseButtonState.Pressed && !SystemParameters.SwapButtons) || (e.LeftButton == MouseButtonState.Pressed && SystemParameters.SwapButtons))
            {
                var point = PointToScreen(e.MouseDevice.GetPosition(this));
                var hitTestResult = VisualTreeHelper.HitTest(gridFileResults, e.MouseDevice.GetPosition(gridFileResults));

                if (hitTestResult?.VisualHit is FrameworkElement element && element.DataContext is FileSearchResult fileSearchResult)
                {
                    var selectedFiles = gridFileResults.SelectedItems.Cast<FileSearchResult>().Select(x => new FileInfo(Path.Combine(x.Path, x.Filename))).ToList();

                    if (!selectedFiles.Any(f => f.FullName == Path.Combine(fileSearchResult.Path, fileSearchResult.Filename)))
                    {
                        selectedFiles.Clear();
                        selectedFiles.Add(new FileInfo(Path.Combine(fileSearchResult.Path, fileSearchResult.Filename)));
                    }

                    var shellContextMenu = new ShellContextMenu();
                    shellContextMenu.ShowContextMenu(selectedFiles, new System.Drawing.Point((int)point.X, (int)point.Y));
                }
            }
        }

        private void gridFileResults_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (chkShowAllLines == null) return;
            if (chkShowAllLines.IsChecked == true)
            {
                return;
            }

            try
            {
                if (e.AddedItems.Count > 0)
                {
                    if (e.AddedItems[0] is FileSearchResult addedItem)
                    {
                        GetScrollViewer(gridResultLines)?.ScrollToLeftEnd();
                        var fileItems = m_ripGrepWrapper.FileResults.Where(x => x.Key.path == addedItem.Path && x.Key.filename == addedItem.Filename);
                        var displayLines = BuildDisplayLines(fileItems);
                        ResultLineItems.Reset(displayLines);
                        txtResultLineStatus.Text = $"{ResultLineItems.Count} lines matched.";
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in gridFileResults_SelectionChanged: {ex.Message}");
            }
        }

        private void grid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A)
            {
                if (sender is DataGrid grid)
                {
                    grid.Focus();
                    grid.SelectAll();
                    e.Handled = true;
                }
                return;
            }

            if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.C)
            {
                copySelectedAsCombo_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.E)
            {
                OpenContainingFolder();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F3 || e.Key == Key.Enter)
            {
                OpenFileViewer();
                e.Handled = true;
                return;
            }
        }

        private void gridResultLines_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                OpenFileViewer();
                e.Handled = true;
            }
        }

        private void grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row)
            {
                row.IsSelected = true;
                row.Focus();
            }
        }

        private void grid_RequestBringIntoViewHandler(object sender, RequestBringIntoViewEventArgs e)
        {
            e.Handled = true;
        }

        private static ScrollViewer? GetScrollViewer(UIElement? element)
        {
            if (element == null) return null;
            ScrollViewer? result = null;
            for (var i = 0; result == null && i < VisualTreeHelper.GetChildrenCount(element); i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);
                if (child is ScrollViewer scrollViewer)
                {
                    result = scrollViewer;
                }
                else
                {
                    result = GetScrollViewer(child as UIElement);
                }
            }
            return result;
        }

        private async void btnStart_Click(object sender, RoutedEventArgs e)
        {
            var rawInput = cmbBasePath.Text?.Trim() ?? string.Empty;
            List<string> inputPaths = new List<string>();

            if (Directory.Exists(rawInput) || File.Exists(rawInput))
            {
                inputPaths.Add(rawInput);
            }
            else
            {
                // Extract quoted paths or paths separated by semicolon, pipe or spaces (e.g. drive letter paths F:\... E:\...)
                var matches = Regex.Matches(rawInput, @"""[^""]+""|[A-Za-z]:\\[^;""|]+");
                foreach (Match m in matches)
                {
                    var clean = m.Value.Trim(' ', '"');
                    if (Directory.Exists(clean) || File.Exists(clean))
                    {
                        inputPaths.Add(clean);
                    }
                }

                if (inputPaths.Count == 0)
                {
                    var tokens = rawInput.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var t in tokens)
                    {
                        var clean = t.Trim(' ', '"');
                        if (Directory.Exists(clean) || File.Exists(clean))
                        {
                            inputPaths.Add(clean);
                        }
                    }
                }
            }

            if (inputPaths.Count == 0)
            {
                MessageBox.Show("Invalid \"In Folder\" path.", "Error");
                return;
            }

            if (m_cancellationTokenSource != null)
            {
                return;
            }

            var searchTerms = Regex.Matches(cmbContainingText.Text, @"""[^""\\]*(?:\\.[^""\\]*)*""|[^\s""]+");
            if (searchTerms.Count < 1)
            {
                return;
            }

            if (MainWindow.MaxSearchTerms > 0 && searchTerms.Count > MainWindow.MaxSearchTerms)
            {
                MessageBox.Show($"Search text contains more than {MainWindow.MaxSearchTerms} terms.");
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            btnStart.IsEnabled = false;
            btnCancel.IsEnabled = true;
            btnPause.IsEnabled = true;
            btnPause.Content = "Pause";
            var cancellationTokenSource = new CancellationTokenSource();
            m_cancellationTokenSource = cancellationTokenSource;

            ResultLineItems.Reset(Enumerable.Empty<ResultLine>());
            txtFileListStatus.Text = string.Empty;
            txtResultLineStatus.Text = string.Empty;

            m_ripGrepWrapper.Clear();

            var startPath = cmbBasePath.Text;
            if (startPath.EndsWith(Path.DirectorySeparatorChar))
            {
                startPath = startPath.TrimEnd(Path.DirectorySeparatorChar);
            }

            // Save history and current active tab values to local portable config
            var exePathConfig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rg-gui.config");
            var fileMapConfig = new ExeConfigurationFileMap { ExeConfigFilename = exePathConfig };
            var exeConfig = ConfigurationManager.OpenMappedExeConfiguration(fileMapConfig, ConfigurationUserLevel.None);
            SaveTabConfig(exeConfig);
            try
            {
                exeConfig.Save(ConfigurationSaveMode.Modified, true);
                ConfigurationManager.RefreshSection("appSettings");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving search history: {ex.Message}");
            }

            try
            {
                int effContextLines = GetContextLines();
                if (chkComboMode.IsChecked == true && effContextLines < 2)
                {
                    effContextLines = 2;
                }

                var searchParameters = new SearchParameters
                {
                    StartPath = startPath,
                    SearchStrings = searchTerms.Cast<Match>().Select(x => x.Value),
                    IgnoreCase = !(chkCaseSensitive.IsChecked ?? false),
                    Recursive = chkRecursive.IsChecked ?? true,
                    IncludePatterns = cmbIncludeFiles.Text,
                    ExcludePatterns = cmbExcludeFiles.Text,
                    RegularExpression = chkRegularExpression.IsChecked ?? false,
                    Encoding = (FileEncoding)cmbEncoding.SelectedIndex,
                    MaxFileSize = int.Parse(txtMaxFileSize.Text),
                    MaxFileSizeUnit = (MaxFileSizeUnit)cmbFileSizeUnit.SelectedIndex,
                    ContextLines = effContextLines,
                };

                FileResultItems.Reset(Enumerable.Empty<FileSearchResult>());
                ResultLineItems.Reset(Enumerable.Empty<ResultLine>());

                await m_ripGrepWrapper.Search(searchParameters, cancellationTokenSource.Token);
            }
            finally
            {
                btnCancel.IsEnabled = false;
                btnStart.IsEnabled = true;
                btnPause.IsEnabled = false;
                btnPause.Content = "Pause";
                m_ripGrepWrapper.Resume();

                m_cancellationTokenSource = null;
                cancellationTokenSource.Cancel();
            }

            stopwatch.Stop();
            txtFileListStatus.Text = $"Found {FileResultItems.Count} files.  Took {stopwatch.Elapsed.TotalSeconds:0.00} seconds.";

            if (chkShowAllLines.IsChecked == true)
            {
                PopulateAllLines();
            }
            else if (FileResultItems.Count > 0 && gridFileResults.SelectedIndex < 0)
            {
                gridFileResults.SelectedIndex = 0;
            }
        }

        private void btnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new VistaFolderBrowserDialog()
            {
                Description = "Select folder",
                UseDescriptionForTitle = true,
                Multiselect = false
            };

            if (dialog.ShowDialog(Window.GetWindow(this)).GetValueOrDefault())
            {
                cmbBasePath.Text = dialog.SelectedPath;
            }
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            CancelSearch();
        }

        private void btnPause_Click(object sender, RoutedEventArgs e)
        {
            if (m_ripGrepWrapper.IsPaused)
            {
                m_ripGrepWrapper.Resume();
                btnPause.Content = "Pause";
            }
            else
            {
                m_ripGrepWrapper.Pause();
                btnPause.Content = "Resume";
            }
        }



        private void chkShowAllLines_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (m_ripGrepWrapper == null) return;
            try
            {
                if (colFile != null)
                {
                    colFile.Visibility = Visibility.Visible;
                }
                PopulateAllLines();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in chkShowAllLines_Checked: {ex.Message}");
            }
        }

        private void chkShowAllLines_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (m_ripGrepWrapper == null) return;
            try
            {
                if (colFile != null)
                {
                    colFile.Visibility = Visibility.Collapsed;
                }
                if (gridFileResults != null && gridFileResults.SelectedItem is FileSearchResult selectedFile)
                {
                    ResultLineItems.Reset(Enumerable.Empty<ResultLine>());
                    var lineResults = m_ripGrepWrapper.FileResults.Where(x => x.Key.path == selectedFile.Path && x.Key.filename == selectedFile.Filename);
                    foreach (var lineResult in lineResults)
                    {
                        ResultLineItems.Add(new ResultLine(lineResult.Key.lineNumber, GetColorizedString(lineResult.Value.LineContent, lineResult.Value.TermResults).Trim(), lineResult.Key.filename, lineResult.Key.path));
                    }
                    txtResultLineStatus.Text = $"{ResultLineItems.Count} lines matched.";
                }
                else
                {
                    ResultLineItems.Reset(Enumerable.Empty<ResultLine>());
                    txtResultLineStatus.Text = string.Empty;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in chkShowAllLines_Unchecked: {ex.Message}");
            }
        }

        private void selectAllLines_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                gridResultLines.Focus();
                gridResultLines.SelectAll();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in selectAllLines_Click: {ex.Message}");
            }
        }

        private List<ResultLine> BuildDisplayLines(IEnumerable<KeyValuePair<(string path, string filename, int lineNumber), LineResult>> items)
        {
            var sorted = items.OrderBy(x => x.Key.path).ThenBy(x => x.Key.filename).ThenBy(x => x.Key.lineNumber).ToList();

            if (chkComboMode.IsChecked != true)
            {
                return sorted.Select(x => new ResultLine(
                    x.Key.lineNumber,
                    GetColorizedString(x.Value.LineContent, x.Value.TermResults).Trim(),
                    x.Key.filename,
                    x.Key.path
                )).ToList();
            }

            var resultList = new List<ResultLine>();
            int i = 0;
            while (i < sorted.Count)
            {
                var current = sorted[i];

                if (ComboHelper.TryExtractComboBlock(sorted, i, out var comboText, out var mergedTerms, out int consumedCount))
                {
                    resultList.Add(new ResultLine(
                        current.Key.lineNumber,
                        GetColorizedString(comboText, mergedTerms).Trim(),
                        current.Key.filename,
                        current.Key.path
                    ));
                    i += consumedCount;
                }
                else
                {
                    string cleanContent = current.Value.LineContent.Trim();
                    if (!string.IsNullOrWhiteSpace(cleanContent) && current.Value.TermResults.Count > 0)
                    {
                        resultList.Add(new ResultLine(
                            current.Key.lineNumber,
                            GetColorizedString(current.Value.LineContent, current.Value.TermResults).Trim(),
                            current.Key.filename,
                            current.Key.path
                        ));
                    }
                    i++;
                }
            }

            return resultList;
        }

        private void chkComboMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (m_ripGrepWrapper.FileResults.Count > 0)
            {
                if (chkShowAllLines.IsChecked == true)
                {
                    PopulateAllLines();
                }
                else if (gridFileResults.SelectedItem is FileSearchResult selectedFile)
                {
                    var fileItems = m_ripGrepWrapper.FileResults.Where(x => x.Key.path == selectedFile.Path && x.Key.filename == selectedFile.Filename);
                    var displayLines = BuildDisplayLines(fileItems);
                    ResultLineItems.Reset(displayLines);
                    txtResultLineStatus.Text = $"{ResultLineItems.Count} lines matched.";
                }
            }
        }

        private void PopulateAllLines()
        {
            try
            {
                var displayLines = BuildDisplayLines(m_ripGrepWrapper.FileResults);
                ResultLineItems.Reset(displayLines);
                txtResultLineStatus.Text = $"{ResultLineItems.Count} lines matched.";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in PopulateAllLines: {ex.Message}");
            }
        }

        private void SetClipboardTextWithRetry(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return;
                }
                catch (COMException ex) when ((uint)ex.ErrorCode == 0x800401D0)
                {
                    Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to copy to clipboard: {ex.Message}");
                    break;
                }
            }
        }

        private void copySelectedLines_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var stringBuilder = new StringBuilder();
                foreach (var item in gridResultLines.Items)
                {
                    if (gridResultLines.SelectedItems.Contains(item) && item is ResultLine resultLine)
                    {
                        var cleanContent = Regex.Replace(resultLine.Content ?? "", @"</?c\d+>", "");
                        stringBuilder.AppendLine(cleanContent);
                    }
                }
                if (stringBuilder.Length > 0)
                {
                    SetClipboardTextWithRetry(stringBuilder.ToString());
                    MessageBox.Show("Copiado al portapapeles.", "Copiado", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in copySelectedLines_Click: {ex.Message}");
            }
        }

        private void copyAllLines_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var stringBuilder = new StringBuilder();
                foreach (var item in gridResultLines.Items)
                {
                    if (item is ResultLine resultLine)
                    {
                        var cleanContent = Regex.Replace(resultLine.Content ?? "", @"</?c\d+>", "");
                        stringBuilder.AppendLine(cleanContent);
                    }
                }
                if (stringBuilder.Length > 0)
                {
                    SetClipboardTextWithRetry(stringBuilder.ToString());
                    MessageBox.Show("Copiado al portapapeles.", "Copiado", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in copyAllLines_Click: {ex.Message}");
            }
        }

        private void copySelectedAsCombo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (gridResultLines.SelectedItems.Count > 0)
                {
                    var rawLines = new List<string>();
                    foreach (var item in gridResultLines.SelectedItems)
                    {
                        if (item is ResultLine resultLine)
                        {
                            var clean = Regex.Replace(resultLine.Content ?? "", @"</?c\d+>", "").Trim();
                            rawLines.Add(clean);
                        }
                    }

                    var combos = ComboHelper.ExtractCombosFromStrings(rawLines);
                    if (combos.Count == 0)
                    {
                        combos = rawLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                    }

                    if (combos.Count > 0)
                    {
                        var text = string.Join(Environment.NewLine, combos);
                        SetClipboardTextWithRetry(text);
                        MessageBox.Show($"Se copiaron {combos.Count} combos al portapapeles.", "Copiado", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error copying selected combos: {ex.Message}");
            }
        }

        private void copyAllAsCombo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var combos = ComboHelper.ExtractAllCombos(m_ripGrepWrapper.FileResults);
                if (combos.Count == 0 && ResultLineItems.Count > 0)
                {
                    var rawLines = ResultLineItems.Select(x => Regex.Replace(x.Content ?? "", @"</?c\d+>", "").Trim());
                    combos = ComboHelper.ExtractCombosFromStrings(rawLines);
                }

                if (combos.Count > 0)
                {
                    var text = string.Join(Environment.NewLine, combos);
                    SetClipboardTextWithRetry(text);
                    MessageBox.Show($"Se copiaron {combos.Count} combos al portapapeles.", "Copiado", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("No se encontraron credenciales para armar combos.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error copying all combos: {ex.Message}");
            }
        }

        private void exportCombosToTxt_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog()
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = "combos.txt"
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            {
                try
                {
                    var combos = ComboHelper.ExtractAllCombos(m_ripGrepWrapper.FileResults);
                    if (combos.Count == 0 && ResultLineItems.Count > 0)
                    {
                        var rawLines = ResultLineItems.Select(x => Regex.Replace(x.Content ?? "", @"</?c\d+>", "").Trim());
                        combos = ComboHelper.ExtractCombosFromStrings(rawLines);
                    }

                    using var writer = new StreamWriter(dialog.FileName, false, Encoding.UTF8);
                    foreach (var combo in combos)
                    {
                        writer.WriteLine(combo);
                    }

                    MessageBox.Show($"Se exportaron {combos.Count} combos con éxito.", "Exportar Combos", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error al exportar: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void exportAllLinesToCsv_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog()
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = "search_results.csv"
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            {
                try
                {
                    using var writer = new StreamWriter(dialog.FileName, false, Encoding.UTF8);
                    
                    if (chkShowAllLines.IsChecked == true)
                    {
                        writer.WriteLine("Archivo,Línea,Contenido");
                    }
                    else
                    {
                        writer.WriteLine("Línea,Contenido");
                    }

                    foreach (var item in gridResultLines.Items)
                    {
                        if (item is ResultLine resultLine)
                        {
                            string cleanContent = Regex.Replace(resultLine.Content ?? "", @"</?c\d+>", "");
                            string escapedContent = cleanContent.Replace("\"", "\"\"");
                            if (chkShowAllLines.IsChecked == true)
                            {
                                string escapedFile = (resultLine.File ?? "").Replace("\"", "\"\"");
                                writer.WriteLine($"\"{escapedFile}\",{resultLine.Line},\"{escapedContent}\"");
                            }
                            else
                            {
                                writer.WriteLine($"{resultLine.Line},\"{escapedContent}\"");
                            }
                        }
                    }
                    MessageBox.Show("Resultados exportados con éxito.", "Exportar");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error al exportar: " + ex.Message, "Error");
                }
            }
        }

        private void openInFileViewer_Click(object sender, RoutedEventArgs e)
        {
            OpenFileViewer();
        }

        private void openContainingFolder_Click(object sender, RoutedEventArgs e)
        {
            OpenContainingFolder();
        }

        private string GetColorizedString(string source, IEnumerable<TermResult> termResults)
        {
            var segmentEdges = new List<int>();
            foreach (var termResult in termResults)
            {
                segmentEdges.Add(termResult.Start);
                segmentEdges.Add(termResult.End + 1);
            }
            var sortedEdges = segmentEdges.Distinct().OrderBy(x => x).ToList();

            var highlightResults = new List<TermResult>();
            TermResult? previous = null;

            for (var i = 0; i < sortedEdges.Count - 1 && highlightResults.Count < MainWindow.MaxLineHighlights; i++)
            {
                var segmentStart = sortedEdges[i];
                var segmentEnd = sortedEdges[i + 1] - 1;

                var termResult = termResults.Where(x => segmentStart >= x.Start && segmentEnd <= x.End).OrderBy(x => x.TermIndex).FirstOrDefault();
                if (termResult != null)
                {
                    if (previous?.End == segmentStart - 1 && previous?.TermIndex == termResult.TermIndex)
                    {
                        previous.End = segmentEnd;
                    }
                    else
                    {
                        previous = new TermResult(segmentStart, segmentEnd, termResult.TermIndex);
                        highlightResults.Add(previous);
                    }
                }
            }

            var stringBuilder = new StringBuilder();
            var startingIndex = 0;
            foreach (var highlightResult in highlightResults)
            {
                if (startingIndex != highlightResult.Start)
                {
                    stringBuilder.Append(EscapeString(source.Substring(startingIndex, highlightResult.Start - startingIndex)));
                }

                var colorIndex = MainWindow.MultipleHighlightColors ? highlightResult.TermIndex % HIGHLIGHT_COLORS_COUNT : 0;

                stringBuilder.Append($"<c{colorIndex}>");
                stringBuilder.Append(EscapeString(source.Substring(highlightResult.Start, highlightResult.End - highlightResult.Start + 1)));
                stringBuilder.Append($"</c{colorIndex}>");

                startingIndex = highlightResult.End + 1;
            }

            stringBuilder.Append(EscapeString(source.Substring(startingIndex)));
            return stringBuilder.ToString();
        }

        private static string EscapeString(string source)
        {
            return source.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private string? GetSelectedResultLineFullPath(out int lineNumber)
        {
            lineNumber = 1;
            if (gridResultLines.SelectedItems.Count > 0 && gridResultLines.SelectedItems[gridResultLines.SelectedItems.Count - 1] is ResultLine resultLine)
            {
                lineNumber = resultLine.Line;
                if (!string.IsNullOrEmpty(resultLine.Path) && !string.IsNullOrEmpty(resultLine.File))
                {
                    return Path.Combine(resultLine.Path, resultLine.File);
                }
            }

            if (gridFileResults.SelectedItems.Count > 0 && gridFileResults.SelectedItems[gridFileResults.SelectedItems.Count - 1] is FileSearchResult resultFile)
            {
                if (!string.IsNullOrEmpty(resultFile.Path) && !string.IsNullOrEmpty(resultFile.Filename))
                {
                    return Path.Combine(resultFile.Path, resultFile.Filename);
                }
            }

            return null;
        }

        private void OpenFileViewer()
        {
            var fullPath = GetSelectedResultLineFullPath(out int lineNumber);
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
            {
                return;
            }

            try
            {
                if (!string.IsNullOrEmpty(MainWindow.FileViewerPath) && File.Exists(MainWindow.FileViewerPath))
                {
                    string rawArgs = MainWindow.FileViewerArgs ?? "$FILE";
                    string fileArg = $"\"{fullPath.Trim('\"')}\"";
                    string args;

                    if (rawArgs.Contains("\"$FILE\""))
                    {
                        args = rawArgs.Replace("\"$FILE\"", fileArg);
                    }
                    else if (rawArgs.Contains("$FILE"))
                    {
                        args = rawArgs.Replace("$FILE", fileArg);
                    }
                    else
                    {
                        args = $"{rawArgs} {fileArg}";
                    }

                    args = args.Replace("$LINE", lineNumber.ToString());

                    var processStartInfo = new ProcessStartInfo()
                    {
                        FileName = MainWindow.FileViewerPath,
                        Arguments = args,
                        UseShellExecute = false
                    };

                    using var process = Process.Start(processStartInfo);
                }
                else
                {
                    // Open with default associated application
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = fullPath,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenContainingFolder()
        {
            var fullPath = GetSelectedResultLineFullPath(out _);
            if (string.IsNullOrEmpty(fullPath))
            {
                return;
            }

            try
            {
                if (File.Exists(fullPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{fullPath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    var dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"\"{dir}\"",
                            UseShellExecute = true
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow
            {
                Owner = Window.GetWindow(this),
                Theme = MainWindow.CurrentTheme.GetName(),
                MaxSearchTerms = MainWindow.MaxSearchTerms,
                Multicolor = MainWindow.MultipleHighlightColors,
                MaxLineHighlights = MainWindow.MaxLineHighlights,
                MaxParallelProcesses = MainWindow.MaxParallelProcesses,
                FileViewerPath = MainWindow.FileViewerPath,
                FileViewerArgs = MainWindow.FileViewerArgs
            };

            if (settingsWindow.ShowDialog() == true)
            {
                MainWindow.CurrentTheme = Enum.Parse<ThemeType>(settingsWindow.Theme);
                ThemesController.SetTheme(MainWindow.CurrentTheme);
                MainWindow.MaxSearchTerms = settingsWindow.MaxSearchTerms;
                MainWindow.MultipleHighlightColors = settingsWindow.Multicolor;
                MainWindow.MaxLineHighlights = settingsWindow.MaxLineHighlights;
                MainWindow.MaxParallelProcesses = settingsWindow.MaxParallelProcesses;
                MainWindow.FileViewerPath = settingsWindow.FileViewerPath;
                MainWindow.FileViewerArgs = settingsWindow.FileViewerArgs;

                var exePathConfig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rg-gui.config");
                var fileMapConfig = new ExeConfigurationFileMap { ExeConfigFilename = exePathConfig };
                var config = ConfigurationManager.OpenMappedExeConfiguration(fileMapConfig, ConfigurationUserLevel.None);
                MainWindow.SaveGlobalConfig(config);
                try
                {
                    config.Save(ConfigurationSaveMode.Modified, true);
                    ConfigurationManager.RefreshSection("appSettings");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to save config in settings click: {ex.Message}");
                }

                // Preserve current user inputs before reloading history items
                var activeBasePath = cmbBasePath.Text;
                var activeInclude = cmbIncludeFiles.Text;
                var activeExclude = cmbExcludeFiles.Text;
                var activeContaining = cmbContainingText.Text;

                // Force reload history items in the current tab UI
                LoadHistory(cmbBasePath, "HistoryBasePath");
                LoadHistory(cmbIncludeFiles, "HistoryIncludeFiles", new[] { "*.*" });
                LoadHistory(cmbExcludeFiles, "HistoryExcludeFiles", new[] { "*.exe|*.dll|*.so|*.bin|*.iso" });
                LoadHistory(cmbContainingText, "HistoryContainingText");

                // Restore active user input texts so they are not wiped
                if (!string.IsNullOrEmpty(activeBasePath)) cmbBasePath.Text = activeBasePath;
                else
                {
                    var configBasePath = config.AppSettings.Settings["BasePath"]?.Value;
                    if (!string.IsNullOrEmpty(configBasePath)) cmbBasePath.Text = configBasePath;
                }

                if (!string.IsNullOrEmpty(activeInclude)) cmbIncludeFiles.Text = activeInclude;
                else
                {
                    var configIncludeFiles = config.AppSettings.Settings["IncludeFiles"]?.Value;
                    if (!string.IsNullOrEmpty(configIncludeFiles)) cmbIncludeFiles.Text = configIncludeFiles;
                }

                if (!string.IsNullOrEmpty(activeExclude)) cmbExcludeFiles.Text = activeExclude;
                else
                {
                    var configExcludeFiles = config.AppSettings.Settings["ExcludeFiles"]?.Value;
                    if (!string.IsNullOrEmpty(configExcludeFiles)) cmbExcludeFiles.Text = configExcludeFiles;
                }

                if (!string.IsNullOrEmpty(activeContaining)) cmbContainingText.Text = activeContaining;
                else
                {
                    var configContainingText = config.AppSettings.Settings["ContainingText"]?.Value;
                    if (!string.IsNullOrEmpty(configContainingText)) cmbContainingText.Text = configContainingText;
                }
            }
        }

        private void txtContainingText_OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                e.Handled = true;
                if (btnStart.IsEnabled)
                {
                    btnStart.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            }
        }

        private void txtBasePath_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (cmbBasePath == null) return;
            UpdateFolderSuggestionValues();
            var input = cmbBasePath.Text;
            if (input.Length > m_currentInput.Length && input != m_currentSuggestion)
            {
                m_currentSuggestion = m_folderSuggestionValues.FirstOrDefault(x => x.StartsWith(input, StringComparison.CurrentCultureIgnoreCase)) ?? string.Empty;
                if (!string.IsNullOrEmpty(m_currentSuggestion))
                {
                    m_currentText = m_currentSuggestion;
                    m_selectionStart = input.Length;
                    m_selectionLength = m_currentSuggestion.Length - input.Length;

                    cmbBasePath.Text = m_currentText;
                    // Try to find the internal textbox inside editable ComboBox to select
                    var textBox = cmbBasePath.Template.FindName("PART_EditableTextBox", cmbBasePath) as TextBox;
                    textBox?.Select(m_selectionStart, m_selectionLength);
                }
            }
            m_currentInput = input;
        }

        private void UpdateFolderSuggestionValues()
        {
            if (!IsLoaded) return;
            if (cmbBasePath == null) return;
            var input = cmbBasePath.Text;
            if (input.EndsWith(Path.DirectorySeparatorChar) && Directory.Exists(input))
            {
                try
                {
                    m_folderSuggestionValues = Directory.GetDirectories(input);
                }
                catch
                {
                    m_folderSuggestionValues = Array.Empty<string>();
                }
            }
        }

        private void txtMaxFileSize_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (txtMaxFileSize == null) return;
            var input = txtMaxFileSize.Text;
            txtMaxFileSize.Text = new string(input.Where(c => char.IsDigit(c)).ToArray());
        }

        private void cmbFileSizeUnit_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (txtMaxFileSize != null && cmbFileSizeUnit != null)
            {
                txtMaxFileSize.IsEnabled = (cmbFileSizeUnit.SelectedIndex != 0);
            }
        }
    }
}
