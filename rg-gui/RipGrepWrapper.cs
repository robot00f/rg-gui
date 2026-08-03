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
            var terms = searchParameters.SearchStrings.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (terms.Count == 0) return;

            var cmd = Cli.Wrap(m_ripGrepPath)
                .WithArguments(args =>
                {
                    args.Add("-uu");
                    args.Add("--no-heading");
                    args.Add("--line-number");
                    args.Add($"--field-match-separator={fieldMatchSeparator}");

                    if (searchParameters.IgnoreCase)
                    {
                        args.Add("-i");
                    }

                    if (searchParameters.IncludeHiddenFiles)
                    {
                        args.Add("--hidden");
                    }

                    if (!searchParameters.Recursive)
                    {
                        args.Add("--max-depth=1");
                    }

                    if (!searchParameters.RegularExpression)
                    {
                        args.Add("--fixed-strings");
                    }

                    if (!string.IsNullOrWhiteSpace(searchParameters.IncludePatterns))
                    {
                        var inc = GetSearchPatterns(searchParameters.IncludePatterns).ToList();
                        if (inc.Count > 0)
                        {
                            args.Add($"--iglob={{{string.Join(",", inc)}}}");
                        }
                    }

                    if (searchParameters.ExcludePatterns.Any())
                    {
                        var exc = GetSearchPatterns(searchParameters.ExcludePatterns).ToList();
                        if (exc.Count > 0)
                        {
                            args.Add($"--iglob=!{{{string.Join(",", exc)}}}");
                        }
                    }

                    args.Add("--color");
                    args.Add("always");

                    if (searchParameters.Encoding != FileEncoding.Auto)
                    {
                        args.Add("-E");
                        args.Add(EncodingTypes[searchParameters.Encoding]);
                    }

                    if (searchParameters.MaxFileSizeUnit != MaxFileSizeUnit.None)
                    {
                        args.Add("--max-filesize");
                        args.Add($"{searchParameters.MaxFileSize}{(searchParameters.MaxFileSizeUnit != MaxFileSizeUnit.B ? searchParameters.MaxFileSizeUnit : string.Empty)}");
                    }

                    foreach (var term in terms)
                    {
                        args.Add("-e");
                        args.Add(term);
                    }

                    args.Add("--");
                    args.Add(searchParameters.StartPath);
                })
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
                                                    // Intentionally empty: tracked inside lock (FilesFound) below
                                                }
                                            }
                                        }

                                        if (termMatches.Count > 0)
                                        {
                                            // Trigger file found event once per unique file
                                            bool isFirstDiscovery = false;
                                            lock (FilesFound)
                                            {
                                                if (!FilesFound.Any(x => x.path == path && x.filename == filename))
                                                {
                                                    isFirstDiscovery = true;
                                                }

                                                foreach (var termMatch in termMatches)
                                                {
                                                    if (!FilesFound.Contains((path, filename, termMatch.TermIndex)))
                                                    {
                                                        FilesFound.Add((path, filename, termMatch.TermIndex));
                                                    }
                                                }
                                            }

                                            if (isFirstDiscovery)
                                            {
                                                RaiseFileFound(path, filename);
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
            catch (System.ComponentModel.Win32Exception ex)
            {
                Debug.WriteLine($"Win32Exception while launching rg.exe: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception in RipGrep search execution: {ex.Message}");
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
