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

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
