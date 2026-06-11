using System.CommandLine;
using System.Windows;

namespace rg_gui
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected void Application_Startup(object sender, StartupEventArgs e)
        {
            var inFolderOption = new Option<string>("--folder", "-f")
            {
                Description = "Folder to search in"
            };
            var includeFilesOption = new Option<string>("--include-files", "-i")
            {
                Description = "Files/subfolders to include"
            };
            var excludeFilesOption = new Option<string>("--exclude-files", "-e")
            {
                Description = "Files/subfolders to exclude"
            };
            var containingTextOption = new Option<string>("--text", "-t")
            {
                Description = "Text to search for"
            };

            var rootCommand = new RootCommand("rg-gui: A Simple RipGrep GUI for Windows");
            rootCommand.Options.Clear();
            rootCommand.Directives.Clear();
            rootCommand.Add(inFolderOption);
            rootCommand.Add(includeFilesOption);
            rootCommand.Add(excludeFilesOption);
            rootCommand.Add(containingTextOption);

            var parseResult = rootCommand.Parse(e.Args);
            string? inFolder = null;
            string? includeFiles = null;
            string? excludeFiles = null;
            string? containingText = null;

            if (parseResult.Errors.Count == 0)
            {
                inFolder = parseResult.GetValue(inFolderOption);
                includeFiles = parseResult.GetValue(includeFilesOption);
                excludeFiles = parseResult.GetValue(excludeFilesOption);
                containingText = parseResult.GetValue(containingTextOption);
            }

            Dispatcher.Invoke(() =>
            {
                var window = new MainWindow(inFolder, includeFiles, excludeFiles, containingText);
                window.Show();
            });
        }
    }
}
