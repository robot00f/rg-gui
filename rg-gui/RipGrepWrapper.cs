using CliWrap;
using CliWrap.EventStream;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace rg_gui
{
    public class RipGrepWrapper
    {
        public enum FileEncoding
        {
            Auto,
            GBK
        }

        public enum MaxFileSizeUnit
        {
            None,
            B,
            K,
            M,
            G
        }

        public class SearchParameters
        {
            public string StartPath { get; set; } = string.Empty;

            public IEnumerable<string> SearchStrings { get; set; } = Enumerable.Empty<string>();

            public string IncludePatterns { get; set; } = string.Empty;

            public string ExcludePatterns { get; set; } = string.Empty;

            public bool IncludeHiddenFiles { get; set; } = true;

            public bool IgnoreCase { get; set; } = true;

            public bool Recursive { get; set; } = true;

            public bool RegularExpression { get; set; } = true;

            public FileEncoding Encoding { get; set; } = FileEncoding.Auto;

            public int MaxFileSize { get; set; }

            public MaxFileSizeUnit MaxFileSizeUnit { get; set; } = MaxFileSizeUnit.None;
        }

        public class LineResult
        {
            public LineResult(string lineContent)
            {
                LineContent = lineContent;
                TermResults = new();
            }

            public string LineContent { get; }
            public ConcurrentBag<TermResult> TermResults { get; }
        }

        public readonly ConcurrentBag<(string path, string filename, int termIndex)> FilesFound = new();
        public readonly ConcurrentDictionary<(string path, string filename, int lineNumber), LineResult> FileResults = new();
        private int m_searchTermCount;

        public event EventHandler<(string path, string filename)>? FileFound;
        protected void RaiseFileFound(string path, string filename)
        {
            FileFound?.Invoke(this, (path, filename));
        }

        public event EventHandler<(string path, string filename, int lineNumber, string lineContent, IEnumerable<TermResult> termResults)>? LineFound;
        protected void RaiseLineFound(string path, string filename, int lineNumber, string lineContent, IEnumerable<TermResult> termResults)
        {
            LineFound?.Invoke(this, (path, filename, lineNumber, lineContent, termResults));
        }

        private readonly string m_ripGrepPath;

        private readonly object m_pauseLock = new object();
        private volatile bool m_isPaused = false;
        private TaskCompletionSource<bool> m_pauseTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsPaused => m_isPaused;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("ntdll.dll", EntryPoint = "NtSuspendProcess")]
        private static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll", EntryPoint = "NtResumeProcess")]
        private static extern int NtResumeProcess(IntPtr processHandle);

        private static List<int> GetChildRipgrepProcessIds()
        {
            var pids = new List<int>();
            uint currentPid = (uint)Environment.ProcessId;
            IntPtr snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS = 2
            if (snapshot == IntPtr.Zero || snapshot == (IntPtr)(-1))
            {
                return pids;
            }

            try
            {
                PROCESSENTRY32 pe32 = new PROCESSENTRY32();
                pe32.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));

                if (Process32First(snapshot, ref pe32))
                {
                    do
                    {
                        if (pe32.th32ParentProcessID == currentPid && pe32.szExeFile.Contains("rg", StringComparison.OrdinalIgnoreCase))
                        {
                            pids.Add((int)pe32.th32ProcessID);
                        }
                    } while (Process32Next(snapshot, ref pe32));
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }
            return pids;
        }

        public void Pause()
        {
            lock (m_pauseLock)
            {
                if (!m_isPaused)
                {
                    m_isPaused = true;
                    m_pauseTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                    var childPids = GetChildRipgrepProcessIds();
                    foreach (var pid in childPids)
                    {
                        try
                        {
                            using var proc = Process.GetProcessById(pid);
                            NtSuspendProcess(proc.Handle);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to suspend process {pid}: {ex.Message}");
                        }
                    }
                }
            }
        }

        public void Resume()
        {
            lock (m_pauseLock)
            {
                if (m_isPaused)
                {
                    m_isPaused = false;
                    m_pauseTcs.TrySetResult(true);

                    var childPids = GetChildRipgrepProcessIds();
                    foreach (var pid in childPids)
                    {
                        try
                        {
                            using var proc = Process.GetProcessById(pid);
                            NtResumeProcess(proc.Handle);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to resume process {pid}: {ex.Message}");
                        }
                    }
                }
            }
        }

        public Task WaitIfPausedAsync(CancellationToken cancellationToken)
        {
            Task task;
            lock (m_pauseLock)
            {
                if (!m_isPaused)
                {
                    return Task.CompletedTask;
                }
                task = m_pauseTcs.Task;
            }
            return task.WaitAsync(cancellationToken);
        }

        public RipGrepWrapper(string ripGrepPath)
        {
            m_ripGrepPath = ripGrepPath;
            m_pauseTcs.TrySetResult(true);
        }

        public void Clear()
        {
            FilesFound.Clear();
            FileResults.Clear();
            Resume();
        }

        public async Task Search(SearchParameters searchParameters, CancellationToken cancellationToken)
        {
            m_searchTermCount = searchParameters.SearchStrings.Count();
            if (m_searchTermCount == 0 || string.IsNullOrWhiteSpace(searchParameters.StartPath))
            {
                return;
            }

            const string fieldMatchSeparator = "\t";
            var argsBuilder = new StringBuilder();
            argsBuilder.Append("-uu ");
            argsBuilder.Append("--no-heading ");
            argsBuilder.Append("--line-number ");
            argsBuilder.Append($"--field-match-separator=\"{fieldMatchSeparator}\" ");

            if (searchParameters.IgnoreCase)
            {
                argsBuilder.Append("-i ");
            }

            if (searchParameters.IncludeHiddenFiles)
            {
                argsBuilder.Append("--hidden ");
            }

            if (!searchParameters.Recursive)
            {
                argsBuilder.Append("--max-depth=1 ");
            }

            if (!searchParameters.RegularExpression)
            {
                argsBuilder.Append("--fixed-strings ");
            }

            if (!string.IsNullOrWhiteSpace(searchParameters.IncludePatterns))
            {
                argsBuilder.Append("--iglob={");
                argsBuilder.AppendJoin(",", GetSearchPatterns(searchParameters.IncludePatterns));
                argsBuilder.Append("} ");
            }

            if (searchParameters.ExcludePatterns.Any())
            {
                argsBuilder.Append("--iglob=!{");
                argsBuilder.AppendJoin(",", GetSearchPatterns(searchParameters.ExcludePatterns));
                argsBuilder.Append("} ");
            }

            argsBuilder.Append("--color always ");

            if (searchParameters.Encoding != FileEncoding.Auto)
            {
                argsBuilder.Append($"-E {EncodingTypes[searchParameters.Encoding]} ");
            }

            if (searchParameters.MaxFileSizeUnit != MaxFileSizeUnit.None)
            {
                argsBuilder.Append($"--max-filesize {searchParameters.MaxFileSize}{(searchParameters.MaxFileSizeUnit != MaxFileSizeUnit.B ? searchParameters.MaxFileSizeUnit : string.Empty)} ");
            }

            // Append all search terms as individual -e arguments
            var terms = searchParameters.SearchStrings.ToList();
            foreach (var term in terms)
            {
                if (!string.IsNullOrWhiteSpace(term))
                {
                    // Escape term for argument structure if necessary, or just use -e
                    argsBuilder.Append("-e ");
                    argsBuilder.Append(term);
                    argsBuilder.Append(' ');
                }
            }

            // Signal no more flags will be set.
            argsBuilder.Append("-- ");
            argsBuilder.Append($"\"{searchParameters.StartPath}\"");

            var cmd = Cli.Wrap(m_ripGrepPath)
                .WithArguments(argsBuilder.ToString())
                .WithValidation(CommandResultValidation.None);

            try
            {
                await foreach (var cmdEvent in cmd.ListenAsync(Encoding.UTF8, cancellationToken))
                {
                    await WaitIfPausedAsync(cancellationToken);

                    switch (cmdEvent)
                    {
                        case StandardOutputCommandEvent stdOut:
                            {
                                var result = stdOut.Text.Split(fieldMatchSeparator, 3);

                                if (result.Length == 3 &&
                                    !string.IsNullOrWhiteSpace(result[0]) &&
                                    !string.IsNullOrWhiteSpace(result[1]) &&
                                    !string.IsNullOrWhiteSpace(result[2]) &&
                                    int.TryParse(RemoveAnsiColors(result[1]), out int lineNumber)
                                    )
                                {
                                    var fullPath = RemoveAnsiColors(result[0]);
                                    var path = Path.GetDirectoryName(fullPath);
                                    var filename = Path.GetFileName(fullPath);

                                    if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(filename))
                                    {
                                        var cleanLineContent = RemoveAnsiColors(result[2]);

                                        // Find which of our search terms matched this line
                                        var termMatches = new List<TermResult>();
                                        for (int t = 0; t < terms.Count; t++)
                                        {
                                            var termMatchesForThisTerm = GetTermMatches(result[2], cleanLineContent, terms[t], t, searchParameters.IgnoreCase, searchParameters.RegularExpression);
                                            if (termMatchesForThisTerm.Count > 0)
                                            {
                                                termMatches.AddRange(termMatchesForThisTerm);
                                                if (!FilesFound.Contains((path, filename, t)))
                                                {
                                                    FilesFound.Add((path, filename, t));
                                                }
                                            }
                                        }

                                        if (termMatches.Count > 0)
                                        {
                                            // Raise file found event if all unique terms have been found across files, or simply notify on file first discovery
                                            // Let's trigger file found immediately if it's the first time we see this file
                                            if (FilesFound.Count(x => x.path == path && x.filename == filename) > 0)
                                            {
                                                // Check how many unique term indexes have been found for this file
                                                var uniqueTermCountForFile = FilesFound.Where(x => x.path == path && x.filename == filename).Select(x => x.termIndex).Distinct().Count();
                                                if (uniqueTermCountForFile == m_searchTermCount)
                                                {
                                                    RaiseFileFound(path, filename);
                                                }
                                                else
                                                {
                                                    // Also notify immediately for user feedback
                                                    RaiseFileFound(path, filename);
                                                }
                                            }

                                            if (!FileResults.ContainsKey((path, filename, lineNumber)))
                                            {
                                                FileResults.GetOrAdd((path, filename, lineNumber), new LineResult(cleanLineContent));
                                            }

                                            foreach (var termMatch in termMatches)
                                            {
                                                FileResults[(path, filename, lineNumber)].TermResults.Add(termMatch);
                                            }

                                            RaiseLineFound(path, filename, lineNumber, cleanLineContent, termMatches);
                                        }
                                    }
                                }
                            }
                            break;
                        default:
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static readonly char[] PatternDelimiters = { ' ', ':', ';', ',' };

        private static readonly Dictionary<FileEncoding, string> EncodingTypes = new()
        {
            { FileEncoding.Auto, string.Empty },
            { FileEncoding.GBK, "GBK" },
        };

        private static IEnumerable<string> GetSearchPatterns(string patternString)
        {
            var searchPatterns = new List<string>();
            var splitPatternString = patternString.Split(PatternDelimiters, StringSplitOptions.RemoveEmptyEntries);

            var invalidChars = Path.GetInvalidFileNameChars().Where(x => x != Path.DirectorySeparatorChar && x != '*').ToList();
            invalidChars.Add('{');
            invalidChars.Add('}');

            foreach (var token in splitPatternString)
            {
                var pattern = token;

                // Remove any invalid characters from patterns.
                foreach (var c in invalidChars)
                {
                    pattern = pattern.Replace(c.ToString(), string.Empty);
                }

                // Remove any whitespace from patterns.
                pattern = Regex.Replace(pattern, @"\s+", "");

                if (!string.IsNullOrWhiteSpace(pattern))
                {
                    searchPatterns.Add(pattern);
                }
            }

            return searchPatterns;
        }

        private static string RemoveAnsiColors(string source)
        {
            return Regex.Replace(source, @"\x1B\[[^@-~]*[@-~]", string.Empty);
        }

        private static IList<TermResult> GetTermMatches(string coloredSource, string cleanSource, string term, int termIndex, bool ignoreCase, bool isRegex)
        {
            var termMatches = new List<TermResult>();
            if (string.IsNullOrEmpty(term) || string.IsNullOrEmpty(cleanSource)) return termMatches;

            // Strip quotes from the search term if it was quoted
            var cleanTerm = term;
            if (cleanTerm.StartsWith("\"") && cleanTerm.EndsWith("\"") && cleanTerm.Length >= 2)
            {
                cleanTerm = cleanTerm.Substring(1, cleanTerm.Length - 2);
            }

            if (string.IsNullOrEmpty(cleanTerm)) return termMatches;

            try
            {
                // We use regex to find where the cleanTerm matches in cleanSource (plain text)
                string pattern = isRegex ? cleanTerm : Regex.Escape(cleanTerm);
                var options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;

                // Protect against invalid regex input crashes by catching compilation exceptions locally
                Regex regex;
                try
                {
                    regex = new Regex(pattern, options);
                }
                catch (ArgumentException)
                {
                    // Fallback to literal search if regex compilation fails
                    pattern = Regex.Escape(cleanTerm);
                    regex = new Regex(pattern, options);
                }

                var matches = regex.Matches(cleanSource);
                foreach (Match m in matches)
                {
                    termMatches.Add(new TermResult(m.Index, m.Index + m.Length - 1, termIndex));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed matching terms: {ex.Message}");
            }

            return termMatches;
        }
    }
}
