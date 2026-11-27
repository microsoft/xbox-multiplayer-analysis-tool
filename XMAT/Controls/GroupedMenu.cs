// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace XMAT
{
    // Builds the new look's context menus: labeled groups of rows on the menu surface of Styles/Menus.xaml. A row has an
    // icon and a short label, and its accessible name keeps the full meaning. Menus are filled as they open, so a menu
    // opened after the look changes shows that look.
    internal static class GroupedMenu
    {
        internal const string SurfaceStyleKey = "ContextMenuSurfaceStyle";

        // The new look's surface, or the original look's menu as it was.
        internal static void ApplyLook(ContextMenu menu)
        {
            if (LookState.Current.IsNewLook)
            {
                menu.SetResourceReference(FrameworkElement.StyleProperty, SurfaceStyleKey);
            }
            else
            {
                menu.ClearValue(FrameworkElement.StyleProperty);
            }
        }

        // Starts a group: a separator after the group before it, then a header with the group's name and, when its items
        // act on one, their subject, such as a host.
        internal static void AddGroup(ContextMenu menu, string name, string subject)
        {
            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new Separator());
            }

            menu.Items.Add(new MenuGroupHeader { Text = name, Subject = string.IsNullOrEmpty(subject) ? null : subject });
        }

        internal static MenuNote Note(string text) => new() { Text = text };

        // A row with an icon and a short label. ACCESSIBLE NAME says in full what the row does when the label leaves part
        // of it, such as the host, to the group's header.
        internal static MenuItem Item(string label, string glyph, string accessibleName = null)
        {
            var item = new MenuItem { Header = label };
            MenuRow.SetGlyph(item, glyph);
            if (accessibleName != null)
            {
                AutomationProperties.SetName(item, accessibleName);
            }

            return item;
        }
    }
}
