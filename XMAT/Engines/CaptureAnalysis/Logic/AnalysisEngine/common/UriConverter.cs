// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace CaptureAnalysisEngine
{
    internal class UriToMethodConverter
    {
        public void LoadMap(String filePath)
        {
            using (var mapFile = new StreamReader(filePath))
            {
                LoadMap(mapFile);
            }
        }
        
        public void LoadMap(StreamReader mapFile)
        {
            while(mapFile.EndOfStream == false)
            {
                String line = mapFile.ReadLine();

                var columns = Utils.GetCSVValues(line);

                // Not a value row if there arent 4 columns
                if(columns.Length != 4)
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

                    if(uriSplit[0] == "GET")
                    {
                        m_getMethods.Add(ConvertUriToRegex(uriSplit[1]), methodTuple);
                    }
                    else
                    {
                        m_nonGetMethods.Add(ConvertUriToRegex(uriSplit[1]), methodTuple);
                    }
                }
                else
                {
                    m_services.Add(uri, methodTuple);
                }
            }
        }

        private Regex ConvertUriToRegex(String uri)
        {
            Regex dotReplace = new Regex("\\.");
            uri = dotReplace.Replace(uri, "\\.");

            Regex questionReplace = new Regex("\\?");
            uri = questionReplace.Replace(uri, "\\?");

            Regex optionalReplace = new Regex("\\[.*?\\]");
            uri = optionalReplace.Replace(uri, ".*");

            Regex requiredReplace = new Regex("\\{.*?\\}");

            uri = requiredReplace.Replace(uri, ".+");

            return new Regex(uri);
        }

        public Tuple<String, String, String> GetService(String service)
        {
            if(m_services.ContainsKey(service))
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
            return FindMatch(get ? m_getMethods : m_nonGetMethods, uri);
        }

        private Tuple<String, String, String> FindMatch(Dictionary<Regex, Tuple<String,String, String>> map, String match)
        {
            foreach(var value in map)
            {
                if(value.Key.IsMatch(match))
                {
                    return value.Value;
                }
            }
            return null;
        }

        private Dictionary<String, Tuple<String, String, String>> m_services = new Dictionary<String, Tuple<String, String, String>>();
        private Dictionary<Regex, Tuple<String, String, String>> m_getMethods = new Dictionary<Regex, Tuple<String, String, String>>();
        private Dictionary<Regex, Tuple<String, String, String>> m_nonGetMethods = new Dictionary<Regex, Tuple<String, String, String>>();
    }
}
