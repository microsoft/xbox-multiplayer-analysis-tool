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

    // A color drawn on a surface, which must stand out from it by a contrast ratio.
    public sealed record ContrastPair(string Foreground, string Surface);

    // Pairs that are checked together, such as text on the surfaces, and the contrast they need. Outlines are drawn
    // around text rather than as text.
    public sealed record ContrastGroup(string Name, double Needs, IReadOnlyList<ContrastPair> Pairs, bool Outlines = false);

    // XMAT's colors as the design system defines them: the roles views and styles use, the colors that belong to one
    // component, and the pairs of colors that must stand out from each other. The Color page shows them with the values
    // the themes give them, and the design system tests (XMAT.Tests/DesignSystem/ColorRoleTests.cs) check the themes
    // against this list: every theme defines every color here, every color the themes define is here, and every pair
    // meets its contrast in Light and Dark. To add a color, add it here and to the themes (docs/design-system.md).
    public static class ColorCatalog
    {
        // WCAG's contrast for text, and for the outlines and parts that identify a control.
        public const double TextContrast = 4.5;
        public const double ControlContrast = 3.0;

        public static IReadOnlyList<ColorRoleGroup> RoleGroups { get; } = new[]
        {
            new ColorRoleGroup("Surfaces", new[]
            {
                new ColorRole("SurfaceCanvasBrush", "Window chrome, such as menus, the status bar and unselected tabs, and the header band of a table"),
                new ColorRole("SurfacePanelBrush", "Content panels, the selected tab, text boxes, toolbars and buttons"),
                new ColorRole("SurfaceWellBrush", "Recessed wells: tables and text areas"),
                new ColorRole("SurfaceHoverBrush", "A hovered button or row"),
                new ColorRole("SurfaceSelectedBrush", "A chosen card, such as the device in New Capture. High contrast keeps the background and marks the choice with the selected text background"),
            }),
            new ColorRoleGroup("Text", new[]
            {
                new ColorRole("TextPrimaryBrush", "All text people read, headings included. Size and weight, not color, make a heading"),
                new ColorRole("TextSecondaryBrush", "Labels, captions, column headers and the status bar"),
                new ColorRole("TextDisabledBrush", "Disabled controls"),
                new ColorRole("TextLinkBrush", "Links, such as the release notes link in What's new. Links are also underlined, so color isn't their only sign"),
            }),
            new ColorRoleGroup("Borders", new[]
            {
                new ColorRole("BorderSubtleBrush", "Dividers, panel edges and table grid lines"),
                new ColorRole("BorderStrongBrush", "The outline that identifies a control, such as a check box, scroll bar thumbs and the pane splitter while it's dragged"),
                new ColorRole("BorderHoverBrush", "An outline under the pointer"),
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
                new ColorRole("ErrorFillBrush", "5xx responses, and the button of an action that can't be undone"),
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
            new ColorRoleGroup("Buttons", new[]
            {
                new ColorRole("ButtonTextBrush", "Button text. It's the text color, except in high contrast, which gives buttons their own text color"),
                new ColorRole("ButtonBorderBrush", "Button outlines, in the button text color in high contrast"),
            }),
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
                new ColorRole("WellEdgeBrush", "The edge of a recessed well, such as a text area. Only high contrast shows it"),
                new ColorRole("ScrollBarBackgroundBrush", "The scroll bar's track"),
            }),
            new ColorRoleGroup("Capture tabs", new[]
            {
                new ColorRole("TabStripBrush", "The strip the capture tabs sit on"),
                new ColorRole("TabItemInsetShadowBrush", "The soft shadow at the base of unselected capture tabs"),
                new ColorRole("TabItemSelectedEdgeBrush", "The selected capture tab's top edge. Only high contrast shows it"),
            }),
            new ColorRoleGroup("Tables and charts", new[]
            {
                new ColorRole("ColumnHeaderDividerBrush", "Dividers between column headers, and the neutral bars of charts"),
                new ColorRole("ColumnDividerBrush", "The divider between a capture's table and its inspector"),
                new ColorRole("ProblemRowBrush", "The faint wash behind a table row whose status is a problem, such as a 5xx response. High contrast shows none"),
                new ColorRole("AttentionRowBrush", "The faint wash behind a table row whose status needs attention, such as a 4xx response. High contrast shows none"),
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

        // The colors that must stand out from the surfaces they're on: text by 4.5:1, and the outlines that identify
        // controls by 3:1. Disabled text needn't, as WCAG allows.
        public static IReadOnlyList<ContrastGroup> ContrastGroups { get; } = new[]
        {
            new ContrastGroup("Text", TextContrast,
                new[] { "TextPrimaryBrush", "TextSecondaryBrush", "TextLinkBrush" }
                    .SelectMany(text => new[] { "SurfaceCanvasBrush", "SurfacePanelBrush", "SurfaceWellBrush", "SurfaceHoverBrush", "SurfaceSelectedBrush" }
                        .Select(surface => new ContrastPair(text, surface)))
                    .ToList()),
            new ContrastGroup("Buttons and selection", TextContrast, new[]
            {
                new ContrastPair("ButtonTextBrush", "SurfacePanelBrush"),
                new ContrastPair("ButtonTextBrush", "SurfaceHoverBrush"),
                new ContrastPair("ButtonTextBrush", "AccentSoftBrush"),
                new ContrastPair("AccentOnFillBrush", "AccentFillBrush"),
                new ContrastPair("ErrorOnFillBrush", "ErrorFillBrush"),
            }),
            new ContrastGroup("Status", TextContrast,
                new[] { "Success", "Warning", "Error" }
                    .SelectMany(status => new[]
                    {
                        new ContrastPair($"{status}TextBrush", $"{status}SoftBrush"),
                        new ContrastPair($"{status}TextBrush", "SurfacePanelBrush"),
                        new ContrastPair($"{status}TextBrush", "SurfaceWellBrush"),
                    })
                    .ToList()),
            // A washed row's text, and its status cell's code. A wash drawn at part strength is checked at full strength,
            // which is harder to read on.
            new ContrastGroup("Washed table rows", TextContrast, new[]
            {
                new ContrastPair("TextPrimaryBrush", "ProblemRowBrush"),
                new ContrastPair("TextPrimaryBrush", "AttentionRowBrush"),
                new ContrastPair("TextSecondaryBrush", "ProblemRowBrush"),
                new ContrastPair("TextSecondaryBrush", "AttentionRowBrush"),
                new ContrastPair("ErrorTextBrush", "ProblemRowBrush"),
                new ContrastPair("WarningTextBrush", "AttentionRowBrush"),
            }),
            new ContrastGroup("Control outlines", ControlContrast, new[]
            {
                new ContrastPair("BorderStrongBrush", "SurfacePanelBrush"),
                new ContrastPair("BorderStrongBrush", "SurfaceWellBrush"),
                new ContrastPair("ButtonBorderBrush", "SurfacePanelBrush"),
            }, Outlines: true),
        };
    }
}
