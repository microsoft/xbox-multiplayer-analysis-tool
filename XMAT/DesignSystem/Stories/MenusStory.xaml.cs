// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace XMAT.DesignSystem
{
    public partial class MenusStory : Story
    {
        public MenusStory()
        {
            InitializeComponent();
            SessionMenu = BuildSessionMenu();
            var opener = new Button { Content = "Open the traffic table's menu", ContextMenu = SessionMenu };
            opener.Click += (_, _) =>
            {
                SessionMenu.PlacementTarget = opener;
                SessionMenu.Placement = PlacementMode.Bottom;
                SessionMenu.IsOpen = true;
            };
            ContextMenuExample.Content = opener;
        }

        // The context menu the example opens.
        internal ContextMenu SessionMenu { get; }

        // The traffic table's menu for a request, with the app's labels, built with GroupedMenu on the new look's
        // surface. One row is unavailable, as Delete unselected is when every session is selected.
        private static ContextMenu BuildSessionMenu()
        {
            var menu = new ContextMenu();
            menu.SetResourceReference(StyleProperty, GroupedMenu.SurfaceStyleKey);

            GroupedMenu.AddGroup(menu, Text("MENU_GROUP_PROCESS"), "GameClient.exe");
            menu.Items.Add(GroupedMenu.Item(Text("MENU_SHOW_ONLY_PROCESS"), MenuGlyphs.Filter));
            menu.Items.Add(GroupedMenu.Item(Text("MENU_HIDE_PROCESS"), MenuGlyphs.Hide));

            GroupedMenu.AddGroup(menu, Text("MENU_GROUP_HOST"), "api.example.com");
            menu.Items.Add(GroupedMenu.Item(Text("MENU_ADD_TO_BLOCK_LIST"), MenuGlyphs.Block));
            MenuItem bypass = GroupedMenu.Item(Text("MENU_ADD_TO_BYPASS_LIST"), MenuGlyphs.Bypass);
            MenuRow.SetDescription(bypass, Text("MENU_BYPASS_DESCRIPTION"));
            menu.Items.Add(bypass);
            menu.Items.Add(GroupedMenu.Note(Localization.GetLocalizedString("MENU_ALSO_BLOCKED_BY", "*.example.com")));
            menu.Items.Add(GroupedMenu.Item(Text("MENU_MANAGE_HOST_LISTS"), MenuGlyphs.Settings));

            GroupedMenu.AddGroup(menu, Text("MENU_GROUP_SESSIONS"), null);
            menu.Items.Add(GroupedMenu.Item(Text("MENU_EXPORT_SELECTED"), MenuGlyphs.Export));
            MenuItem deleteUnselected = GroupedMenu.Item(Text("MENU_DELETE_UNSELECTED"), MenuGlyphs.Delete);
            deleteUnselected.IsEnabled = false;
            menu.Items.Add(deleteUnselected);
            return menu;
        }

        private static string Text(string key) => Localization.GetLocalizedString(key);
    }
}
