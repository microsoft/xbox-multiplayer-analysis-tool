// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace XMAT.DesignSystem
{
    // How firmly a rule holds. A Must rule names the tests that fail when the product breaks it. A Should rule is checked
    // in review, and becomes a Must once a test checks it.
    public enum RuleLevel
    {
        Must,
        Should,
    }

    // A rule of the design system. Its ID never changes: it is the page's prefix and a number, such as COLOR-3, and the
    // tests that check the rule start their failure messages with it, so a failure points at the rule and its page.
    // CHECKED BY names those tests as Class.Method.
    public sealed record DesignRule(string Id, RuleLevel Level, string Page, string Text, string Why, params IReadOnlyList<string> CheckedBy)
    {
        public string LevelText => Level.ToString();

        public string CheckText =>
            CheckedBy.Count == 0 ? "Checked in review" : "Checked by " + string.Join(", ", CheckedBy);
    }

    // The design system's rules, page by page. Each page shows its rules with examples of following and breaking them
    // (Story.cs), and the design system tests check that every rule is on its page and that every Must rule's tests
    // exist (docs/design-system.md).
    internal static class RuleCatalog
    {
        internal static IReadOnlyList<DesignRule> Rules { get; } = new[]
        {
            new DesignRule("COLOR-1", RuleLevel.Must, "Color",
                "Views and styles take every color from a role or a component color, never from a palette step or a color value written in.",
                "A role changes in one place and follows Light, Dark and high contrast. A color written in stays the same in every theme.",
                "ProductRuleTests.Views_TakeColorsFromRoles"),
            new DesignRule("COLOR-2", RuleLevel.Must, "Color",
                "Views take theme colors with DynamicResource, not StaticResource.",
                "The theme can change while XMAT runs. A StaticResource keeps the color it found when the view loaded.",
                "ProductRuleTests.Views_TakeThemeColorsWithDynamicResource"),
            new DesignRule("COLOR-3", RuleLevel.Must, "Color",
                "Text stands out from its surface by at least 4.5:1, and the outline that identifies a control by at least 3:1.",
                "These are the WCAG AA minimums. People read XMAT for hours, on bright and dim screens.",
                "ColorRoleTests.Colors_StandOutFromTheirSurfaces"),
            new DesignRule("COLOR-4", RuleLevel.Must, "Color",
                "Text stays readable in every Windows contrast theme.",
                "People who turn on a contrast theme need its colors, so XMAT takes them rather than its own.",
                "HighContrastThemeTests.HighContrastTheme_KeepsTextLegibleInEachContrastTheme"),
            new DesignRule("COLOR-5", RuleLevel.Must, "Color",
                "Light and Dark take every color from the palette, and the palette is generated from Palette.json.",
                "The ramps line up, so the themes stay one family of colors, and a base color changes everywhere at once.",
                "ColorRoleTests.NewLookThemes_TakeEveryColorFromThePalette", "PaletteTests.Palette_MatchesPaletteJson"),
            new DesignRule("COLOR-6", RuleLevel.Must, "Color",
                "Every theme defines every role, and every color a theme defines is listed on this page with what it is for.",
                "A color nobody can find gets added twice, and a role a theme misses shows the wrong color.",
                "ColorRoleTests.EveryTheme_DefinesEveryRole_AndTheNewLookThemesMatch",
                "ColorRoleTests.EveryThemeColor_IsInTheDesignSystem"),
            new DesignRule("COLOR-7", RuleLevel.Should, "Color",
                "Color is never the only signal: a status also has text or an icon.",
                "Some people can't tell red from green, and a contrast theme replaces every color."),

            new DesignRule("STATUS-1", RuleLevel.Must, "Status",
                "Every state XMAT shows has one meaning, set in StatusMeanings: a problem, needs attention, live, working or routine. The meaning sets how the state shows, in every view.",
                "A color then means the same thing everywhere, so people learn it once, and no state is shown before someone decides what it means.",
                "StatusMeaningTests.EveryState_HasAMeaning"),
            new DesignRule("STATUS-2", RuleLevel.Must, "Status",
                "In a table, only a problem's or a warning's status cell takes color, with its icon. The rest of the row stays body text.",
                "A busy capture stays calm and readable, and the few problems in it stand out.",
                "StatusMeaningTests.Table_ColorsOnlyTheStatusCellOfAProblemOrWarning"),
            new DesignRule("STATUS-3", RuleLevel.Must, "Status",
                "Charts color each status class the same way: 2xx green, 3xx blue, 4xx amber, 5xx red, and no status gray.",
                "In a chart, color is the data, so it has to read the same in the timeline, the traffic overview and the column summaries.",
                "StatusMeaningTests.Charts_ColorEachStatusClassTheSameWay"),
            new DesignRule("STATUS-4", RuleLevel.Must, "Status",
                "In a table, the status codes line up: every row keeps the icon's space, whether or not it shows the icon.",
                "A straight column of codes is quick to scan, and the icons make a column of their own beside it.",
                "StatusMeaningTests.Table_LinesUpEveryStatusCode"),

            new DesignRule("BUTTON-1", RuleLevel.Should, "Buttons",
                "Label a button with the action it takes, using a verb, such as Add capture or Delete. Not OK, Yes or No.",
                "People choose by reading the button, without going back to the question above it."),
            new DesignRule("BUTTON-2", RuleLevel.Must, "Buttons",
                "A button that shows only an icon has a tooltip and an accessible name that say what it does.",
                "An icon alone is ambiguous, and Narrator has nothing else to read.",
                "ProductRuleTests.IconButtons_HaveANameAndATooltip"),
            new DesignRule("BUTTON-3", RuleLevel.Should, "Buttons",
                "Use a toggle button for an option that stays on, such as Wrap lines, and a standard button for an action.",
                "A toggle button shows whether its option is on. An action has nothing to show once it's done."),
        };

        // The rule with ID, or null when there is none.
        internal static DesignRule Find(string id) =>
            Rules.FirstOrDefault(rule => string.Equals(rule.Id, id, StringComparison.Ordinal));

        internal static IEnumerable<DesignRule> OnPage(string page) =>
            Rules.Where(rule => string.Equals(rule.Page, page, StringComparison.Ordinal));
    }
}
