// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text;

namespace XMAT.WebServiceCapture.Proxy
{
    public class HeaderCollection : IEnumerable, IEnumerable<KeyValuePair<string, IEnumerable<string>>>
    {
        private readonly Dictionary<string, IEnumerable<string>> _headers = new(StringComparer.OrdinalIgnoreCase);

        public string this[string key]
        {
            get => GetHeaderValuesAsString(key);
            set => _headers[key] = new List<string>() { value };
        }

        public string GetHeaderValuesAsString(string key)
        {
            if (_headers.TryGetValue(key, out var list))
                return string.Join(';', list);
            else
                return string.Empty;
        }

        public IEnumerable<string> GetHeaderValuesAsList(string key)
        {
            if (_headers.TryGetValue(key, out var list))
                return list;
            else
                return null;
        }

        public void Add(string key, string value)
        {
            if (_headers.TryGetValue(key, out var existing))
            {
                var values = new List<string>(existing) { value };
                _headers[key] = values;
                return;
            }

            _headers[key] = new List<string> { value };
        }

        public void CopyTo(HttpHeaders headers)
        {
            if (headers == null)
                return;

            foreach (var kvp in _headers)
            {
                if (!headers.TryAddWithoutValidation(kvp.Key, kvp.Value))
                {
                    PublicUtilities.AppLog(LogLevel.ERROR, $"(CopyTo) Failed to add request header [{kvp.Key}]:[{kvp.Value}]");
                }
            }
        }

        public void CopyFrom(HttpHeaders headers)
        {
            if (headers == null)
                return;

            foreach (var kvp in headers)
            {
                _headers[kvp.Key] = kvp.Value;
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();

            foreach (var kvp in _headers)
            {
                foreach (string value in kvp.Value)
                {
                    sb.Append($"{kvp.Key}: {value}\r\n");
                }
            }
            return sb.ToString();
        }

        public IEnumerator GetEnumerator()
        {
            return _headers.GetEnumerator();
        }

        IEnumerator<KeyValuePair<string, IEnumerable<string>>> IEnumerable<KeyValuePair<string, IEnumerable<string>>>.GetEnumerator()
        {
            return _headers.GetEnumerator();
        }
    }
}
