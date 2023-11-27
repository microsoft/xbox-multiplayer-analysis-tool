// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using XMAT;

namespace CaptureAnalysisEngine
{
    public static class Utils
    {
        // TODO: refactor this method and the method below into a single cohesive
        // utility method that can return a bunch of types from a provided base class
        // type with a provided attribute type
        public static IEnumerable<Type> GetRuleTypesWithRuleAttribute()
        {
            return from assemblies in AppDomain.CurrentDomain.GetAssemblies()
                   from types in assemblies.GetTypes()
                   where types.IsSubclassOf(typeof(Rule)) && types.IsDefined(typeof(RuleAttribute), false)
                   select types;
        }

        public static void PrintMemoryStats()
        {
            Process proc = Process.GetCurrentProcess();
            proc.Refresh();
            PublicUtilities.AppLog(LogLevel.INFO, $"---------[ Memory Stats Start ]---------");
            PublicUtilities.AppLog(LogLevel.INFO, $"  PrivateMemorySize64: {proc.PrivateMemorySize64 / 1048576}");
            PublicUtilities.AppLog(LogLevel.INFO, $"  PagedSystemMemorySize64: {proc.PagedSystemMemorySize64 / 1048576}");
            PublicUtilities.AppLog(LogLevel.INFO, $" ");
            PublicUtilities.AppLog(LogLevel.INFO, $"  PagedMemorySize64: {proc.PagedMemorySize64 / 1048576}");
            PublicUtilities.AppLog(LogLevel.INFO, $"  PeakPagedMemorySize64: {proc.PeakPagedMemorySize64 / 1048576}");
            PublicUtilities.AppLog(LogLevel.INFO, $"  VirtualMemorySize64: {proc.VirtualMemorySize64 / 1048576}");
            PublicUtilities.AppLog(LogLevel.INFO, $"  PeakVirtualMemorySize64: {proc.PeakVirtualMemorySize64 / 1048576}");
            PublicUtilities.AppLog(LogLevel.INFO, $"  WorkingSet64: {proc.WorkingSet64 / 1048576}");
            PublicUtilities.AppLog(LogLevel.INFO, $"  PeakWorkingSet64: {proc.PeakWorkingSet64 / 1048576}");
            PublicUtilities.AppLog(LogLevel.INFO, $"---------[ Memory Stats End ]---------");
            proc.Dispose();
        }

        public static bool IsSuccessHTTPStatusCode(UInt32 httpStatusCode)
        {
            // 2xx are http success codes
            if ((httpStatusCode >= 200) && (httpStatusCode < 300))
            {
                return true;
            }

            // MSXML XHR bug: get_status() returns HTTP/1223 for HTTP/204:
            // http://blogs.msdn.com/b/ieinternals/archive/2009/07/23/the-ie8-native-xmlhttprequest-object.aspx
            // treat it as success code as well
            if (httpStatusCode == 1223)
            {
                return true;
            }

            return false;
        }

        public static String[] GetCSVValues(String input)
        {
            String[] delims = { "\",\"" };
            String[] rows = input.Split(delims, StringSplitOptions.None);

            List<String> values = new List<String>();
            for (int i = 0; i < rows.Length; ++i)
            {
                if (rows[i].Length > 1 && (rows[i][rows[i].Length - 1] == '\"'))
                {
                    int j = i + 1;
                    String mergedStr = rows[i];
                    for (; j < rows.Length; ++j)
                    {
                        if (rows[j].Length > 1 && (rows[j][0] == '\"'))
                        {
                            mergedStr += "\",\"" + rows[j];
                        }
                        else
                        {
                            break;
                        }
                    }
                    values.Add(mergedStr);
                    i = j - 1;
                }
                else
                {
                    values.Add(rows[i]);
                }
            }

            for (int i = 0; i < values.Count; ++i)
            {
                values[i] = values[i].Replace("\"\"", "\"");
            }

            //remove starting and ending quotes
            values[0] = values[0].Substring(1);
            values[values.Count - 1] = values[values.Count - 1].Substring(0, values[values.Count - 1].Length - 1);
            return values.ToArray();
        }

        public static int GetFrameNumber(String filename)
        {
            String number = filename.Substring(0, filename.IndexOf('_'));
            return int.Parse(number);
        }

        public static bool IsAnalyzedService(WebHeaderCollection headers, String customUserAgent)
        {
            var result = false;
            if (headers["x-xbl-api-build-version"] != null && headers["x-xbl-api-build-version"].Equals("adk", StringComparison.OrdinalIgnoreCase))
            {
                PublicUtilities.AppLog(LogLevel.INFO, "header[x-xbl-api-build-version] is adk");
                result = true;
            }
            else
            {
                if (headers["x-xbl-api-build-version"] == null)
                {
                    PublicUtilities.AppLog(LogLevel.INFO, "header[x-xbl-api-build-version] is null");
                }
                else
                {
                    PublicUtilities.AppLog(LogLevel.INFO, "header[x-xbl-api-build-version] is not adk");
                }
            }

            if (headers["User-Agent"] != null && headers["User-Agent"].Contains("XboxServicesAPI") && headers["x-xbl-client-name"] == null)
            {
                PublicUtilities.AppLog(LogLevel.INFO, "header[User-Agent] contains XboxServicesAPI and header[x-xbl-client-name] is null");
                result = true;
            }
            else
            {
                if (headers["User-Agent"] == null)
                {
                    PublicUtilities.AppLog(LogLevel.INFO, "header[User-Agent] is null");
                }
                else if (!headers["User-Agent"].Contains("XboxServicesAPI"))
                {
                    PublicUtilities.AppLog(LogLevel.INFO, "header[User-Agent] does not contain XboxServicesAPI");
                }
                else
                {
                    PublicUtilities.AppLog(LogLevel.INFO, "header[x-xbl-client-name] is not null");
                }
            }

            // XCE CS1.0 event
            if (headers["X-XBL-Build-Version"] != null)
            {
                PublicUtilities.AppLog(LogLevel.INFO, "header[X-XBL-Build-Version] is not null");
                result = true;
            }
            else
            {
                PublicUtilities.AppLog(LogLevel.INFO, "header[X-XBL-Build-Version] is null");
            }

            // UTC upload CS2.1 events
            if (headers["X-AuthXToken"] != null)
            {
                PublicUtilities.AppLog(LogLevel.INFO, "header[X-AuthXToken] is not null");
                result = true;
            }
            else
            {
                PublicUtilities.AppLog(LogLevel.INFO, "header[X-AuthXToken] is null");
            }

            if (!String.IsNullOrEmpty(customUserAgent) && headers["User-Agent"] == customUserAgent)
            {
                PublicUtilities.AppLog(LogLevel.INFO, "customUserAgent is not null and header[User-Agent] matches.");
                result = true;
            }
            else
            {
                if (String.IsNullOrEmpty(customUserAgent))
                {
                    PublicUtilities.AppLog(LogLevel.INFO, "customUserAgent is null or empty");
                }
                else
                {
                    PublicUtilities.AppLog(LogLevel.INFO, "header[User-Agent] does not match customUserAgent");
                }
            }

            return result;
        }

        public static String GeneratePathToFile(String file)
        {
            String runningDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            return Path.Combine(runningDirectory, file);
        }

        public static String GetXboxUserID(String line)
        {
            if (line.Contains("xuid(") == false)
            {
                return "";
            }

            int start = line.IndexOf('(') + 1;
            int end = line.IndexOf(')');

            return line.Substring(start, end - start);
        }

        public static void PrintCallIdRange(StringBuilder output, IEnumerable<ServiceCallItem> calls, UInt32 minRangeSize)
        {
            if (calls.Count() == 0)
            {
                return;
            }
            List<UInt32> range = new List<UInt32>();
            UInt32 prevId = calls.First().Id - 1;
            for (int i = 0; i < calls.Count(); ++i)
            {
                UInt32 localId = calls.ElementAt(i).Id;
                if (localId == prevId + 1)
                {
                    range.Add(localId);
                }
                else
                {
                    if (range.Count >= minRangeSize)
                    {
                        output.AppendFormat("{0}-{1}, ", range.First(), range.Last());
                    }
                    else
                    {
                        foreach (var id in range)
                        {
                            output.AppendFormat("{0}, ", id);
                        }
                    }

                    range.Clear();
                    range.Add(localId);
                }

                prevId = localId;
            }

            if (range.Count >= minRangeSize)
            {
                output.AppendFormat("{0}-{1}, ", range.First(), range.Last());
            }
            else
            {
                foreach (var id in range)
                {
                    output.AppendFormat("{0}, ", id);
                }
            }
        }

        public static LinkedList<ServiceCallItem> GetCallsBetweenRange(List<ServiceCallItem> callHistory, UInt32 historyIndexLow, UInt32 historyIndexHigh)
        {
            LinkedList<ServiceCallItem> calls = new LinkedList<ServiceCallItem>();

            UInt32 lowIndex = historyIndexLow;
            while (lowIndex <= historyIndexHigh)
            {
                calls.AddLast(callHistory[(int)lowIndex]);
                ++lowIndex;
            }

            return calls;
        }

        public static LinkedList<List<ServiceCallItem>> GetExcessCallsForTimeWindow(IEnumerable<ServiceCallItem> callHistoryEnumerable, UInt32 timeWindowInMs, UInt32 maxNumAllowedCalls)
        {
            // Filter out shoulder tap calls to simplify code below other code
            List<ServiceCallItem> callHistory = callHistoryEnumerable.Where(call => call.IsShoulderTap == false).ToList();
            LinkedList<List<ServiceCallItem>> excessCalls = new LinkedList<List<ServiceCallItem>>();

            if (callHistory.Count < 2)
            {
                return excessCalls;
            }

            Int32 start = 0;
            Int32 current = 0;

            for (current = start; current < callHistory.Count; ++current)
            {
                do
                {
                    // See if this call is within the call window
                    long delta = (long)(callHistory[current].ReqTimeUTC - callHistory[start].ReqTimeUTC) / TimeSpan.TicksPerMillisecond;

                    // If it is within the window, continue with the outer loop
                    if (delta <= timeWindowInMs)
                    {
                        break;
                    }
                    // If this call is outside of the window
                    else if (delta > timeWindowInMs)
                    {
                        // and the number of calls in the window exceeds the maximum number of calls
                        if (current - start > maxNumAllowedCalls)
                        {
                            // record the window and reset the start of the window
                            excessCalls.AddLast(callHistory.GetRange(start, current - start));
                            start = current;
                        }
                        else
                        {
                            // move the start index forward and recheck the delta
                            ++start;
                        }
                    }
                // For moving the start of the window forward looking for a new window
                } while (start != current);
            }

            // If at the the end of the list of calls we had a window of excessive calls , record it
            if(current == callHistory.Count && current - start > maxNumAllowedCalls)
            {
                excessCalls.AddLast(callHistory.GetRange(start, current - start));
            }

            return excessCalls;
        }

        public static void SafeAssign<T>(JsonElement json, string propertyName, ref T obj)
        {
            string valueAsText;
            JsonElement objElement;
            if (json.TryGetProperty(propertyName, out objElement))
            {
                try
                {
                    valueAsText = objElement.ToString();
                    obj = (T)Convert.ChangeType(valueAsText, typeof(T));
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException($"Failed to parse json value for '{propertyName}'", ex);
                }
            }
        }

        //public static void AddNonNullItem(ref Newtonsoft.Json.Linq.JArray array, Newtonsoft.Json.Linq.JToken json)
        //{
        //    if (json != null) array.Add(json);
        //}

        public static Object GetStaticProperty<T>(String propertyName)
        {
            return typeof(T).GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).GetValue(null, null);
        }

        public static Object GetStaticProperty(String propertyName, Type type)
        {
            return type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).GetValue(null, null);
        }

        public static bool HasStaticProperty(String propertyName, Type type)
        {
            return type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) != null;
        }

        public static MemoryStream DecompressToMemory(ZipArchiveEntry entry)
        {
            var memory = new MemoryStream();
            using (var zip = entry.Open())
            {
                zip.CopyTo(memory);
            }
            memory.Seek(0, SeekOrigin.Begin);
            return memory;
        }

        public static MemoryStream InflateData(byte[] data)
        {
            MemoryStream decompressed = new MemoryStream();
            using (var memory = new MemoryStream(data))
            {
                using (var compressed = new DeflateStream(memory, CompressionMode.Decompress))
                {
                    compressed.CopyTo(decompressed);
                    decompressed.Seek(0, SeekOrigin.Begin);
                    return decompressed;
                }
            }
        }
    }

    public static class BinaryReadingExtensions
    {
        public static String ConvertToString(this List<byte> buffer, int length = int.MaxValue)
        {
            if (length > buffer.Count)
                return Encoding.ASCII.GetString(buffer.ToArray());

            return Encoding.ASCII.GetString(buffer.ToArray(), 0, length);
        }


        public static int FindFirstMatch(this List<byte> source, byte[] pattern)
        {
            if (source == null || pattern == null || source.Count == 0 ||
               pattern.Length == 0 || pattern.Length > source.Count)
                return -1;

            for (int i = 0; i < source.Count - pattern.Length; ++i)
            {
                if (IsMatch(source, i, pattern))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsMatch(List<byte> source, int position, byte[] pattern)
        {
            for (int j = 0; j < pattern.Length; ++j)
            {
                if (source[position + j] != pattern[j])
                {
                    return false;
                }
            }

            return true;
        }

        public static string ReadLine(this BinaryReader obj)
        {
            byte[] pattern = Encoding.ASCII.GetBytes("\r\n");
            List<byte> bytes = new List<byte>();

            int count = 0;
            do
            {
                var buffer = obj.ReadBytes(4096);
                count = buffer.Length;
                if (count > 0)
                {
                    bytes.AddRange(buffer);
                }

                // Find the newline pattern
                int match = bytes.FindFirstMatch(pattern);
                if (match != -1)
                {
                    // reset the stream to just after the newline
                    obj.BaseStream.Seek(match - bytes.Count + 2, SeekOrigin.Current);
                    return bytes.ConvertToString(match);
                }

            } while (count == 4096);

            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        public static byte[] ReadToEnd(this BinaryReader obj)
        {
            List<byte> bytes = new List<byte>();
            int count = 0;
            do
            {
                var buffer = obj.ReadBytes(4096);
                count = buffer.Length;
                if (count > 0)
                {
                    bytes.AddRange(buffer);
                }
            } while (count == 4096);

            return bytes.ToArray();
        }

        public static bool IsEndOfStream(this BinaryReader obj)
        {
            return obj.BaseStream.Position == obj.BaseStream.Length;
        }
    }
}
