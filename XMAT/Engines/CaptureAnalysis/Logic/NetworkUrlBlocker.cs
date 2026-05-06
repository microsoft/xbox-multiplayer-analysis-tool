// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using XMAT;

namespace XMAT.NetworkTrace
{
    public class NetworkUrlBlocker : INotifyPropertyChanged
    {
        private static readonly NetworkUrlBlocker _instance = new NetworkUrlBlocker();
        public static NetworkUrlBlocker Instance => _instance;

        private readonly HashSet<string> _blockedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Regex> _blockedPatterns = new List<Regex>();

        public ObservableCollection<string> BlockedUrls { get; } = new ObservableCollection<string>();

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private NetworkUrlBlocker() { }

        public void BlockUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            url = NormalizeUrl(url);

            if (_blockedUrls.Add(url))
            {
                PublicUtilities.AppLog(LogLevel.INFO, $"URL blocked: {url}");

                try
                {
                    // Create regex pattern to match this URL
                    string pattern = "^" + Regex.Escape(url).Replace("\\*", ".*") + "$";
                    var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                    _blockedPatterns.Add(regex);

                    PublicUtilities.SafeInvoke(() => BlockedUrls.Add(url));
                }
                catch (Exception ex)
                {
                    PublicUtilities.AppLog(LogLevel.ERROR, $"Failed to create URL blocking pattern: {ex.Message}");
                    _blockedUrls.Remove(url);
                }
            }
        }

        public void UnblockUrl(string url)
        {
            url = NormalizeUrl(url);

            if (_blockedUrls.Remove(url))
            {
                PublicUtilities.AppLog(LogLevel.INFO, $"URL unblocked: {url}");

                // Rebuild patterns list
                _blockedPatterns.Clear();
                foreach (var blockedUrl in _blockedUrls)
                {
                    string pattern = "^" + Regex.Escape(blockedUrl).Replace("\\*", ".*") + "$";
                    _blockedPatterns.Add(new Regex(pattern, RegexOptions.IgnoreCase));
                }

                PublicUtilities.SafeInvoke(() => BlockedUrls.Remove(url));
            }
        }

        public bool IsUrlBlocked(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            url = NormalizeUrl(url);

            if (_blockedUrls.Contains(url))
                return true;

            // Check regex patterns
            foreach (var pattern in _blockedPatterns)
            {
                if (pattern.IsMatch(url))
                    return true;
            }

            return false;
        }

        public void ClearAllBlockedUrls()
        {
            _blockedUrls.Clear();
            _blockedPatterns.Clear();

            PublicUtilities.SafeInvoke(() => BlockedUrls.Clear());
            PublicUtilities.AppLog(LogLevel.INFO, "All blocked URLs cleared");
        }

        private string NormalizeUrl(string url)
        {
            // Remove protocol if present
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                url = url.Substring(7);
            else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = url.Substring(8);

            // Remove trailing slash
            if (url.EndsWith("/"))
                url = url.Substring(0, url.Length - 1);

            return url;
        }
    }
}