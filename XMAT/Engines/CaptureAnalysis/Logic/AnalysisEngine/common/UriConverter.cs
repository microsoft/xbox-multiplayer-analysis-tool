// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace CaptureAnalysisEngine
{
    internal class UriToMethodConverter
    {
        private static readonly Regex OptionalSegment =
            new(
                "\\[.*?\\]");
        private static readonly Regex RequiredSegment =
            new(
                "\\{.*?\\}");

        public void LoadMap(String filePath)
        {
            using (var mapFile = new StreamReader(filePath))
            {
                LoadMap(mapFile);
            }
        }

        public void LoadMap(StreamReader mapFile)
        {
            while (mapFile.EndOfStream == false)
            {
                String line = mapFile.ReadLine();

                var columns = Utils.GetCSVValues(line);

                // Not a value row if there arent 4 columns
                if (columns.Length != 4)
                {
                    continue;
                }

                String uri = columns[0];
                String cppMethod = columns[1];
                String winrtMethod = columns[2];
                String cMethod = columns[3];

                var methodTuple = new Tuple<String, String, String>(cppMethod, winrtMethod, cMethod);

                // If it has a space then its a specific endpoint and not a service
                if (uri.Contains(" "))
                {
                    var uriSplit = uri.Split(new char[] { ' ' });

                    if (uriSplit[0] == "GET")
                    {
                        m_getMethods.Add(
                            new MethodPattern(
                                ConvertUriToRegex(
                                    uriSplit[1]),
                                methodTuple));
                    }
                    else
                    {
                        m_nonGetMethods.Add(
                            new MethodPattern(
                                ConvertUriToRegex(
                                    uriSplit[1]),
                                methodTuple));
                    }
                }
                else
                {
                    m_services.Add(uri, methodTuple);
                }
            }
        }

        private InputRegex ConvertUriToRegex(String uri)
        {
            uri = uri.Replace(
                ".",
                "\\.",
                StringComparison.Ordinal);
            uri = uri.Replace(
                "?",
                "\\?",
                StringComparison.Ordinal);
            uri = OptionalSegment.Replace(
                uri,
                ".*");
            uri = RequiredSegment.Replace(
                uri,
                ".+");

            return new InputRegex(uri);
        }

        public Tuple<String, String, String> GetService(String service)
        {
            if (m_services.ContainsKey(service))
            {
                return m_services[service];
            }
            return null;
        }

        public Dictionary<String, Tuple<String, String, String>> GetServices()
        {
            return m_services;
        }

        public Tuple<String, String, String> GetMethod(String uri, bool get)
        {
            return GetMethod(
                uri,
                get,
                null,
                CancellationToken.None);
        }

        internal Tuple<String, String, String> GetMethod(
            String uri,
            bool get,
            AnalysisWorkBudget budget,
            CancellationToken cancellationToken)
        {
            return FindMatch(
                get
                    ? m_getMethods
                    : m_nonGetMethods,
                uri,
                budget,
                cancellationToken);
        }

        internal int MatcherCreationCount =>
            m_getMethods.Count(
                pattern =>
                    pattern.Pattern.MatcherCreated) +
            m_nonGetMethods.Count(
                pattern =>
                    pattern.Pattern.MatcherCreated);

        internal int ExactEvaluationCount =>
            m_getMethods.Sum(
                pattern =>
                    pattern.Pattern.ExactEvaluationCount) +
            m_nonGetMethods.Sum(
                pattern =>
                    pattern.Pattern.ExactEvaluationCount);

        private static Tuple<String, String, String> FindMatch(
            IEnumerable<MethodPattern> map,
            String match,
            AnalysisWorkBudget budget,
            CancellationToken cancellationToken)
        {
            foreach (var value in map)
            {
                if (value.Pattern.IsMatch(
                        match,
                        budget,
                        cancellationToken))
                {
                    return value.Methods;
                }
            }
            return null;
        }

        private sealed record MethodPattern(
            InputRegex Pattern,
            Tuple<String, String, String> Methods);

        private Dictionary<String, Tuple<String, String, String>> m_services = new Dictionary<String, Tuple<String, String, String>>();
        private List<MethodPattern> m_getMethods = new();
        private List<MethodPattern> m_nonGetMethods = new();
    }
}
