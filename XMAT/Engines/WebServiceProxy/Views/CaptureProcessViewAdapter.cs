// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using XMAT.Operations;
using XMAT.Operations.Processes;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    // How a capture tab shows and edits its process filter: the PROCESS column text, the matcher that the grid and the
    // timeline share with the analysis, and the process items of their context menus. The filter only hides rows; it
    // never changes what the proxy forwards or records.
    internal static class CaptureProcessViewAdapter
    {
        internal const string ServiceHostSelector = "svchost.exe";

        internal static string DisplayText(ClientProcessInfo info, bool resolving)
        {
            info ??= ClientProcessInfo.NotRecorded;
            if (!ProcessFilter.IsUnknown(info))
            {
                if (string.IsNullOrEmpty(info.Name))
                {
                    return Localization.GetLocalizedString("PROCESS_COLUMN_PID", info.ProcessId);
                }

                return info.ProcessId > 0
                    ? Localization.GetLocalizedString("PROCESS_COLUMN_NAME_PID", info.Name, info.ProcessId)
                    : info.Name;
            }

            if (resolving)
            {
                return Localization.GetLocalizedString("PROCESS_COLUMN_RESOLVING");
            }

            return Localization.GetLocalizedString(
                info.Status == ClientProcessStatus.Remote ? "PROCESS_COLUMN_REMOTE" : "PROCESS_COLUMN_UNKNOWN");
        }

        // The compiled matcher, or null when the specification has no selectors and so hides nothing.
        internal static ProcessFilter CreateMatcher(ProcessFilterSpec spec) =>
            ProcessFilter.IsEmpty(spec) ? null : ProcessFilter.Compile(spec);

        internal static Func<ProxyConnectionModel, bool> CreatePredicate(ProcessFilterSpec spec)
        {
            ProcessFilter matcher = CreateMatcher(spec);
            return matcher == null ? null : model => matcher.Matches(model.ClientProcess);
        }

        // The editor takes selectors separated by semicolons; names may contain spaces.
        internal static IReadOnlyList<string> Selectors(string text) =>
            (text ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        internal static string Join(ImmutableArray<string> selectors) =>
            selectors.IsDefaultOrEmpty ? string.Empty : string.Join("; ", selectors);

        // Show only this selector: it replaces every Show only selector and clears every Hide selector, including
        // wildcards and process ids that would hide some of the process's rows.
        internal static OperationResult<ProcessFilterSpec> WithOnly(ProcessFilterSpec current, string selector)
        {
            OperationResult<ProcessFilterSpec> only = ProcessFilter.Parse(new[] { selector }, Array.Empty<string>());
            return only.Succeeded && SameSelectors(only.Value, current)
                ? OperationResult<ProcessFilterSpec>.Success(current)
                : only;
        }

        // Also hide this selector; Show only is kept, and hiding wins over it.
        internal static OperationResult<ProcessFilterSpec> WithExcluded(ProcessFilterSpec current, string selector)
        {
            var exclude = Items(current?.Exclude ?? default).ToList();
            if (!exclude.Any(existing => SameSelector(existing, selector)))
            {
                exclude.Add(selector);
            }

            return ProcessFilter.Parse(Items(current?.Include ?? default), exclude);
        }

        // Whether two specifications have the same selectors, spelled the same and in the same order, as the capture
        // compares them before it notifies a new filter.
        internal static bool SameSelectors(ProcessFilterSpec left, ProcessFilterSpec right)
        {
            left ??= ProcessFilterSpec.Empty;
            right ??= ProcessFilterSpec.Empty;
            return Items(left.Include).SequenceEqual(Items(right.Include), StringComparer.Ordinal) &&
                Items(left.Exclude).SequenceEqual(Items(right.Exclude), StringComparer.Ordinal);
        }

        // Appends the process items for one request, decided when the menu opens. Each item runs a proxy command with
        // the specification to apply, routed from the element that owns the menu. A request without a process gets a
        // disabled note instead of actions.
        internal static void AddMenuItems(
            ContextMenu menu,
            ProxyConnectionModel model,
            ProcessFilterSpec current,
            IInputElement commandTarget)
        {
            ArgumentNullException.ThrowIfNull(menu);
            menu.Items.Add(new Separator());
            ClientProcessInfo info = model?.ClientProcess;
            string selector = SelectorOf(info);
            OperationResult<ProcessFilterSpec> only = selector == null ? null : WithOnly(current, selector);
            OperationResult<ProcessFilterSpec> hide = selector == null ? null : WithExcluded(current, selector);
            if (only?.Succeeded == true && hide?.Succeeded == true)
            {
                string label = HostListAction.EscapeAccessKeys(LabelOf(info));
                menu.Items.Add(Item(
                    Localization.GetLocalizedString("PROCESS_FILTER_SHOW_ONLY", label),
                    WebServiceProxyCommands.ShowOnlyProcess,
                    only.Value,
                    commandTarget));
                menu.Items.Add(Item(
                    Localization.GetLocalizedString("PROCESS_FILTER_HIDE", label),
                    WebServiceProxyCommands.HideProcess,
                    hide.Value,
                    commandTarget));
            }
            else
            {
                menu.Items.Add(new MenuItem
                {
                    Header = Localization.GetLocalizedString("PROCESS_FILTER_NO_PROCESS"),
                    IsEnabled = false,
                });
            }

            if (!ProcessFilter.IsEmpty(current))
            {
                menu.Items.Add(Item(
                    Localization.GetLocalizedString("PROCESS_FILTER_SHOW_ALL"),
                    WebServiceProxyCommands.ShowAllProcesses,
                    ProcessFilterSpec.Empty,
                    commandTarget));
            }
        }

        private static string SelectorOf(ClientProcessInfo info)
        {
            if (ProcessFilter.IsUnknown(info))
            {
                return null;
            }

            return string.IsNullOrEmpty(info.Name)
                ? info.ProcessId.ToString(CultureInfo.InvariantCulture)
                : info.Name;
        }

        private static string LabelOf(ClientProcessInfo info) =>
            string.IsNullOrEmpty(info.Name)
                ? Localization.GetLocalizedString("PROCESS_COLUMN_PID", info.ProcessId)
                : info.Name;

        // Selectors are case-insensitive and .exe is optional, so "svchost" and "SVCHOST.EXE" are the same selector.
        private static bool SameSelector(string left, string right) =>
            string.Equals(WithoutExe(left), WithoutExe(right), StringComparison.OrdinalIgnoreCase);

        private static string WithoutExe(string selector) =>
            selector != null && selector.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? selector.Substring(0, selector.Length - 4)
                : selector;

        private static IEnumerable<string> Items(ImmutableArray<string> selectors) =>
            selectors.IsDefault ? Enumerable.Empty<string>() : selectors;

        // The parameter and the target are set before the command, which is asked at once whether it can run with them.
        private static MenuItem Item(
            string header,
            RoutedCommand command,
            ProcessFilterSpec target,
            IInputElement commandTarget) => new()
            {
                Header = header,
                CommandParameter = target,
                CommandTarget = commandTarget,
                Command = command,
            };
    }

    // The PROCESS column: ClientProcess and IsProcessResolving as text.
    internal sealed class ProcessDisplayConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            CaptureProcessViewAdapter.DisplayText(
                values != null && values.Length > 0 ? values[0] as ClientProcessInfo : null,
                values != null && values.Length > 1 && values[1] is bool resolving && resolving);

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
