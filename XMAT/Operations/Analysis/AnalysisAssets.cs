// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CaptureAnalysisEngine;

namespace XMAT.Operations.Analysis
{
    internal sealed class AnalysisAssetSet
    {
        private readonly byte[] _apiMap;
        private readonly byte[] _rules;

        internal AnalysisAssetSet(byte[] apiMap, byte[] rules)
        {
            _apiMap = (byte[])apiMap.Clone();
            _rules = (byte[])rules.Clone();
        }

        internal TraceAnalyzer CreateAnalyzer(bool includeReports)
        {
            var analyzer = new TraceAnalyzer();
            using (var mapStream = new MemoryStream(_apiMap, false))
            using (var mapReader = new StreamReader(
                mapStream,
                Encoding.UTF8,
                true,
                4096,
                false))
            {
                analyzer.LoadURIMap(mapReader);
            }

            using (var rulesStream = new MemoryStream(_rules, false))
            using (var rulesReader = new StreamReader(
                rulesStream,
                Encoding.UTF8,
                true,
                4096,
                false))
            {
                analyzer.LoadRules(rulesReader);
            }

            if (includeReports)
            {
                analyzer.AddReports(
                    new Report[]
                    {
                        new PerEndpointReport(),
                        new CallReport(),
                        new StatsReport(),
                    });
            }
            return analyzer;
        }
    }

    internal sealed record AnalysisAssetCheck(
        string Id,
        bool Passed,
        string Code);

    internal enum AnalysisAssetLoadPolicy
    {
        // Package and command-line validation rejects incomplete or unknown entries.
        Strict,

        // The GUI delegates hand-edited files directly to the production parsers, preserving their legacy tolerance.
        GuiCompatible,
    }

    internal static class AnalysisAssets
    {
        private const string ApiMapRelativePath =
            "Data\\XboxLiveTraceAnalyzer.APIMap.csv";
        private const string RulesRelativePath =
            "Data\\XboxLiveTraceAnalyzer.Rules.json";

        internal static AnalysisAssetSet Load(string installDirectory) =>
            Load(
                installDirectory,
                AnalysisAssetLoadPolicy.Strict);

        internal static AnalysisAssetSet Load(
            string installDirectory,
            AnalysisAssetLoadPolicy policy)
        {
            ValidateInstallDirectory(installDirectory);
            var apiMap = ReadAsset(
                installDirectory,
                ApiMapRelativePath,
                "analysis.assets.apimap");
            var rules = ReadAsset(
                installDirectory,
                RulesRelativePath,
                "analysis.assets.rules");
            switch (policy)
            {
                case AnalysisAssetLoadPolicy.Strict:
                    ValidateApiMap(apiMap);
                    ValidateRules(rules);
                    break;

                case AnalysisAssetLoadPolicy.GuiCompatible:
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(policy));
            }

            // The production parser performs the final semantic validation, including rule property shapes.
            var assets = new AnalysisAssetSet(apiMap, rules);
            try
            {
                assets.CreateAnalyzer(false);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                throw new AnalysisAssetException(
                    "analysis.assets.invalid",
                    ex);
            }
            return assets;
        }

        internal static IReadOnlyList<AnalysisAssetCheck> Check(
            string installDirectory)
        {
            return new[]
            {
                CheckOne(
                    "data.apimap",
                    () =>
                    {
                        ValidateInstallDirectory(installDirectory);
                        var bytes = ReadAsset(
                            installDirectory,
                            ApiMapRelativePath,
                            "analysis.assets.apimap");
                        ValidateApiMap(bytes);
                        ValidateApiMapSemantics(bytes);
                    },
                    "analysis.assets.apimap"),
                CheckOne(
                    "data.rules",
                    () =>
                    {
                        ValidateInstallDirectory(installDirectory);
                        var bytes = ReadAsset(
                            installDirectory,
                            RulesRelativePath,
                            "analysis.assets.rules");
                        ValidateRules(bytes);
                        new AnalysisAssetSet(Array.Empty<byte>(), bytes)
                            .CreateAnalyzer(false);
                    },
                    "analysis.assets.rules"),
            };
        }

        private static AnalysisAssetCheck CheckOne(
            string id,
            Action check,
            string failureCode)
        {
            try
            {
                check();
                return new AnalysisAssetCheck(id, true, "analysis.assets.ok");
            }
            catch (AnalysisAssetException ex)
            {
                return new AnalysisAssetCheck(id, false, ex.Code);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return new AnalysisAssetCheck(id, false, failureCode);
            }
        }

        private static byte[] ReadAsset(
            string installDirectory,
            string relativePath,
            string code)
        {
            try
            {
                return File.ReadAllBytes(Path.Combine(
                    installDirectory,
                    relativePath));
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                throw new AnalysisAssetException(code, ex);
            }
        }

        private static void ValidateInstallDirectory(string installDirectory)
        {
            if (string.IsNullOrWhiteSpace(installDirectory))
            {
                throw new AnalysisAssetException(
                    "analysis.assets.install-directory");
            }
        }

        private static void ValidateApiMap(byte[] bytes)
        {
            try
            {
                using var stream = new MemoryStream(bytes, false);
                using var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    true,
                    4096,
                    false);
                var rows = 0;
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }
                    var values = Utils.GetCSVValues(line);
                    if (values.Length != 4 ||
                        values.Any(string.IsNullOrWhiteSpace))
                    {
                        throw new AnalysisAssetException(
                            "analysis.assets.apimap");
                    }
                    rows++;
                }
                if (rows == 0)
                {
                    throw new AnalysisAssetException(
                        "analysis.assets.apimap");
                }
            }
            catch (AnalysisAssetException)
            {
                throw;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                throw new AnalysisAssetException(
                    "analysis.assets.apimap",
                    ex);
            }
        }

        private static void ValidateApiMapSemantics(byte[] bytes)
        {
            try
            {
                var analyzer = new TraceAnalyzer();
                using var stream = new MemoryStream(bytes, false);
                using var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    true,
                    4096,
                    false);
                analyzer.LoadURIMap(reader);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                throw new AnalysisAssetException(
                    "analysis.assets.apimap",
                    ex);
            }
        }

        private static void ValidateRules(byte[] bytes)
        {
            try
            {
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty(
                        "Rules",
                        out var rules) ||
                    rules.ValueKind != JsonValueKind.Array ||
                    rules.GetArrayLength() == 0)
                {
                    throw new AnalysisAssetException(
                        "analysis.assets.rules");
                }

                var knownTypes = Utils.GetRuleTypesWithRuleAttribute()
                    .Select(type => type.Name)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var rule in rules.EnumerateArray())
                {
                    if (rule.ValueKind != JsonValueKind.Object ||
                        !RequiredString(rule, "Type", out var type) ||
                        !RequiredString(rule, "Name", out _) ||
                        !RequiredString(rule, "Endpoint", out _) ||
                        !knownTypes.Contains(type))
                    {
                        throw new AnalysisAssetException(
                            "analysis.assets.rules");
                    }
                }
            }
            catch (AnalysisAssetException)
            {
                throw;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                throw new AnalysisAssetException(
                    "analysis.assets.rules",
                    ex);
            }
        }

        private static bool RequiredString(
            JsonElement value,
            string name,
            out string text)
        {
            text = null;
            return value.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(text = property.GetString());
        }
    }

    internal sealed class AnalysisAssetException : Exception
    {
        internal AnalysisAssetException(
            string code,
            Exception innerException = null)
            : base("Required analysis data is unavailable.", innerException)
        {
            Code = code;
        }

        internal string Code { get; }
    }
}
