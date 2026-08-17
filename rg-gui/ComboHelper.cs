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

        public static bool IsAlreadyCombo(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            var trimmed = line.Trim();

            if (trimmed.Contains("://") && trimmed.Count(c => c == ':') >= 2)
            {
                return true;
            }

            if (trimmed.Contains('@') && trimmed.Contains(':') && !trimmed.Contains(' '))
            {
                var parts = trimmed.Split(':');
                if (parts.Length >= 2 && parts[0].Contains('@'))
                {
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

            int maxLookahead = Math.Min(5, items.Count - startIndex);
            int matchCount = 0;
            int lastMatchedOffset = 0;

            for (int offset = 0; offset < maxLookahead; offset++)
            {
                var item = items[startIndex + offset];
                if (item.Key.path != currentPath || item.Key.filename != currentFile)
                {
                    break;
                }

                if (offset > 0 && item.Key.lineNumber > items[startIndex + offset - 1].Key.lineNumber + 5)
                {
                    break;
                }

                string line = item.Value.LineContent.Trim();

                if (foundUrl == null && TryGetPrefixValue(line, UrlPrefixes, out var urlVal))
                {
                    foundUrl = urlVal;
                    matchCount++;
                    lastMatchedOffset = offset;
                    CollectTerms(item.Value.TermResults, termResultsAcc);
                }
                else if (foundUser == null && TryGetPrefixValue(line, UserPrefixes, out var userVal))
                {
                    foundUser = userVal;
                    matchCount++;
                    lastMatchedOffset = offset;
                    CollectTerms(item.Value.TermResults, termResultsAcc);
                }
                else if (foundPass == null && TryGetPrefixValue(line, PassPrefixes, out var passVal))
                {
                    foundPass = passVal;
                    matchCount++;
                    lastMatchedOffset = offset;
                    CollectTerms(item.Value.TermResults, termResultsAcc);
                }
                else if (IsAlreadyCombo(line))
                {
                    if (matchCount == 0)
                    {
                        comboText = line;
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

            if (foundUser != null && foundPass != null)
            {
                if (!string.IsNullOrEmpty(foundUrl))
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

        private static void CollectTerms(IEnumerable<TermResult> source, List<TermResult> target)
        {
            if (source != null)
            {
                target.AddRange(source);
            }
        }
    }
}
