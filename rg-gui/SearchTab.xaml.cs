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
        private const int MAX_HISTORY_COUNT = 15;
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

        public SearchTab()
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

            // Load initial config values
            var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            cmbBasePath.Text = config.AppSettings.Settings["BasePath"]?.Value ?? MainWindow.DEFAULT_BASEPATH;
            cmbIncludeFiles.Text = config.AppSettings.Settings["IncludeFiles"]?.Value ?? MainWindow.DEFAULT_INCLUDEFILES;
            cmbExcludeFiles.Text = config.AppSettings.Settings["ExcludeFiles"]?.Value ?? MainWindow.DEFAULT_EXCLUDEFILES;
            cmbContainingText.Text = config.AppSettings.Settings["ContainingText"]?.Value ?? MainWindow.DEFAULT_CONTAININGTEXT;
            chkCaseSensitive.IsChecked = bool.TryParse(config.AppSettings.Settings["CaseSensitive"]?.Value, out var caseSensitive) ? caseSensitive : MainWindow.DEFAULT_CASESENSITIVE;
            chkRecursive.IsChecked = bool.TryParse(config.AppSettings.Settings["Recursive"]?.Value, out var recursive) ? recursive : MainWindow.DEFAULT_RECURSIVE;
            chkRegularExpression.IsChecked = bool.TryParse(config.AppSettings.Settings["RegularExpression"]?.Value, out var regularExpression) ? regularExpression : MainWindow.DEFAULT_REGULAREXPRESSION;

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

            // Load history lists
            LoadHistory(cmbBasePath, "HistoryBasePath");
            LoadHistory(cmbIncludeFiles, "HistoryIncludeFiles", new[] { "*.*" });
            LoadHistory(cmbExcludeFiles, "HistoryExcludeFiles", new[] { "*.exe|*.dll|*.so|*.bin|*.iso" });
            LoadHistory(cmbContainingText, "HistoryContainingText");

            bool hasLocalSettings = config.AppSettings.Settings["BasePath"] != null;
            LoadFileSeekSettings(force: !hasLocalSettings);
        }

        public void CancelSearch()
        {
            m_ripGrepWrapper?.Resume();
            m_cancellationTokenSource?.Cancel();
        }

        private void LoadHistory(ComboBox comboBox, string configKey, string[]? defaultItems = null)
        {
            try
            {
                var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
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
                if (items.Count > 0)
                {
                    comboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading history for {configKey}: {ex.Message}");
            }
        }

        private void SaveHistory(ComboBox comboBox, string configKey)
        {
            try
            {
                var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
                var currentText = comboBox.Text;
                var items = comboBox.ItemsSource as List<string> ?? new List<string>();

                var list = new List<string>(items);
                if (!string.IsNullOrEmpty(currentText))
                {
                    list.Remove(currentText);
                    list.Insert(0, currentText);
                }

                if (list.Count > MAX_HISTORY_COUNT)
                {
                    list = list.Take(MAX_HISTORY_COUNT).ToList();
                }

                comboBox.ItemsSource = list;
                comboBox.Text = currentText;

                var historyStr = string.Join('|', list);
                MainWindow.SetConfigValue(config, configKey, historyStr);
                config.Save();
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
                if (val is string[] array)
                {
                    var list = new List<string>(array);
                    comboBox.ItemsSource = list;
                    if (list.Count > 0)
                    {
                        comboBox.Text = list[0];
                    }
                }
                else if (val is string str)
                {
                    var list = new List<string> { str };
                    comboBox.ItemsSource = list;
                    comboBox.Text = str;
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

            MainWindow.SetConfigValue(config, "FileEncoding", ((ComboBoxItem)cmbEncoding.SelectedItem).Name);
            MainWindow.SetConfigValue(config, "MaxFileSize", txtMaxFileSize.Text);
            MainWindow.SetConfigValue(config, "MaxFileSizeUnit", ((ComboBoxItem)cmbFileSizeUnit.SelectedItem).Name);

            SaveHistory(cmbBasePath, "HistoryBasePath");
            SaveHistory(cmbIncludeFiles, "HistoryIncludeFiles");
            SaveHistory(cmbExcludeFiles, "HistoryExcludeFiles");
            SaveHistory(cmbContainingText, "HistoryContainingText");
        }

        private void LoadFileSeekSettings(bool force = false)
        {
            try
            {
                using var mainKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Binary Fortress Software\FileSeek");
                if (mainKey != null)
                {
                    var profileName = mainKey.GetValue("DefaultProfile") as string ?? "DefaultProfile";
                    using var profileKey = mainKey.OpenSubKey(profileName == "DefaultProfile" ? "DefaultProfile" : $@"Profiles\{profileName}");
                    if (profileKey != null)
                    {
                        if (force)
                        {
                            var caseSens = profileKey.GetValue("CaseSensitive") as string;
                            if (caseSens != null)
                            {
                                chkCaseSensitive.IsChecked = caseSens == "1";
                            }

                            var subFolders = profileKey.GetValue("SearchSubFolders") as string;
                            if (subFolders != null)
                            {
                                chkRecursive.IsChecked = subFolders == "1";
                            }

                            var isRegex = profileKey.GetValue("IsQueryRegEx") as string;
                            if (isRegex != null)
                            {
                                chkRegularExpression.IsChecked = isRegex == "1";
                            }
                        }
                        else
                        {
                            if (chkCaseSensitive.IsChecked == null)
                            {
                                var caseSens = profileKey.GetValue("CaseSensitive") as string;
                                if (caseSens != null) chkCaseSensitive.IsChecked = caseSens == "1";
                            }
                            if (chkRecursive.IsChecked == null)
                            {
                                var subFolders = profileKey.GetValue("SearchSubFolders") as string;
                                if (subFolders != null) chkRecursive.IsChecked = subFolders == "1";
                            }
                            if (chkRegularExpression.IsChecked == null)
                            {
                                var isRegex = profileKey.GetValue("IsQueryRegEx") as string;
                                if (isRegex != null) chkRegularExpression.IsChecked = isRegex == "1";
                            }
                        }

                        // Load history lists from FileSeek registry
                        LoadHistoryFromRegistry(cmbBasePath, profileKey, "LastUsedPath");
                        LoadHistoryFromRegistry(cmbIncludeFiles, profileKey, "LastUsedFilesInclude");
                        LoadHistoryFromRegistry(cmbExcludeFiles, profileKey, "LastUsedFilesExclude");
                        LoadHistoryFromRegistry(cmbContainingText, profileKey, "LastUsedQuery");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error loading FileSeek settings: " + ex.Message);
            }
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
                        ResultLineItems.Reset(Enumerable.Empty<ResultLine>());

                        var lineResults = m_ripGrepWrapper.FileResults.Where(x => x.Key.path == addedItem.Path && x.Key.filename == addedItem.Filename);
                        foreach (var lineResult in lineResults)
                        {
                            ResultLineItems.Add(new ResultLine(lineResult.Key.lineNumber, GetColorizedString(lineResult.Value.LineContent, lineResult.Value.TermResults).Trim(), lineResult.Key.filename, lineResult.Key.path));
                        }

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

            if (e.Key != Key.F3)
            {
                return;
            }

            OpenFileViewer();
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

        private void gridResultLines_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (string.IsNullOrEmpty(MainWindow.FileViewerPath) || string.IsNullOrEmpty(MainWindow.FileViewerArgs))
            {
                e.Handled = true;
            }
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
            if ((cmbBasePath.Text.IndexOfAny(Path.GetInvalidPathChars()) != -1) || !Directory.Exists(cmbBasePath.Text))
            {
                MessageBox.Show("Invalid \"In Folder\" path.", "Error");
                return;
            }

            if (m_cancellationTokenSource != null)
            {
                return;
            }

            var searchTerms = Regex.Matches(cmbContainingText.Text, @"""[^""\\]*(?:\\.[^""\\]*)*""|([^\s])+|[^\s""]+");
            if (searchTerms.Count < 1)
            {
                return;
            }

            if (MainWindow.MaxSearchTerms < 1)
            {
                MainWindow.MaxSearchTerms = 1;
            }

            if (searchTerms.Count > MainWindow.MaxSearchTerms)
            {
                MessageBox.Show($"Search text contains more than {MainWindow.MaxSearchTerms} terms.");
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            btnStart.IsEnabled = false;
            btnCancel.IsEnabled = true;
            btnSettings.IsEnabled = false;
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

            // Save history values
            SaveHistory(cmbBasePath, "HistoryBasePath");
            SaveHistory(cmbIncludeFiles, "HistoryIncludeFiles");
            SaveHistory(cmbExcludeFiles, "HistoryExcludeFiles");
            SaveHistory(cmbContainingText, "HistoryContainingText");

            try
            {
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
                };

                FileResultItems.Reset(Enumerable.Empty<FileSearchResult>());
                ResultLineItems.Reset(Enumerable.Empty<ResultLine>());

                await m_ripGrepWrapper.Search(searchParameters, cancellationTokenSource.Token);
            }
            finally
            {
                btnCancel.IsEnabled = false;
                btnStart.IsEnabled = true;
                btnSettings.IsEnabled = true;
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

        private void btnImportFileSeek_Click(object sender, RoutedEventArgs e)
        {
            LoadFileSeekSettings(force: true);
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

        private void PopulateAllLines()
        {
            try
            {
                var allLines = new List<ResultLine>();
                foreach (var lineResult in m_ripGrepWrapper.FileResults)
                {
                    allLines.Add(new ResultLine(
                        lineResult.Key.lineNumber,
                        GetColorizedString(lineResult.Value.LineContent, lineResult.Value.TermResults).Trim(),
                        lineResult.Key.filename,
                        lineResult.Key.path
                    ));
                }
                ResultLineItems.Reset(allLines);
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

        private void OpenFileViewer()
        {
            if (gridResultLines.SelectedItems.Count <= 0)
            {
                return;
            }

            var resultLine = gridResultLines.SelectedItems[gridResultLines.SelectedItems.Count - 1] as ResultLine;
            if (resultLine == null)
            {
                return;
            }

            string fullPath = "";

            if (chkShowAllLines.IsChecked == true)
            {
                if (!string.IsNullOrEmpty(resultLine.Path) && !string.IsNullOrEmpty(resultLine.File))
                {
                    fullPath = Path.Combine(resultLine.Path, resultLine.File);
                }
            }
            else
            {
                if (gridFileResults.SelectedItems.Count > 0)
                {
                    var resultFile = gridFileResults.SelectedItems[gridFileResults.SelectedItems.Count - 1] as FileSearchResult;
                    if (resultFile != null)
                    {
                        fullPath = Path.Combine(resultFile.Path, resultFile.Filename);
                    }
                }
            }

            if (string.IsNullOrEmpty(fullPath))
            {
                return;
            }

            if (!string.IsNullOrEmpty(MainWindow.FileViewerPath) && File.Exists(MainWindow.FileViewerPath) && !string.IsNullOrEmpty(MainWindow.FileViewerArgs) && MainWindow.FileViewerArgs.Contains("$FILE"))
            {
                var args = MainWindow.FileViewerArgs
                    .Replace("$FILE", $"\"{fullPath}\"")
                    .Replace("$LINE", resultLine.Line.ToString());

                var processStartInfo = new ProcessStartInfo()
                {
                    FileName = MainWindow.FileViewerPath,
                    Arguments = args,
                    UseShellExecute = false
                };

                using var process = new Process()
                {
                    StartInfo = processStartInfo
                };
                process.Start();
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
                MainWindow.FileViewerPath = settingsWindow.FileViewerPath;
                MainWindow.FileViewerArgs = settingsWindow.FileViewerArgs;

                var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
                MainWindow.SaveGlobalConfig(config);
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
            var input = txtMaxFileSize.Text;
            txtMaxFileSize.Text = new string(input.Where(c => char.IsDigit(c)).ToArray());
        }

        private void cmbFileSizeUnit_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (txtMaxFileSize != null)
            {
                txtMaxFileSize.IsEnabled = (cmbFileSizeUnit.SelectedIndex != 0);
            }
        }
    }
}
