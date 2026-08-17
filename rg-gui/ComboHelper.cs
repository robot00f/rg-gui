using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using static rg_gui.RipGrepWrapper;

namespace rg_gui
{
    public static class ComboHelper
    {
        public static readonly string[] UrlPrefixes = new[]
        {
            "url:", "url :", "url=", "host:", "host :", "host=", "site:", "site :", "website:", "target:"
        };

        public static readonly string[] UserPrefixes = new[]
        {
            "user:", "user :", "user=", "username:", "username :", "login:", "login :", "email:", "email :", "usr:", "usr :", "account:"
        };

        public static readonly string[] PassPrefixes = new[]
        {
            "pass:", "pass :", "pass=", "password:", "password :", "pwd:", "pwd :", "clave:", "contraseña:", "contrasena:", "secret:", "key:"
        };

        public static readonly string[] IgnorePrefixes = new[]
        {
            "soft:", "browser:", "profile:", "application:", "app:", "title:", "time:", "path:", "created:", "modified:", "========", "--------"
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

        public static bool TrySplitUserPass(string line, out string user, out string pass)
        {
            user = string.Empty;
            pass = string.Empty;
            if (string.IsNullOrWhiteSpace(line)) return false;
            var trimmed = line.Trim();

            // If line starts with URL/Host/User/Pass/Ignore prefix or http:// or android://, it's not raw user:pass
            if (UrlPrefixes.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
                UserPrefixes.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
                PassPrefixes.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
                IgnorePrefixes.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
                trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("android://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int firstColon = trimmed.IndexOf(':');
            if (firstColon > 0 && firstColon < trimmed.Length - 1)
            {
                string u = trimmed.Substring(0, firstColon).Trim();
                string p = trimmed.Substring(firstColon + 1).Trim();

                if (!string.IsNullOrWhiteSpace(u) && !string.IsNullOrWhiteSpace(p) && !u.Contains(' '))
                {
                    user = u;
                    pass = p;
                    return true;
                }
            }
            return false;
        }

        public static bool IsFullUrlCombo(string line, out string combo)
        {
            combo = string.Empty;
            if (string.IsNullOrWhiteSpace(line)) return false;
            var trimmed = line.Trim();

            if ((trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.StartsWith("android://", StringComparison.OrdinalIgnoreCase)) &&
                trimmed.Count(c => c == ':') >= 2)
            {
                combo = trimmed;
                return true;
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

                // 1. Check for complete inline URL combo (e.g. https://site.com:user:pass)
                if (offset == 0 && IsFullUrlCombo(line, out var fullCombo))
                {
                    comboText = fullCombo;
                    mergedTerms = item.Value.TermResults.ToList();
                    consumedCount = 1;
                    return true;
                }

                // 2. Check for URL/Host prefix line
                if (foundUrl == null && TryGetPrefixValue(line, UrlPrefixes, out var urlVal))
                {
                    if (!string.IsNullOrWhiteSpace(urlVal))
                    {
                        foundUrl = urlVal;
                        lastMatchedOffset = offset;
                        CollectTerms(item.Value.TermResults, termResultsAcc);
                        continue;
                    }
                }

                // 3. Check for User prefix line (3-line format)
                if (foundUser == null && TryGetPrefixValue(line, UserPrefixes, out var userVal))
                {
                    if (!string.IsNullOrWhiteSpace(userVal))
                    {
                        foundUser = userVal;
                        lastMatchedOffset = offset;
                        CollectTerms(item.Value.TermResults, termResultsAcc);
                        continue;
                    }
                }

                // 4. Check for Pass prefix line (3-line format)
                if (foundPass == null && TryGetPrefixValue(line, PassPrefixes, out var passVal))
                {
                    if (!string.IsNullOrWhiteSpace(passVal))
                    {
                        foundPass = passVal;
                        lastMatchedOffset = offset;
                        CollectTerms(item.Value.TermResults, termResultsAcc);
                        continue;
                    }
                }

                // 5. If no User/Pass prefix was found yet, check for raw "user:pass" line (2-line format)
                if (foundUser == null && foundPass == null && TrySplitUserPass(line, out var splitUser, out var splitPass))
                {
                    foundUser = splitUser;
                    foundPass = splitPass;
                    lastMatchedOffset = offset;
                    CollectTerms(item.Value.TermResults, termResultsAcc);
                    break;
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

                if (IsFullUrlCombo(line, out var fullCombo))
                {
                    if (seen.Add(fullCombo))
                    {
                        combos.Add(fullCombo);
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
                        continue;
                    }

                    if (user == null && TryGetPrefixValue(candidate, UserPrefixes, out var us) && !string.IsNullOrWhiteSpace(us))
                    {
                        user = us;
                        lastOffset = offset;
                        continue;
                    }

                    if (pass == null && TryGetPrefixValue(candidate, PassPrefixes, out var p) && !string.IsNullOrWhiteSpace(p))
                    {
                        pass = p;
                        lastOffset = offset;
                        continue;
                    }

                    if (user == null && pass == null && TrySplitUserPass(candidate, out var su, out var sp))
                    {
                        user = su;
                        pass = sp;
                        lastOffset = offset;
                        break;
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
