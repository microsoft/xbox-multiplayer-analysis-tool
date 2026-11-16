// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;

namespace XMAT.DesignSystem
{
    // A named color of the themes and what XMAT uses it for.
    public sealed record ColorRole(string Key, string Use);

    // Roles that do one kind of job, such as the surfaces.
    public sealed record ColorRoleGroup(string Name, IReadOnlyList<ColorRole> Roles);

    // XMAT's colors as the design system defines them: the roles views and styles use, the colors that belong to one
    // component, and the text and surfaces that must stay readable together. The Color page shows them with the values
    // the themes give them, and the design system tests (XMAT.Tests/DesignSystem/ColorRoleTests.cs) check the themes
    // against this list: every theme defines every color here, every color the themes define is here, and every pair
    // here meets 4.5:1 in Light and Dark. To add a color, add it here and to the themes (docs/design-system.md).
    public static class ColorCatalog
    {
        // The contrast that text needs on its background: WCAG's 4.5:1 for normal text.
        public const double ReadableContrast = 4.5;

        public static IReadOnlyList<ColorRoleGroup> RoleGroups { get; } = new[]
        {
            new ColorRoleGroup("Surfaces", new[]
            {
                new ColorRole("SurfaceCanvasBrush", "Window chrome: the window, menus, the status bar and unselected tabs"),
                new ColorRole("SurfacePanelBrush", "Content panels, the selected tab, text boxes and toolbars"),
                new ColorRole("SurfaceWellBrush", "Recessed wells: tables and text areas"),
                new ColorRole("SurfaceHeaderBrush", "The header band of a table"),
                new ColorRole("SurfaceStripBrush", "The capture tab strip"),
                new ColorRole("SurfaceHoverBrush", "A hovered button or row"),
                new ColorRole("SurfaceSelectedBrush", "A chosen card, such as the device in New Capture. High contrast keeps the background and marks the choice with the selected text background"),
                new ColorRole("SurfaceControlBrush", "A button's face"),
            }),
            new ColorRoleGroup("Text", new[]
            {
                new ColorRole("TextPrimaryBrush", "Body text"),
                new ColorRole("TextSecondaryBrush", "Labels, captions, column headers and the status bar"),
                new ColorRole("TextDisabledBrush", "Disabled controls"),
                new ColorRole("TextHeadingBrush", "Section headings"),
                new ColorRole("TextControlBrush", "Button text"),
            }),
            new ColorRoleGroup("Borders", new[]
            {
                new ColorRole("BorderSubtleBrush", "Table grid lines and the faintest dividers"),
                new ColorRole("BorderDefaultBrush", "Panel borders and dividers"),
                new ColorRole("BorderControlBrush", "Button outlines and the pane splitter"),
                new ColorRole("BorderStrongBrush", "Strong outlines and scroll bar thumbs"),
                new ColorRole("BorderStrongHoverBrush", "A strong outline under the pointer"),
                new ColorRole("BorderWellBrush", "A text area's outline"),
            }),
            new ColorRoleGroup("Accent", new[]
            {
                new ColorRole("AccentBrush", "Selection outlines, the selected tab's indicator and chart lines"),
                new ColorRole("AccentFillBrush", "A selected item's fill"),
                new ColorRole("AccentOnFillBrush", "Text on AccentFillBrush"),
                new ColorRole("AccentSoftBrush", "Pressed buttons and checked toggles"),
            }),
            new ColorRoleGroup("Status", new[]
            {
                new ColorRole("SuccessFillBrush", "2xx responses in the timeline and charts"),
                new ColorRole("SuccessOnFillBrush", "Labels on SuccessFillBrush"),
                new ColorRole("SuccessTextBrush", "Good news, such as an analysis check that found no issues"),
                new ColorRole("SuccessSoftBrush", "The background behind SuccessTextBrush"),
                new ColorRole("WarningFillBrush", "4xx responses"),
                new ColorRole("WarningOnFillBrush", "Labels on WarningFillBrush"),
                new ColorRole("WarningTextBrush", "Warnings and pending captures"),
                new ColorRole("WarningSoftBrush", "The background behind WarningTextBrush"),
                new ColorRole("ErrorFillBrush", "5xx responses"),
                new ColorRole("ErrorOnFillBrush", "Labels on ErrorFillBrush"),
                new ColorRole("ErrorTextBrush", "Errors"),
                new ColorRole("ErrorSoftBrush", "The background behind ErrorTextBrush"),
                new ColorRole("InfoFillBrush", "3xx redirects"),
                new ColorRole("InfoOnFillBrush", "Labels on InfoFillBrush"),
                new ColorRole("NeutralFillBrush", "Requests without a status"),
                new ColorRole("NeutralOnFillBrush", "Labels on NeutralFillBrush"),
            }),
        };

        public static IEnumerable<ColorRole> Roles => RoleGroups.SelectMany(group => group.Roles);

        // Colors that belong to one component, grouped by component. They follow the same rules as roles, but only
        // their component uses them.
        public static IReadOnlyList<ColorRoleGroup> ComponentColorGroups { get; } = new[]
        {
            new ColorRoleGroup("Start Capture button", new[]
            {
                new ColorRole("CaptureActionBackgroundBrush", "The button's face: a soft gradient of Xbox green"),
                new ColorRole("CaptureActionHoverBrush", "The button under the pointer"),
                new ColorRole("CaptureActionPressedBrush", "The button while it's pressed"),
                new ColorRole("CaptureActionBorderBrush", "The button's outline"),
                new ColorRole("CaptureActionSheenBrush", "The light line along the top of the button"),
                new ColorRole("RecordingIndicatorBrush", "The dot that shows a capture is recording"),
            }),
            new ColorRoleGroup("Controls", new[]
            {
                new ColorRole("InputBorderBrush", "Text box and dropdown outlines: a soft outline with a strong bottom edge"),
                new ColorRole("HoverOutlineBrush", "The outline that marks hover. Only high contrast shows it"),
                new ColorRole("ScrollBarBackgroundBrush", "The scroll bar's track"),
            }),
            new ColorRoleGroup("Capture tabs", new[]
            {
                new ColorRole("TabItemInsetShadowBrush", "The soft shadow at the base of unselected capture tabs"),
                new ColorRole("TabItemSelectedEdgeBrush", "The selected capture tab's top edge. Only high contrast shows it"),
            }),
            new ColorRoleGroup("Tables and charts", new[]
            {
                new ColorRole("ColumnHeaderDividerBrush", "Dividers between column headers, and the neutral bars of charts"),
                new ColorRole("ColumnDividerBrush", "The divider between a capture's table and its inspector"),
            }),
            new ColorRoleGroup("Loading", new[]
            {
                new ColorRole("SkeletonBaseBrush", "Placeholders shown while content loads"),
                new ColorRole("SkeletonHighlightBrush", "The shimmer that moves across a placeholder"),
            }),
            new ColorRoleGroup("Analysis report", new[]
            {
                new ColorRole("ExpanderHeaderBrush", "Endpoint headers"),
                new ColorRole("ExpanderSubHeaderBrush", "Rule headers"),
                new ColorRole("ExpanderDetailBrush", "Violation details"),
            }),
        };

        public static IEnumerable<ColorRole> ComponentColors => ComponentColorGroups.SelectMany(group => group.Roles);

        // Text and the surfaces it appears on, which must meet ReadableContrast: body and secondary text on every
        // surface, headings, button text on a button's states, text on the accent fill, and status text on surfaces and
        // its soft background.
        public static IReadOnlyList<(string Text, string Surface)> ReadablePairs { get; } =
            RoleGroups.Single(group => group.Name == "Surfaces").Roles
                .SelectMany(surface => new[] { ("TextPrimaryBrush", surface.Key), ("TextSecondaryBrush", surface.Key) })
                .Concat(new[]
                {
                    ("TextHeadingBrush", "SurfacePanelBrush"), ("TextHeadingBrush", "SurfaceSelectedBrush"),
                    ("TextControlBrush", "SurfaceControlBrush"), ("TextControlBrush", "SurfaceHoverBrush"),
                    ("TextControlBrush", "AccentSoftBrush"), ("AccentOnFillBrush", "AccentFillBrush"),
                })
                .Concat(new[] { "Success", "Warning", "Error" }.SelectMany(status => new[]
                {
                    ($"{status}TextBrush", $"{status}SoftBrush"), ($"{status}TextBrush", "SurfacePanelBrush"),
                    ($"{status}TextBrush", "SurfaceWellBrush"),
                }))
                .ToList();
    }
}
