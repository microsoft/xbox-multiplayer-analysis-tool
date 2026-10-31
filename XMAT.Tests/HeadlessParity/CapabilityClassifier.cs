// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using XMAT.Operations;
using XMAT.Operations.Capabilities;

namespace XMAT.Tests.HeadlessParity
{
    internal sealed record ParityReport(IReadOnlyList<string> Unclassified, IReadOnlyList<string> Stale)
    {
        internal bool IsClean => Unclassified.Count == 0 && Stale.Count == 0;

        internal string Describe()
        {
            var message = new StringBuilder();
            if (Unclassified.Count != 0)
            {
                message.AppendLine("GUI entry points that no capability classifies. Add each to a capability in ")
                    .AppendLine("XMAT/Operations/Capabilities/CapabilityRegistry.cs (Supported, Planned or, for presentation only, GuiOnly):");
                foreach (string key in Unclassified)
                {
                    message.Append("  ").AppendLine(key);
                }
            }

            if (Stale.Count != 0)
            {
                message.AppendLine("Registry entry points that no longer exist in the product. Remove them:");
                foreach (string key in Stale)
                {
                    message.Append("  ").AppendLine(key);
                }
            }

            return message.ToString();
        }
    }

    internal static class CapabilityClassifier
    {
        internal static ParityReport Classify(IEnumerable<string> discovered, IEnumerable<Capability> registry)
        {
            var found = new SortedSet<string>(discovered, StringComparer.Ordinal);
            var classified = new SortedSet<string>(
                registry.SelectMany(capability => capability.GuiEntryPoints), StringComparer.Ordinal);
            return new ParityReport(found.Except(classified).ToList(), classified.Except(found).ToList());
        }
    }

    // Structural rules for registry entries. Returns one message per violation.
    internal static class CapabilityRegistryRules
    {
        private static readonly Regex IdPattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*(?:\\.[a-z0-9]+(?:-[a-z0-9]+)*)+$");
        private static readonly Regex SlicePattern = new("^S[0-9]+[a-z]?$");
        private static readonly string[] KeyPrefixes =
        {
            "command:", "click:", "dynamic:", "window:", "enum:", "analyzer:", "preference:",
        };

        internal static IReadOnlyList<string> Validate(IEnumerable<Capability> registry, Assembly product)
        {
            var problems = new List<string>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (Capability capability in registry)
            {
                string id = capability.Id ?? "(null)";
                if (!ids.Add(id))
                {
                    problems.Add($"{id}: duplicate id.");
                }

                if (capability.Id == null || !IdPattern.IsMatch(capability.Id))
                {
                    problems.Add($"{id}: ids are lower-case dotted names such as 'analysis.xbox-live'.");
                }

                if (string.IsNullOrWhiteSpace(capability.Title))
                {
                    problems.Add($"{id}: needs a title.");
                }

                foreach (string key in capability.GuiEntryPoints)
                {
                    if (key == null || !KeyPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
                    {
                        problems.Add($"{id}: '{key}' is not a known entry point key.");
                    }
                }

                if (capability.GuiEntryPoints.Distinct(StringComparer.Ordinal).Count() != capability.GuiEntryPoints.Count)
                {
                    problems.Add($"{id}: lists an entry point twice.");
                }

                switch (capability.Status)
                {
                    case CapabilityStatus.Supported:
                        RequireSlice(capability, problems);
                        if (string.IsNullOrWhiteSpace(capability.CliRoute))
                        {
                            problems.Add($"{id}: a supported capability needs its command line.");
                        }

                        if (capability.GuiEntryPoints.Count != 0 && string.IsNullOrWhiteSpace(capability.Operation))
                        {
                            problems.Add($"{id}: a supported GUI capability needs the shared operation both adapters use.");
                        }

                        if (!string.IsNullOrWhiteSpace(capability.Operation) && !IsOperation(product, capability.Operation))
                        {
                            problems.Add($"{id}: '{capability.Operation}' is not an IOperation<TRequest, TResult> in the product.");
                        }

                        RequireNoReason(capability, problems);
                        break;

                    case CapabilityStatus.Planned:
                        RequireSlice(capability, problems);
                        if (string.IsNullOrWhiteSpace(capability.Owner))
                        {
                            problems.Add($"{id}: a planned capability needs an owner.");
                        }

                        RequireNoReason(capability, problems);
                        break;

                    case CapabilityStatus.GuiOnly:
                        if (!id.StartsWith("view.", StringComparison.Ordinal))
                        {
                            problems.Add($"{id}: GuiOnly is for presentation only; its id must start with 'view.'.");
                        }

                        if (capability.Reason == null || !capability.Reason.StartsWith("Presentation:", StringComparison.Ordinal))
                        {
                            problems.Add($"{id}: a GuiOnly reason starts with 'Presentation:' and says why nothing runs headless.");
                        }

                        if (capability.CliRoute != null || capability.Operation != null || capability.Slice != null)
                        {
                            problems.Add($"{id}: a GuiOnly capability has no command line, operation or slice.");
                        }

                        break;

                    default:
                        problems.Add($"{id}: unknown status {capability.Status}.");
                        break;
                }
            }

            return problems;
        }

        private static void RequireSlice(Capability capability, List<string> problems)
        {
            if (capability.Slice == null || !SlicePattern.IsMatch(capability.Slice))
            {
                problems.Add($"{capability.Id}: needs the delivery slice, such as 'S1' or 'S8a'.");
            }
        }

        private static void RequireNoReason(Capability capability, List<string> problems)
        {
            if (capability.Reason != null)
            {
                problems.Add($"{capability.Id}: only GuiOnly capabilities carry a reason.");
            }
        }

        private static bool IsOperation(Assembly product, string typeName)
        {
            Type type = product.GetType(typeName, throwOnError: false);
            return type != null && !type.IsAbstract && type.GetInterfaces().Any(
                candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IOperation<,>));
        }
    }
}
