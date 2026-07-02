using FramePFX.Themes;
using Ookii.Dialogs.Wpf;
using Peter;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static rg_gui.RipGrepWrapper;

namespace rg_gui
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const double DEFAULT_MAINWINDOW_LEFT = 0;
        private const double DEFAULT_MAINWINDOW_TOP = 0;
        private const double DEFAULT_MAINWINDOW_WIDTH = 800;
        private const double DEFAULT_MAINWINDOW_HEIGHT = 450;
        private const int DEFAULT_MAINWINDOW_STATE = 0;

        private const string DEFAULT_BASEPATH = "";
        private const string DEFAULT_INCLUDEFILES = "";
        private const string DEFAULT_EXCLUDEFILES = "";
        private const string DEFAULT_CONTAININGTEXT = "";

        private const bool DEFAULT_CASESENSITIVE = false;
        private const bool DEFAULT_RECURSIVE = true;
        private const bool DEFAULT_REGULAREXPRESSION = false;

        private const string DEFAULT_FILEENCODING = "Auto";

        private const int DEFAULT_MAXFILESIZE = 0;
        private const string DEFAULT_MAXFILESIZEUNIT = "None";

        private const int DEFAULT_MAXSEARCHTERMS = 10;

        private const int HIGHLIGHT_COLORS_COUNT = 4;

        private const double GRID_SPLITTER_WIDTH = 5.0;

        private string m_currentInput = string.Empty;
        private string? m_currentSuggestion = string.Empty;
        private string m_currentText = string.Empty;
        private int m_selectionStart;
        private int m_selectionLength;
        private IEnumerable<string> m_folderSuggestionValues = Enumerable.Empty<string>();

        private int m_maxSearchTerms;

        private const ThemeType DEFAULT_THEME = ThemeType.Light;
        private ThemeType m_currentTheme;

        private const bool DEFAULT_MULTIPLEHIGHLIGHTCOLORS = true;
        private bool m_multipleHighlightColors = DEFAULT_MULTIPLEHIGHLIGHTCOLORS;

        private const int DEFAULT_MAXLINEHIGHLIGHTS = 100;
        private int m_maxLineHighlights = DEFAULT_MAXLINEHIGHLIGHTS;

        private string m_fileViewerPath;
        private string m_fileViewerArgs;

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

            public ResultLine(int line, string content, string file = "")
            {
                Line = line;
                Content = content;
                File = file;
            }
        }

        private CancellationTokenSource? m_cancellationTokenSource;

        private readonly RipGrepWrapper m_ripGrepWrapper;

        public RangeObservableCollection<FileSearchResult> FileResultItems { get; } = new();
        public RangeObservableCollection<ResultLine> ResultLineItems { get; } = new();

        public MainWindow(string? basePath, string? includeFiles, string? excludeFiles, string? containingText)
        {
            InitializeComponent();

            var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            Left = double.TryParse(config.AppSettings.Settings["MainWindowLeft"]?.Value, out var left) ? left : DEFAULT_MAINWINDOW_LEFT;
            Top = double.TryParse(config.AppSettings.Settings["MainWindowTop"]?.Value, out var top) ? top : DEFAULT_MAINWINDOW_TOP;
            Width = double.TryParse(config.AppSettings.Settings["MainWindowWidth"]?.Value, out var width) ? width : DEFAULT_MAINWINDOW_WIDTH;
            Height = double.TryParse(config.AppSettings.Settings["MainWindowHeight"]?.Value, out var height) ? height : DEFAULT_MAINWINDOW_HEIGHT;
            WindowState = int.TryParse(config.AppSettings.Settings["MainWindowState"]?.Value, out var windowState) ? WindowState : DEFAULT_MAINWINDOW_STATE;

            txtBasePath.Text = basePath ?? config.AppSettings.Settings["BasePath"]?.Value ?? DEFAULT_BASEPATH;
            txtIncludeFiles.Text = includeFiles ?? config.AppSettings.Settings["IncludeFiles"]?.Value ?? DEFAULT_INCLUDEFILES;
            txtExcludeFiles.Text = excludeFiles ?? config.AppSettings.Settings["ExcludeFiles"]?.Value ?? DEFAULT_EXCLUDEFILES;
            txtContainingText.Text = containingText ?? config.AppSettings.Settings["ContainingText"]?.Value ?? DEFAULT_CONTAININGTEXT;
            chkCaseSensitive.IsChecked = bool.TryParse(config.AppSettings.Settings["CaseSensitive"]?.Value, out var caseSensitive) ? caseSensitive : DEFAULT_CASESENSITIVE;
            chkRecursive.IsChecked = bool.TryParse(config.AppSettings.Settings["Recursive"]?.Value, out var recursive) ? recursive : DEFAULT_RECURSIVE;

            var gridFileResultsWidthStr = config.AppSettings.Settings["GridFileResultsWidth"]?.Value;
            var gridSplitterWidthStr = config.AppSettings.Settings["GridSplitterWidth"]?.Value;
            var gridResultLinesWidthStr = config.AppSettings.Settings["GridResultLinesWidth"]?.Value;

            var gridLengthConverter = new GridLengthConverter();

            if (gridFileResultsWidthStr != null && gridSplitterWidthStr != null && gridResultLinesWidthStr != null)
            {
                var gridFileResultsWidth = (GridLength?)gridLengthConverter.ConvertFromString(gridFileResultsWidthStr);
                var gridSplitterWidth = (GridLength?)gridLengthConverter.ConvertFromString(gridSplitterWidthStr);
                var gridResultLinesWidth = (GridLength?)gridLengthConverter.ConvertFromString(gridResultLinesWidthStr);

                // Sanity check the column widths before restoring them.
                if (gridFileResultsWidth != null && gridSplitterWidth != null && gridResultLinesWidth != null &&
                    (gridFileResultsWidth.Value.Value + gridSplitterWidth.Value.Value + gridResultLinesWidth.Value.Value) < Width &&
                    gridSplitterWidth.Value.Value == GRID_SPLITTER_WIDTH)
                {
                    gridResults.ColumnDefinitions[0].Width = (GridLength)gridFileResultsWidth;
                    gridResults.ColumnDefinitions[1].Width = (GridLength)gridSplitterWidth;
                    gridResults.ColumnDefinitions[2].Width = (GridLength)gridResultLinesWidth;
                }
            }

            var fileEncoding = cmbEncoding.FindName(config.AppSettings.Settings["FileEncoding"]?.Value ?? DEFAULT_FILEENCODING);
            if (fileEncoding != null)
            {
                cmbEncoding.SelectedItem = fileEncoding;
            }
            else
            {
                cmbEncoding.SelectedIndex = 0;
            }

            txtMaxFileSize.Text = (int.TryParse(config.AppSettings.Settings["MaxFileSize"]?.Value, out var maxFileSize) ? maxFileSize : DEFAULT_MAXFILESIZE).ToString();
            var maxFileSizeUnit = cmbFileSizeUnit.FindName(config.AppSettings.Settings["MaxFileSizeUnit"]?.Value ?? DEFAULT_MAXFILESIZEUNIT);
            if (maxFileSizeUnit != null)
            {
                cmbFileSizeUnit.SelectedItem = maxFileSizeUnit;
            }
            else
            {
                cmbFileSizeUnit.SelectedIndex = 0;
            }

            m_currentTheme = Enum.TryParse<ThemeType>(config.AppSettings.Settings["Theme"]?.Value, out var themeName) ? themeName : DEFAULT_THEME;
            ThemesController.SetTheme(m_currentTheme);

            var ripgrepPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty, "rg.exe");
            if (!File.Exists(ripgrepPath))
            {
                MessageBox.Show("rg.exe not found in installation path.", "Error");
                throw new Exception("rg.exe not found in installation path.");
            }

            m_maxSearchTerms = int.TryParse(config.AppSettings.Settings["MaxSearchTerms"]?.Value, out var maxSearchTerms) ? maxSearchTerms : DEFAULT_MAXSEARCHTERMS;
            m_multipleHighlightColors = bool.TryParse(config.AppSettings.Settings["MultipleHighlightColors"]?.Value, out var multipleHighlightColors) ? multipleHighlightColors : DEFAULT_MULTIPLEHIGHLIGHTCOLORS;

            m_maxLineHighlights = int.TryParse(config.AppSettings.Settings["MaxLineHighlights"]?.Value, out var maxLineHighlights) ? maxLineHighlights : DEFAULT_MAXLINEHIGHLIGHTS;

            m_ripGrepWrapper = new RipGrepWrapper(ripgrepPath);
            m_ripGrepWrapper.FileFound += OnFileAdded;

            m_fileViewerPath = config.AppSettings.Settings["FileViewerPath"]?.Value ?? string.Empty;
            m_fileViewerArgs = config.AppSettings.Settings["FileViewerArgs"]?.Value ?? string.Empty;

            bool hasLocalSettings = config.AppSettings.Settings["BasePath"] != null;
            LoadFileSeekSettings(force: !hasLocalSettings);
        }

        private static void SetConfigValue(Configuration config, string key, string value)
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

        private void OnClosing(object? sender, EventArgs e)
        {
            var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            if (WindowState != WindowState.Minimized)
            {
                SetConfigValue(config, "MainWindowLeft", Left.ToString());
                SetConfigValue(config, "MainWindowTop", Top.ToString());
                SetConfigValue(config, "MainWindowWidth", Width.ToString());
                SetConfigValue(config, "MainWindowHeight", Height.ToString());
                SetConfigValue(config, "MainWindowState", ((int)WindowState).ToString());

                var gridLengthConverter = new GridLengthConverter();
                var gridFileResultsWidthStr = gridLengthConverter.ConvertToString(gridResults.ColumnDefinitions[0].Width);
                var gridSplitterWidthStr = gridLengthConverter.ConvertToString(gridResults.ColumnDefinitions[1].Width);
                var gridResultLinesWidthStr = gridLengthConverter.ConvertToString(gridResults.ColumnDefinitions[2].Width);

                if (gridFileResultsWidthStr != null && gridSplitterWidthStr != null && gridResultLinesWidthStr != null)
                {
                    SetConfigValue(config, "GridFileResultsWidth", gridFileResultsWidthStr);
                    SetConfigValue(config, "GridSplitterWidth", gridSplitterWidthStr);
                    SetConfigValue(config, "GridResultLinesWidth", gridResultLinesWidthStr);
                }
                else
                {
                    // Unable to get the current values for some reason.  Save default values instead.
                    SetConfigValue(config, "GridFileResultsWidth", "*");
                    SetConfigValue(config, "GridSplitterWidth", GRID_SPLITTER_WIDTH.ToString("N0"));
                    SetConfigValue(config, "GridResultLinesWidth", "*");
                }
            }

            SetConfigValue(config, "BasePath", txtBasePath.Text);
            SetConfigValue(config, "IncludeFiles", txtIncludeFiles.Text);
            SetConfigValue(config, "ExcludeFiles", txtExcludeFiles.Text);
            SetConfigValue(config, "ContainingText", txtContainingText.Text);
            SetConfigValue(config, "CaseSensitive", (chkCaseSensitive.IsChecked ?? DEFAULT_CASESENSITIVE).ToString());
            SetConfigValue(config, "Recursive", (chkRecursive.IsChecked ?? DEFAULT_RECURSIVE).ToString());
            SetConfigValue(config, "RegularExpression", (chkRegularExpression.IsChecked ?? DEFAULT_REGULAREXPRESSION).ToString());

            SetConfigValue(config, "FileEncoding", ((ComboBoxItem)cmbEncoding.SelectedItem).Name);
            SetConfigValue(config, "MaxFileSize", txtMaxFileSize.Text);
            SetConfigValue(config, "MaxFileSizeUnit", ((ComboBoxItem)cmbFileSizeUnit.SelectedItem).Name);
            SetConfigValue(config, "Theme", m_currentTheme.ToString());
            SetConfigValue(config, "MultipleHighlightColors", m_multipleHighlightColors.ToString());
            SetConfigValue(config, "MaxLineHighlights", m_maxLineHighlights.ToString());

            SetConfigValue(config, "FileViewerPath", m_fileViewerPath);
            SetConfigValue(config, "FileViewerArgs", m_fileViewerArgs);

            config.Save();

            ConfigurationManager.RefreshSection("appSettings");
        }

        private void OnFileAdded(object? sender, (string path, string filename) result)
        {
            Application.Current.Dispatcher.Invoke(delegate
            {
                // Ensure the same result won't be added multiple times.
                if (!FileResultItems.Any(x => x.Path == result.path && x.Filename == result.filename))
                {
                    FileResultItems.Add(new FileSearchResult(result.path, result.filename));
                    txtFileListStatus.Text = $"Found {FileResultItems.Count} files.";
                }
            });
        }

        private void gridFileResults_MouseDown(object? sender, MouseEventArgs e)
        {
            if ((e.RightButton == MouseButtonState.Pressed && !SystemParameters.SwapButtons) || (e.LeftButton == MouseButtonState.Pressed && SystemParameters.SwapButtons))
            {
                var selectedFiles = new List<FileInfo>();

                foreach (var selectedItem in gridFileResults.SelectedItems)
                {
                    if (selectedItem is FileSearchResult fileSearchResult)
                    {
                        selectedFiles.Add(new FileInfo(Path.Combine(fileSearchResult.Path, fileSearchResult.Filename)));
                    }
                }

                if (selectedFiles.Any())
                {
                    var point = PointToScreen(e.MouseDevice.GetPosition(this));

                    var shellContextMenu = new ShellContextMenu();
                    shellContextMenu.ShowContextMenu(selectedFiles, new System.Drawing.Point((int)point.X, (int)point.Y));
                }
            }
        }

        private void gridFileResults_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (chkShowAllLines.IsChecked == true)
            {
                return;
            }

            if (e.AddedItems.Count > 0)
            {
                if (e.AddedItems[0] is FileSearchResult addedItem)
                {
                    // Scroll gridResultLines back to left end.
                    GetScrollViewer(gridResultLines)?.ScrollToLeftEnd();

                    ResultLineItems.Reset(Enumerable.Empty<ResultLine>());

                    var lineResults = m_ripGrepWrapper.FileResults.Where(x => x.Key.path == addedItem.Path && x.Key.filename == addedItem.Filename);

                    foreach (var lineResult in lineResults)
                    {
                        ResultLineItems.Add(new ResultLine(lineResult.Key.lineNumber, GetColorizedString(lineResult.Value.LineContent, lineResult.Value.TermResults).Trim(), lineResult.Key.filename));
                    }

                    txtResultLineStatus.Text = $"{ResultLineItems.Count} lines matched.";
                }
            }
        }

        private void grid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
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
            if (string.IsNullOrEmpty(m_fileViewerPath) || string.IsNullOrEmpty(m_fileViewerArgs))
            {
                e.Handled = true;
            }
        }

        private static ScrollViewer? GetScrollViewer(UIElement? element)
        {
            if (element == null)
            {
                return null;
            }

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
            if ((txtBasePath.Text.IndexOfAny(Path.GetInvalidPathChars()) != -1) || !Directory.Exists(txtBasePath.Text))
            {
                MessageBox.Show("Invalid \"In Folder\" path.", "Error");
                return;
            }

            // based on https://stackoverflow.com/questions/66598956/how-to-stop-a-method-triggered-by-button-click-in-wpf

            if (m_cancellationTokenSource != null)
            {
                return;
            }

            // Based on https://stackoverflow.com/questions/52194058/regex-with-escaped-double-quotes
            var searchTerms = Regex.Matches(txtContainingText.Text, @"""[^""\\]*(?:\\.[^""\\]*)*""|([^\s])+|[^\s""]+");
            if (searchTerms.Count < 1)
            {
                return;
            }

            // Sanity check -- allow minimum of one search term.
            if (m_maxSearchTerms < 1)
            {
                m_maxSearchTerms = 1;
            }

            if (searchTerms.Count > m_maxSearchTerms)
            {
                MessageBox.Show($"Search text contains more than {m_maxSearchTerms} terms.");
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

            var startPath = txtBasePath.Text;
            if (startPath.EndsWith(Path.DirectorySeparatorChar))
            {
                startPath = startPath.TrimEnd(Path.DirectorySeparatorChar);
            }

            try
            {
                var searchParameters = new SearchParameters
                {
                    StartPath = startPath,
                    SearchStrings = searchTerms.Cast<Match>().Select(x => x.Value),
                    IgnoreCase = !(chkCaseSensitive.IsChecked ?? false),
                    Recursive = chkRecursive.IsChecked ?? true,
                    IncludePatterns = txtIncludeFiles.Text,
                    ExcludePatterns = txtExcludeFiles.Text,
                    RegularExpression = chkRegularExpression.IsChecked ?? false,
                    Encoding = (FileEncoding)cmbEncoding.SelectedIndex,
                    MaxFileSize = int.Parse(txtMaxFileSize.Text),
                    MaxFileSizeUnit = (MaxFileSizeUnit)cmbFileSizeUnit.SelectedIndex,
                };

                FileResultItems.Reset(Enumerable.Empty<FileSearchResult>());

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

            if (dialog.ShowDialog(this).GetValueOrDefault())
            {
                txtBasePath.Text = dialog.SelectedPath;
            }
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            m_ripGrepWrapper.Resume();
            m_cancellationTokenSource?.Cancel();
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
            MessageBox.Show("Configuración de FileSeek Pro cargada con éxito.", "Sincronización");
        }

        private void LoadFileSeekSettings(bool force = false)
        {
            try
            {
                using var mainKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Binary Fortress Software\FileSeek");
                if (mainKey != null)
                {
                    // Configuración del visor de archivos externo
                    string? openOther = mainKey.GetValue("OpenOther") as string;
                    string? openWithLastExe = mainKey.GetValue("OpenWithLastExeSelected") as string;
                    string? openOtherArgs = mainKey.GetValue("OpenOtherArgs") as string;

                    string fsFileViewerPath = !string.IsNullOrEmpty(openOther) ? openOther : (openWithLastExe ?? "");
                    string fsFileViewerArgs = openOtherArgs ?? "";

                    if (!string.IsNullOrEmpty(fsFileViewerArgs))
                    {
                        fsFileViewerArgs = Regex.Replace(fsFileViewerArgs, @"\$file\$", "$FILE", RegexOptions.IgnoreCase);
                        fsFileViewerArgs = Regex.Replace(fsFileViewerArgs, @"\$line\$", "$LINE", RegexOptions.IgnoreCase);
                    }

                    if (force || string.IsNullOrEmpty(m_fileViewerPath))
                    {
                        m_fileViewerPath = fsFileViewerPath;
                    }
                    if (force || string.IsNullOrEmpty(m_fileViewerArgs))
                    {
                        m_fileViewerArgs = fsFileViewerArgs;
                    }

                    // Configuración del perfil de búsqueda
                    using var profileKey = mainKey.OpenSubKey("DefaultProfile");
                    if (profileKey != null)
                    {
                        if (force || string.IsNullOrEmpty(txtBasePath.Text))
                        {
                            var paths = profileKey.GetValue("LastUsedPath") as string[];
                            if (paths != null && paths.Length > 0)
                            {
                                var splitPaths = paths[0].Split('|', StringSplitOptions.RemoveEmptyEntries);
                                if (splitPaths.Length > 0)
                                    txtBasePath.Text = splitPaths[0];
                            }
                        }

                        if (force || string.IsNullOrEmpty(txtIncludeFiles.Text))
                        {
                            var includes = profileKey.GetValue("LastUsedFilesInclude") as string[];
                            if (includes != null && includes.Length > 0)
                            {
                                txtIncludeFiles.Text = string.Join(",", includes[0].Split('|', StringSplitOptions.RemoveEmptyEntries));
                            }
                        }

                        if (force || string.IsNullOrEmpty(txtExcludeFiles.Text))
                        {
                            var excludes = profileKey.GetValue("LastUsedFilesExclude") as string[];
                            if (excludes != null && excludes.Length > 0)
                            {
                                txtExcludeFiles.Text = string.Join(",", excludes[0].Split('|', StringSplitOptions.RemoveEmptyEntries));
                            }
                        }

                        if (force || string.IsNullOrEmpty(txtContainingText.Text))
                        {
                            var queries = profileKey.GetValue("LastUsedQuery") as string[];
                            if (queries != null && queries.Length > 0)
                            {
                                txtContainingText.Text = queries[0];
                            }
                        }

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
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error loading FileSeek settings: " + ex.Message);
            }
        }

        private void chkShowAllLines_Checked(object sender, RoutedEventArgs e)
        {
            if (colFile != null)
            {
                colFile.Visibility = Visibility.Visible;
            }
            PopulateAllLines();
        }

        private void chkShowAllLines_Unchecked(object sender, RoutedEventArgs e)
        {
            if (colFile != null)
            {
                colFile.Visibility = Visibility.Collapsed;
            }
            if (gridFileResults.SelectedItem is FileSearchResult selectedFile)
            {
                ResultLineItems.Reset(Enumerable.Empty<ResultLine>());
                var lineResults = m_ripGrepWrapper.FileResults.Where(x => x.Key.path == selectedFile.Path && x.Key.filename == selectedFile.Filename);
                foreach (var lineResult in lineResults)
                {
                    ResultLineItems.Add(new ResultLine(lineResult.Key.lineNumber, GetColorizedString(lineResult.Value.LineContent, lineResult.Value.TermResults).Trim(), lineResult.Key.filename));
                }
                txtResultLineStatus.Text = $"{ResultLineItems.Count} lines matched.";
            }
            else
            {
                ResultLineItems.Reset(Enumerable.Empty<ResultLine>());
                txtResultLineStatus.Text = string.Empty;
            }
        }

        private void PopulateAllLines()
        {
            var allLines = new List<ResultLine>();
            foreach (var lineResult in m_ripGrepWrapper.FileResults)
            {
                allLines.Add(new ResultLine(
                    lineResult.Key.lineNumber,
                    GetColorizedString(lineResult.Value.LineContent, lineResult.Value.TermResults).Trim(),
                    lineResult.Key.filename
                ));
            }
            ResultLineItems.Reset(allLines);
            txtResultLineStatus.Text = $"{ResultLineItems.Count} lines matched.";
        }

        private void copySelectedLines_Click(object sender, RoutedEventArgs e)
        {
            var stringBuilder = new StringBuilder();
            foreach (var selectedItem in gridResultLines.SelectedItems)
            {
                if (selectedItem is ResultLine resultLine)
                {
                    if (chkShowAllLines.IsChecked == true && !string.IsNullOrEmpty(resultLine.File))
                    {
                        stringBuilder.AppendLine($"{resultLine.File}\t{resultLine.Line}\t{resultLine.Content}");
                    }
                    else
                    {
                        stringBuilder.AppendLine($"{resultLine.Line}\t{resultLine.Content}");
                    }
                }
            }
            if (stringBuilder.Length > 0)
            {
                Clipboard.SetText(stringBuilder.ToString());
            }
        }

        private void copyAllLines_Click(object sender, RoutedEventArgs e)
        {
            var stringBuilder = new StringBuilder();
            foreach (var item in ResultLineItems)
            {
                if (chkShowAllLines.IsChecked == true && !string.IsNullOrEmpty(item.File))
                {
                    stringBuilder.AppendLine($"{item.File}\t{item.Line}\t{item.Content}");
                }
                else
                {
                    stringBuilder.AppendLine($"{item.Line}\t{item.Content}");
                }
            }
            if (stringBuilder.Length > 0)
            {
                Clipboard.SetText(stringBuilder.ToString());
            }
        }

        private void exportAllLinesToCsv_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog()
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = "search_results.csv"
            };

            if (dialog.ShowDialog(this) == true)
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

                    foreach (var item in ResultLineItems)
                    {
                        string escapedContent = item.Content.Replace("\"", "\"\"");
                        if (chkShowAllLines.IsChecked == true)
                        {
                            string escapedFile = item.File.Replace("\"", "\"\"");
                            writer.WriteLine($"\"{escapFile}\",{item.Line},\"{escapedContent}\"");
                        }
                        else
                        {
                            writer.WriteLine($"{item.Line},\"{escapedContent}\"");
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

        private void btnSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow
            {
                Owner = this,
                Theme = m_currentTheme.GetName(),
                MaxSearchTerms = m_maxSearchTerms,
                Multicolor = m_multipleHighlightColors,
                MaxLineHighlights = m_maxLineHighlights,
                FileViewerPath = m_fileViewerPath,
                FileViewerArgs = m_fileViewerArgs
            };

            if (settingsWindow.ShowDialog() == true)
            {
                m_currentTheme = Enum.Parse<ThemeType>(settingsWindow.Theme);
                ThemesController.SetTheme(m_currentTheme);
                m_maxSearchTerms = settingsWindow.MaxSearchTerms;
                m_multipleHighlightColors = settingsWindow.Multicolor;
                m_maxLineHighlights = settingsWindow.MaxLineHighlights;
                m_fileViewerPath = settingsWindow.FileViewerPath;
                m_fileViewerArgs = settingsWindow.FileViewerArgs;
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

            // Based on https://learn.microsoft.com/en-us/answers/questions/840981/auto-complete-for-textbox-in-wpf-(mvvm)
            var input = txtBasePath.Text;
            if (input.Length > m_currentInput.Length && input != m_currentSuggestion)
            {
                m_currentSuggestion = m_folderSuggestionValues.FirstOrDefault(x => x.StartsWith(input, StringComparison.CurrentCultureIgnoreCase));
                if (m_currentSuggestion != null)
                {
                    m_currentText = m_currentSuggestion;
                    m_selectionStart = input.Length;
                    m_selectionLength = m_currentSuggestion.Length - input.Length;

                    txtBasePath.Text = m_currentText;
                    txtBasePath.Select(m_selectionStart, m_selectionLength);
                }
            }
            m_currentInput = input;
        }

        private void UpdateFolderSuggestionValues()
        {
            var input = txtBasePath.Text;

            if (input.EndsWith(Path.DirectorySeparatorChar) && Directory.Exists(input))
            {
                m_folderSuggestionValues = Directory.GetDirectories(input);
            }
        }

        private void txtMaxFileSize_TextChanged(object sender, TextChangedEventArgs e)
        {
            var input = txtMaxFileSize.Text;
            txtMaxFileSize.Text = new string(input.Where(c => char.IsDigit(c)).ToArray());
        }

        private void cmbFileSizeUnit_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            txtMaxFileSize.IsEnabled = (cmbFileSizeUnit.SelectedIndex != 0);
        }

        private void openInFileViewer_Click(object sender, RoutedEventArgs e)
        {
            OpenFileViewer();
        }

        private string GetColorizedString(string source, IEnumerable<TermResult> termResults)
        {
            // Algorithm for calculating highlightRanges was written with assistance from GitHub Copilot AI (GPT-4.1).
            var segmentEdges = new List<int>();
            foreach (var termResult in termResults)
            {
                segmentEdges.Add(termResult.Start);
                segmentEdges.Add(termResult.End + 1);  // +1, next place a segment can start is immediately after the current one.
            }
            var sortedEdges = segmentEdges.Distinct().OrderBy(x => x).ToList();

            var highlightResults = new List<TermResult>();
            TermResult? previous = null;

            for (var i = 0; i < sortedEdges.Count - 1 && highlightResults.Count < m_maxLineHighlights; i++)
            {
                var segmentStart = sortedEdges[i];
                var segmentEnd = sortedEdges[i + 1] - 1;  // -1, this segment ends immediately before the next one begins.

                // Get termResult with the lowest TermIndex overlapping this segment, if any.  This determines the color for this segment.
                var termResult = termResults.Where(x => segmentStart >= x.Start && segmentEnd <= x.End).OrderBy(x => x.TermIndex).FirstOrDefault();
                if (termResult != null)
                {
                    // See if we should expand the previous segment or add a new one.
                    if (previous?.End == segmentStart - 1 && previous?.TermIndex == termResult.TermIndex)
                    {
                        // Previous result is adjacent to the current one, but TermIndex hasn't changed.  Expand previous range.
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

                var colorIndex = m_multipleHighlightColors ? highlightResult.TermIndex % HIGHLIGHT_COLORS_COUNT : 0;

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
            if (gridFileResults.SelectedItems.Count <= 0 || gridResultLines.SelectedItems.Count <= 0)
            {
                return;
            }

            var resultFile = gridFileResults.SelectedItems[gridFileResults.SelectedItems.Count - 1] as FileSearchResult;
            var resultLine = gridResultLines.SelectedItems[gridResultLines.SelectedItems.Count - 1] as ResultLine;
            if (resultFile == null || resultLine == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(m_fileViewerPath) && File.Exists(m_fileViewerPath) && !string.IsNullOrEmpty(m_fileViewerArgs) && m_fileViewerArgs.Contains("$FILE"))
            {
                var args = m_fileViewerArgs
                    .Replace("$FILE", $"\"{Path.Combine(resultFile.Path, resultFile.Filename)}\"")
                    .Replace("$LINE", resultLine.Line.ToString());

                var processStartInfo = new ProcessStartInfo()
                {
                    FileName = m_fileViewerPath,
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
    }
}
