using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace rg_gui
{
    /// <summary>
    /// Interaction logic for SettingsWindow.xaml
    /// </summary>
    public partial class SettingsWindow : Window, INotifyPropertyChanged
    {
        public string Theme { get; set; }
        public int MaxSearchTerms { get; set; }
        public bool Multicolor { get; set; }
        public int MaxLineHighlights { get; set; }

        private string _fileViewerPath;

        public event PropertyChangedEventHandler PropertyChanged;

        public string FileViewerPath
        {
            get => _fileViewerPath;
            set
            {
                _fileViewerPath = value;
                OnPropertyChanged();
            }
        }

        private string _fileViewerArgs;
        public string FileViewerArgs
        {
            get => _fileViewerArgs;
            set
            {
                _fileViewerArgs = value;
                OnPropertyChanged();
            }
        }

        public SettingsWindow()
        {
            InitializeComponent();
            DataContext = this;
        }

        private void btnOK_Click(object sender, RoutedEventArgs e)
        {
            if (MaxSearchTerms < 1)
            {
                MessageBox.Show("Maximum search terms must be at least 1.");
                return;
            }

            if (!string.IsNullOrEmpty(FileViewerPath) && !File.Exists(FileViewerPath))
            {
                MessageBox.Show("Invalid file viewer path.");
                return;
            }

            if (!string.IsNullOrEmpty(FileViewerArgs) && !FileViewerArgs.Contains("$FILE"))
            {
                MessageBox.Show("Invalid file viewer arguments.");
                return;
            }

            this.DialogResult = true;
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void btnFileViewerBrowse_Click(object sender, RoutedEventArgs e)
        {
            // Configure open file dialog box
            var fileDialog = new Microsoft.Win32.OpenFileDialog
            {
                DefaultExt = ".exe",
                Filter = "Executable files (.exe)|*.exe"
            };

            if (fileDialog.ShowDialog() == true)
            {
                FileViewerPath = fileDialog.FileName;
            }
        }

        private void txtMaxTerms_TextChanged(object sender, TextChangedEventArgs e)
        {
            var input = txtMaxTerms.Text;
            txtMaxTerms.Text = new string(input.Where(c => char.IsDigit(c)).ToArray());
        }

        private void txtMaxLineHighlights_TextChanged(object sender, TextChangedEventArgs e)
        {
            var input = txtMaxLineHighlights.Text;
            txtMaxLineHighlights.Text = new string(input.Where(c => char.IsDigit(c)).ToArray());
        }

        private string[] GetRegistryList(Microsoft.Win32.RegistryKey mainKey, Microsoft.Win32.RegistryKey? profileKey, string profileValueName, string[] mainFallbackNames)
        {
            var rawList = new List<object>();

            if (profileKey != null)
            {
                var val = profileKey.GetValue(profileValueName);
                if (val != null) rawList.Add(val);
            }

            foreach (var fallbackName in mainFallbackNames)
            {
                var val = mainKey.GetValue(fallbackName);
                if (val != null) rawList.Add(val);
            }

            var entries = new List<string>();
            foreach (var item in rawList)
            {
                if (item is string[] array)
                {
                    foreach (var s in array)
                    {
                        if (!string.IsNullOrEmpty(s))
                        {
                            entries.AddRange(s.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
                        }
                    }
                }
                else if (item is string str && !string.IsNullOrEmpty(str))
                {
                    entries.AddRange(str.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
                }
            }

            return entries.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToArray();
        }

        private void btnLoadFileSeek_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var mainKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Binary Fortress Software\FileSeek");
                if (mainKey == null)
                {
                    MessageBox.Show("FileSeek registry key not found on this system.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 1. Load File Viewer Path
                var openWithExe = mainKey.GetValue("OpenWithLastExeSelected") as string;
                if (!string.IsNullOrEmpty(openWithExe) && File.Exists(openWithExe))
                {
                    FileViewerPath = openWithExe;
                    if (string.IsNullOrEmpty(FileViewerArgs))
                    {
                        FileViewerArgs = "\"$FILE\"";
                    }
                }

                // 2. Load and write all history list values to application config
                var config = System.Configuration.ConfigurationManager.OpenExeConfiguration(System.Configuration.ConfigurationUserLevel.None);

                var profileName = mainKey.GetValue("DefaultProfile") as string ?? "DefaultProfile";
                using var profileKey = mainKey.OpenSubKey(profileName == "DefaultProfile" ? "DefaultProfile" : $@"Profiles\{profileName}");
                
                // Read base paths
                var basePaths = GetRegistryList(mainKey, profileKey, "LastUsedPath", new[] { "PathHistory" });
                if (basePaths.Length > 0)
                {
                    MainWindow.SetConfigValue(config, "HistoryBasePath", string.Join('|', basePaths));
                    MainWindow.SetConfigValue(config, "BasePath", basePaths[0]);
                }

                // Read include files
                var includeFiles = GetRegistryList(mainKey, profileKey, "LastUsedFilesInclude", new[] { "FilesIncludeHistory", "IncludeHistory" });
                if (includeFiles.Length > 0)
                {
                    MainWindow.SetConfigValue(config, "HistoryIncludeFiles", string.Join('|', includeFiles));
                    MainWindow.SetConfigValue(config, "IncludeFiles", includeFiles[0]);
                }

                // Read exclude files
                var excludeFiles = GetRegistryList(mainKey, profileKey, "LastUsedFilesExclude", new[] { "FilesExcludeHistory", "ExcludeHistory" });
                if (excludeFiles.Length > 0)
                {
                    MainWindow.SetConfigValue(config, "HistoryExcludeFiles", string.Join('|', excludeFiles));
                    MainWindow.SetConfigValue(config, "ExcludeFiles", excludeFiles[0]);
                }

                // Read containing texts
                var queries = GetRegistryList(mainKey, profileKey, "LastUsedQuery", new[] { "QueryHistory" });
                if (queries.Length > 0)
                {
                    MainWindow.SetConfigValue(config, "HistoryContainingText", string.Join('|', queries));
                    MainWindow.SetConfigValue(config, "ContainingText", queries[0]);
                }

                // Options (recursiveness, casing, regex)
                if (profileKey != null)
                {
                    var caseSens = profileKey.GetValue("CaseSensitive") as string;
                    if (caseSens != null) MainWindow.SetConfigValue(config, "CaseSensitive", (caseSens == "1").ToString());

                    var subFolders = profileKey.GetValue("SearchSubFolders") as string;
                    if (subFolders != null) MainWindow.SetConfigValue(config, "Recursive", (subFolders == "1").ToString());

                    var isRegex = profileKey.GetValue("IsQueryRegEx") as string;
                    if (isRegex != null) MainWindow.SetConfigValue(config, "RegularExpression", (isRegex == "1").ToString());
                }

                try
                {
                    config.Save();
                    System.Configuration.ConfigurationManager.RefreshSection("appSettings");
                }
                catch {}

                MessageBox.Show("FileSeek settings and search history imported successfully! Click OK on the settings window to apply.", "Import Successful", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to import FileSeek settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
