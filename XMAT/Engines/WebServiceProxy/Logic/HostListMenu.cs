// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    internal enum HostListKind
    {
        Block,
        Bypass
    }

    internal enum HostListOperation
    {
        Add,
        Remove
    }

    /// <summary>
    /// One immutable host-list menu action: the policy, the list, the host identity and the
    /// labelled operation are fixed when the menu opens, so later changes to the clicked item
    /// or to the lists can never turn it into a different operation.
    /// </summary>
    internal sealed class HostListAction
    {
        internal HostListAction(HostListPolicy policy, HostListKind list, HostListOperation operation, string host)
        {
            Policy = policy;
            List = list;
            Operation = operation;
            Host = host;
            Header = Localization.GetLocalizedString(HeaderKey(list, operation), EscapeAccessKeys(host));
        }

        internal HostListPolicy Policy { get; }
        internal HostListKind List { get; }
        internal HostListOperation Operation { get; }
        internal string Host { get; }
        internal string Header { get; }

        /// <summary>
        /// Applies exactly the labelled operation to the exact entry. Returns false when the
        /// list was already in that state, for example after an edit made elsewhere.
        /// </summary>
        internal bool Execute() => (List, Operation) switch
        {
            (HostListKind.Block, HostListOperation.Add) => Policy.BlockList.Add(Host),
            (HostListKind.Block, HostListOperation.Remove) => Policy.BlockList.Remove(Host),
            (HostListKind.Bypass, HostListOperation.Add) => Policy.BypassList.Add(Host),
            _ => Policy.BypassList.Remove(Host)
        };

        internal static string EscapeAccessKeys(string text) => text?.Replace("_", "__", StringComparison.Ordinal);

        private static string HeaderKey(HostListKind list, HostListOperation operation) => (list, operation) switch
        {
            (HostListKind.Block, HostListOperation.Add) => "HOST_LIST_ADD_BLOCK",
            (HostListKind.Block, HostListOperation.Remove) => "HOST_LIST_REMOVE_BLOCK",
            (HostListKind.Bypass, HostListOperation.Add) => "HOST_LIST_ADD_BYPASS",
            _ => "HOST_LIST_REMOVE_BYPASS"
        };
    }

    /// <summary>
    /// The host-list actions offered for one host, decided by exact membership. Rules that
    /// still match the host by themselves (such as a covering wildcard) are reported so the
    /// menu can explain them instead of removing them.
    /// </summary>
    internal sealed class HostListMenu
    {
        private HostListMenu(
            string host,
            IReadOnlyList<HostListAction> actions,
            IReadOnlyList<string> coveringBlockRules,
            IReadOnlyList<string> coveringBypassRules)
        {
            Host = host;
            Actions = actions;
            CoveringBlockRules = coveringBlockRules;
            CoveringBypassRules = coveringBypassRules;
        }

        internal string Host { get; }
        internal IReadOnlyList<HostListAction> Actions { get; }
        internal IReadOnlyList<string> CoveringBlockRules { get; }
        internal IReadOnlyList<string> CoveringBypassRules { get; }

        /// <summary>
        /// Returns null when the value is not one valid host, so no action is invented for
        /// empty, aggregate or malformed targets.
        /// </summary>
        internal static HostListMenu Create(HostListPolicy policy, string host)
        {
            if (policy == null || !HostAuthority.TryNormalizeHost(host, out string identity))
            {
                return null;
            }

            var actions = new[]
            {
                new HostListAction(
                    policy,
                    HostListKind.Block,
                    policy.BlockList.Contains(identity) ? HostListOperation.Remove : HostListOperation.Add,
                    identity),
                new HostListAction(
                    policy,
                    HostListKind.Bypass,
                    policy.BypassList.Contains(identity) ? HostListOperation.Remove : HostListOperation.Add,
                    identity)
            };
            return new HostListMenu(
                identity,
                actions,
                OtherRules(policy.BlockList.MatchingPatterns(identity), identity),
                OtherRules(policy.BypassList.MatchingPatterns(identity), identity));
        }

        private static IReadOnlyList<string> OtherRules(IEnumerable<string> rules, string host) =>
            rules.Where(rule => !string.Equals(rule, host, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    internal static class HostListCommands
    {
        /// <summary>Executes the <see cref="HostListAction"/> passed as the command parameter.</summary>
        internal static ICommand Apply { get; } = new ApplyCommand();

        private sealed class ApplyCommand : ICommand
        {
            // An action can always run; its result is already decided by its own snapshot.
            public event EventHandler CanExecuteChanged
            {
                add { }
                remove { }
            }

            public bool CanExecute(object parameter) => parameter is HostListAction;

            public void Execute(object parameter)
            {
                if (parameter is HostListAction action && !action.Execute())
                {
                    PublicUtilities.AppLog(LogLevel.INFO,
                        $"Host list unchanged: {action.Operation} {action.Host} ({action.List}) was already applied.");
                }
            }
        }
    }

    /// <summary>
    /// Builds host-list context menus for the capture, timeline and analysis views.
    /// </summary>
    internal static class HostListContextMenu
    {
        /// <summary>
        /// Finds the element whose context menu opens for a right-click or menu-key route:
        /// the first element from the event source upward that owns an enabled context menu.
        /// </summary>
        internal static FrameworkElement FindOwner(object source)
        {
            var current = source as DependencyObject;
            while (current != null)
            {
                if (current is FrameworkElement element &&
                    element.ContextMenu != null &&
                    ContextMenuService.GetIsEnabled(element))
                {
                    return element;
                }
                current = current is Visual
                    ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return null;
        }

        /// <summary>
        /// Replaces the menu items with actions for the snapshotted host, or with a disabled
        /// explanation when there is no single valid host for the clicked item. The new look
        /// shows them as the menu's Host group.
        /// </summary>
        internal static void Populate(
            ContextMenu menu,
            HostListPolicy policy,
            string host,
            string unavailableKey,
            IInputElement commandTarget)
        {
            menu.Items.Clear();
            GroupedMenu.ApplyLook(menu);
            HostListMenu model = HostListMenu.Create(policy, host);
            if (LookState.Current.IsNewLook)
            {
                AddHostGroup(menu, policy, model, unavailableKey, commandTarget);
                return;
            }

            if (model == null)
            {
                menu.Items.Add(Note(Localization.GetLocalizedString(unavailableKey)));
            }
            else
            {
                foreach (HostListAction action in model.Actions)
                {
                    menu.Items.Add(new MenuItem
                    {
                        Header = action.Header,
                        Command = HostListCommands.Apply,
                        CommandParameter = action
                    });
                }
                foreach (string rule in model.CoveringBlockRules)
                {
                    menu.Items.Add(Note(Localization.GetLocalizedString(
                        "HOST_LIST_ALSO_BLOCKED_BY", HostListAction.EscapeAccessKeys(rule))));
                }
                foreach (string rule in model.CoveringBypassRules)
                {
                    menu.Items.Add(Note(Localization.GetLocalizedString(
                        "HOST_LIST_ALSO_BYPASSED_BY", HostListAction.EscapeAccessKeys(rule))));
                }
                if (policy.BlockList.IsBlocked(model.Host) && policy.BypassList.IsBypassed(model.Host))
                {
                    menu.Items.Add(Note(Localization.GetLocalizedString("HOST_LIST_BLOCK_WINS")));
                }
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem
            {
                Header = Localization.GetLocalizedString("HOST_LISTS_MANAGE_MENU"),
                Command = AppCommands.ManageHostLists,
                CommandTarget = commandTarget
            });
        }

        // The new look's Host group. Its header names the host once, so the rows leave it out of their labels, and their
        // accessible names keep it. Each list's notes follow its action.
        private static void AddHostGroup(
            ContextMenu menu,
            HostListPolicy policy,
            HostListMenu model,
            string unavailableKey,
            IInputElement commandTarget)
        {
            GroupedMenu.AddGroup(menu, Localization.GetLocalizedString("MENU_GROUP_HOST"), model?.Host);
            if (model == null)
            {
                menu.Items.Add(GroupedMenu.Note(Localization.GetLocalizedString(unavailableKey)));
            }
            else
            {
                foreach (HostListAction action in model.Actions)
                {
                    string key = GroupedKey(action);
                    MenuItem item = GroupedMenu.Item(
                        Localization.GetLocalizedString(key),
                        action.List == HostListKind.Block ? MenuGlyphs.Block : MenuGlyphs.Bypass,
                        Localization.GetLocalizedString(key + "_NAME", action.Host));
                    if (action.List == HostListKind.Bypass)
                    {
                        MenuRow.SetDescription(item, Localization.GetLocalizedString(
                            action.Operation == HostListOperation.Add ? "MENU_BYPASS_DESCRIPTION" : "MENU_REMOVE_BYPASS_DESCRIPTION"));
                    }
                    item.CommandParameter = action;
                    item.Command = HostListCommands.Apply;
                    menu.Items.Add(item);
                    bool block = action.List == HostListKind.Block;
                    foreach (string rule in block ? model.CoveringBlockRules : model.CoveringBypassRules)
                    {
                        menu.Items.Add(GroupedMenu.Note(Localization.GetLocalizedString(
                            block ? "MENU_ALSO_BLOCKED_BY" : "MENU_ALSO_BYPASSED_BY", rule)));
                    }
                }
                if (policy.BlockList.IsBlocked(model.Host) && policy.BypassList.IsBypassed(model.Host))
                {
                    menu.Items.Add(GroupedMenu.Note(Localization.GetLocalizedString("HOST_LIST_BLOCK_WINS")));
                }
            }

            MenuItem manage = GroupedMenu.Item(Localization.GetLocalizedString("MENU_MANAGE_HOST_LISTS"), MenuGlyphs.Settings);
            manage.CommandTarget = commandTarget;
            manage.Command = AppCommands.ManageHostLists;
            menu.Items.Add(manage);
        }

        // The short label of an action under the Host header; with "_NAME", its accessible name, which names the host.
        private static string GroupedKey(HostListAction action) => (action.List, action.Operation) switch
        {
            (HostListKind.Block, HostListOperation.Add) => "MENU_ADD_TO_BLOCK_LIST",
            (HostListKind.Block, HostListOperation.Remove) => "MENU_REMOVE_FROM_BLOCK_LIST",
            (HostListKind.Bypass, HostListOperation.Add) => "MENU_ADD_TO_BYPASS_LIST",
            _ => "MENU_REMOVE_FROM_BYPASS_LIST"
        };

        private static MenuItem Note(string text) => new() { Header = text, IsEnabled = false };
    }
}
