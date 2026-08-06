using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Configuration;
using FramePFX.Themes;

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
        public int MaxParallelProcesses { get; set; }

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

        private bool ValidateInputs()
        {
            if (MaxSearchTerms < 0)
            {
                MessageBox.Show("Maximum search terms must be a non-negative number.");
                return false;
            }

            if (MaxParallelProcesses < 1)
            {
                MessageBox.Show("Maximum parallel processes must be at least 1.");
                return false;
            }

            if (!string.IsNullOrEmpty(FileViewerPath) && !File.Exists(FileViewerPath))
            {
                MessageBox.Show("Invalid file viewer path.");
                return false;
            }

            if (!string.IsNullOrEmpty(FileViewerArgs) && !FileViewerArgs.Contains("$FILE"))
            {
                MessageBox.Show("Invalid file viewer arguments.");
                return false;
            }

            return true;
        }

        private Configuration? m_importedConfig = null;

        private void btnOK_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs())
            {
                return;
            }

            if (m_importedConfig != null)
            {
                try
                {
                    m_importedConfig.Save(ConfigurationSaveMode.Modified, true);
                    ConfigurationManager.RefreshSection("appSettings");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to save imported configuration: {ex.Message}");
                }
            }

            this.DialogResult = true;
        }

        private void btnApply_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs())
            {
                return;
            }

            if (Enum.TryParse<ThemeType>(Theme, out var themeType))
            {
                MainWindow.CurrentTheme = themeType;
            }

            MainWindow.MaxSearchTerms = MaxSearchTerms;
            MainWindow.MultipleHighlightColors = Multicolor;
            MainWindow.MaxLineHighlights = MaxLineHighlights;
            MainWindow.MaxParallelProcesses = MaxParallelProcesses;
            MainWindow.FileViewerPath = FileViewerPath;
            MainWindow.FileViewerArgs = FileViewerArgs;

            if (m_importedConfig != null)
            {
                try
                {
                    m_importedConfig.Save(ConfigurationSaveMode.Modified, true);
                    ConfigurationManager.RefreshSection("appSettings");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to save imported configuration: {ex.Message}");
                }
            }
            else
            {
                var exePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rg-gui.config");
                var fileMap = new ExeConfigurationFileMap { ExeConfigFilename = exePath };
                var config = ConfigurationManager.OpenMappedExeConfiguration(fileMap, ConfigurationUserLevel.None);
                MainWindow.SaveGlobalConfig(config);

                try
                {
                    config.Save(ConfigurationSaveMode.Modified, true);
                    ConfigurationManager.RefreshSection("appSettings");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to save configuration: {ex.Message}");
                }
            }
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

        private void txtMaxParallelProcesses_TextChanged(object sender, TextChangedEventArgs e)
        {
            var input = txtMaxParallelProcesses.Text;
            txtMaxParallelProcesses.Text = new string(input.Where(c => char.IsDigit(c)).ToArray());
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

                // 2. Read registry profiles to local config file only on demand
                var exePathConfig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rg-gui.config");
                var fileMapConfig = new ExeConfigurationFileMap { ExeConfigFilename = exePathConfig };
                var config = System.Configuration.ConfigurationManager.OpenMappedExeConfiguration(fileMapConfig, System.Configuration.ConfigurationUserLevel.None);

                var profileName = mainKey.GetValue("DefaultProfile") as string ?? "DefaultProfile";
                using var profileKey = mainKey.OpenSubKey(profileName == "DefaultProfile" ? "DefaultProfile" : $@"Profiles\{profileName}");
                
                var basePaths = GetRegistryList(mainKey, profileKey, "LastUsedPath", new[] { "PathHistory" });
                if (basePaths.Length > 0)
                {
                    MainWindow.SetConfigValue(config, "HistoryBasePath", string.Join('|', basePaths));
                    MainWindow.SetConfigValue(config, "BasePath", basePaths[0]);
                }

                var includeFiles = GetRegistryList(mainKey, profileKey, "LastUsedFilesInclude", new[] { "FilesIncludeHistory", "IncludeHistory" });
                if (includeFiles.Length > 0)
                {
                    MainWindow.SetConfigValue(config, "HistoryIncludeFiles", string.Join('|', includeFiles));
                    MainWindow.SetConfigValue(config, "IncludeFiles", includeFiles[0]);
                }

                var excludeFiles = GetRegistryList(mainKey, profileKey, "LastUsedFilesExclude", new[] { "FilesExcludeHistory", "ExcludeHistory" });
                if (excludeFiles.Length > 0)
                {
                    MainWindow.SetConfigValue(config, "HistoryExcludeFiles", string.Join('|', excludeFiles));
                    MainWindow.SetConfigValue(config, "ExcludeFiles", excludeFiles[0]);
                }

                var queries = GetRegistryList(mainKey, profileKey, "LastUsedQuery", new[] { "QueryHistory" });
                if (queries.Length > 0)
                {
                    MainWindow.SetConfigValue(config, "HistoryContainingText", string.Join('|', queries));
                    MainWindow.SetConfigValue(config, "ContainingText", queries[0]);
                }

                if (profileKey != null)
                {
                    var caseSens = profileKey.GetValue("CaseSensitive")?.ToString();
                    if (caseSens != null) MainWindow.SetConfigValue(config, "CaseSensitive", (caseSens == "1" || caseSens.Equals("True", StringComparison.OrdinalIgnoreCase)).ToString());

                    var subFolders = profileKey.GetValue("SearchSubFolders")?.ToString();
                    if (subFolders != null) MainWindow.SetConfigValue(config, "Recursive", (subFolders == "1" || subFolders.Equals("True", StringComparison.OrdinalIgnoreCase)).ToString());

                    var isRegex = profileKey.GetValue("IsQueryRegEx")?.ToString();
                    if (isRegex != null) MainWindow.SetConfigValue(config, "RegularExpression", (isRegex == "1" || isRegex.Equals("True", StringComparison.OrdinalIgnoreCase)).ToString());
                }

                if (!string.IsNullOrEmpty(FileViewerPath)) MainWindow.SetConfigValue(config, "FileViewerPath", FileViewerPath);
                if (!string.IsNullOrEmpty(FileViewerArgs)) MainWindow.SetConfigValue(config, "FileViewerArgs", FileViewerArgs);

                // Hold imported config in memory instance (saved when clicking OK or Apply)
                m_importedConfig = config;

                MessageBox.Show("FileSeek settings and search history imported to staging! Click OK or Apply to finalize and save to disk.", "Import Successful", MessageBoxButton.OK, MessageBoxImage.Information);
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
