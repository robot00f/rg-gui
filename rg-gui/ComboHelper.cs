using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using static rg_gui.RipGrepWrapper;

namespace rg_gui
{
    public static class ComboHelper
    {
        private static readonly string[] UrlPrefixes = new[]
        {
            "url:", "url :", "url=", "host:", "host :", "host=", "site:", "site :", "website:", "website :", "link:", "target:"
        };

        private static readonly string[] UserPrefixes = new[]
        {
            "user:", "user :", "user=", "username:", "username :", "username=", "login:", "login :", "login=",
            "usr:", "usr :", "account:", "account :", "email:", "email :", "mail:", "mail :"
        };

        private static readonly string[] PassPrefixes = new[]
        {
            "pass:", "pass :", "pass=", "password:", "password :", "password=", "pwd:", "pwd :", "pwd=",
            "secret:", "secret :", "key:", "clave:", "contraseña:", "contrasena:"
        };

        public static bool TryGetPrefixValue(string line, string[] prefixes, out string value)
        {
            value = string.Empty;
            if (string.IsNullOrWhiteSpace(line)) return false;
            var trimmed = line.Trim();
            foreach (var prefix in prefixes)
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    value = trimmed.Substring(prefix.Length).Trim();
                    return true;
                }
            }
            return false;
        }

        public static bool IsAlreadyCombo(string line, out string combo)
        {
            combo = string.Empty;
            if (string.IsNullOrWhiteSpace(line)) return false;
            var trimmed = line.Trim();

            // URL:USER:PASS format (e.g. https://domain.com/path:user@email.com:password)
            if (trimmed.Contains("://") && trimmed.Count(c => c == ':') >= 2)
            {
                combo = trimmed;
                return true;
            }

            // USER:PASS with email (e.g. user@domain.com:password without spaces)
            if (trimmed.Contains('@') && trimmed.Contains(':') && !trimmed.Contains(' '))
            {
                var parts = trimmed.Split(':');
                if (parts.Length >= 2 && parts[0].Contains('@') && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    combo = trimmed;
                    return true;
                }
            }

            return false;
        }

        public static bool TryExtractComboBlock(
            List<KeyValuePair<(string path, string filename, int lineNumber), LineResult>> items,
            int startIndex,
            out string comboText,
            out List<TermResult> mergedTerms,
            out int consumedCount)
        {
            comboText = string.Empty;
            mergedTerms = new List<TermResult>();
            consumedCount = 1;

            if (startIndex >= items.Count) return false;

            var firstItem = items[startIndex];
            string currentPath = firstItem.Key.path;
            string currentFile = firstItem.Key.filename;

            string? foundUrl = null;
            string? foundUser = null;
            string? foundPass = null;
            var termResultsAcc = new List<TermResult>();

            int maxLookahead = Math.Min(6, items.Count - startIndex);
            int lastMatchedOffset = 0;

            for (int offset = 0; offset < maxLookahead; offset++)
            {
                var item = items[startIndex + offset];
                if (item.Key.path != currentPath || item.Key.filename != currentFile)
                {
                    break;
                }

                if (offset > 0 && item.Key.lineNumber > items[startIndex + offset - 1].Key.lineNumber + 6)
                {
                    break;
                }

                string line = item.Value.LineContent.Trim();

                if (foundUrl == null && TryGetPrefixValue(line, UrlPrefixes, out var urlVal))
                {
                    if (!string.IsNullOrWhiteSpace(urlVal))
                    {
                        foundUrl = urlVal;
                        lastMatchedOffset = offset;
                        CollectTerms(item.Value.TermResults, termResultsAcc);
                    }
                }
                else if (foundUser == null && TryGetPrefixValue(line, UserPrefixes, out var userVal))
                {
                    if (!string.IsNullOrWhiteSpace(userVal))
                    {
                        foundUser = userVal;
                        lastMatchedOffset = offset;
                        CollectTerms(item.Value.TermResults, termResultsAcc);
                    }
                }
                else if (foundPass == null && TryGetPrefixValue(line, PassPrefixes, out var passVal))
                {
                    if (!string.IsNullOrWhiteSpace(passVal))
                    {
                        foundPass = passVal;
                        lastMatchedOffset = offset;
                        CollectTerms(item.Value.TermResults, termResultsAcc);
                    }
                }
                else if (IsAlreadyCombo(line, out var existingCombo))
                {
                    if (foundUrl == null && foundUser == null && foundPass == null)
                    {
                        comboText = existingCombo;
                        mergedTerms = item.Value.TermResults.ToList();
                        consumedCount = 1;
                        return true;
                    }
                }

                if (foundUser != null && foundPass != null)
                {
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(foundUser) && !string.IsNullOrWhiteSpace(foundPass))
            {
                if (!string.IsNullOrWhiteSpace(foundUrl))
                {
                    comboText = $"{foundUrl}:{foundUser}:{foundPass}";
                }
                else
                {
                    comboText = $"{foundUser}:{foundPass}";
                }

                mergedTerms = termResultsAcc;
                consumedCount = Math.Max(1, lastMatchedOffset + 1);
                return true;
            }

            return false;
        }

        public static List<string> ExtractAllCombos(IEnumerable<KeyValuePair<(string path, string filename, int lineNumber), LineResult>> items)
        {
            var sorted = items.OrderBy(x => x.Key.path).ThenBy(x => x.Key.filename).ThenBy(x => x.Key.lineNumber).ToList();
            var combos = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int i = 0;
            while (i < sorted.Count)
            {
                var current = sorted[i];
                string cleanContent = current.Value.LineContent.Trim();

                if (IsAlreadyCombo(cleanContent, out var existingCombo))
                {
                    if (seen.Add(existingCombo))
                    {
                        combos.Add(existingCombo);
                    }
                    i++;
                    continue;
                }

                if (TryExtractComboBlock(sorted, i, out var comboText, out _, out int consumedCount))
                {
                    if (!string.IsNullOrWhiteSpace(comboText) && seen.Add(comboText))
                    {
                        combos.Add(comboText);
                    }
                    i += consumedCount;
                }
                else
                {
                    i++;
                }
            }

            return combos;
        }

        public static List<string> ExtractCombosFromStrings(IEnumerable<string> lines)
        {
            var combos = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var lineList = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
            int i = 0;
            while (i < lineList.Count)
            {
                string line = lineList[i];

                if (IsAlreadyCombo(line, out var existing))
                {
                    if (seen.Add(existing))
                    {
                        combos.Add(existing);
                    }
                    i++;
                    continue;
                }

                string? url = null;
                string? user = null;
                string? pass = null;
                int lookahead = Math.Min(6, lineList.Count - i);
                int lastOffset = 0;

                for (int offset = 0; offset < lookahead; offset++)
                {
                    string candidate = lineList[i + offset];
                    if (url == null && TryGetPrefixValue(candidate, UrlPrefixes, out var u) && !string.IsNullOrWhiteSpace(u))
                    {
                        url = u;
                        lastOffset = offset;
                    }
                    else if (user == null && TryGetPrefixValue(candidate, UserPrefixes, out var us) && !string.IsNullOrWhiteSpace(us))
                    {
                        user = us;
                        lastOffset = offset;
                    }
                    else if (pass == null && TryGetPrefixValue(candidate, PassPrefixes, out var p) && !string.IsNullOrWhiteSpace(p))
                    {
                        pass = p;
                        lastOffset = offset;
                    }

                    if (user != null && pass != null)
                    {
                        break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
                {
                    string combo = !string.IsNullOrWhiteSpace(url) ? $"{url}:{user}:{pass}" : $"{user}:{pass}";
                    if (seen.Add(combo))
                    {
                        combos.Add(combo);
                    }
                    i += Math.Max(1, lastOffset + 1);
                }
                else
                {
                    i++;
                }
            }

            return combos;
        }

        private static void CollectTerms(IEnumerable<TermResult> source, List<TermResult> target)
        {
            if (source != null)
            {
                target.AddRange(source);
            }
        }
    }
}
