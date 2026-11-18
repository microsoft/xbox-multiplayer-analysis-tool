# Design system

XMAT's views take their colors from a small set of named roles, so that every view looks consistent in the Light,
Dark and High Contrast themes and a color can be changed in one place. Their controls take their styles from one file
per component. Tests in `XMAT.Tests/DesignSystem` and `XMAT.Tests/HighContrastThemeTests.cs` check the rules on this
page.

## Rules

Each rule has an ID that never changes: its page's prefix and a number, such as `COLOR-3` on the Color page or
`BUTTON-2` on the Buttons page. A **Must** rule names the tests that fail when XMAT breaks it, and their failures start
with its ID, so a failure points at the rule. A **Should** rule is checked in review, and becomes a Must once a test
checks it. The rules on this page are tagged with their IDs.

[`XMAT.DesignSystem/RuleCatalog.cs`](../XMAT.DesignSystem/RuleCatalog.cs) lists every rule with why it holds and the
tests that check it. The design system app shows each page's rules with live examples of following them and breaking
them. To add a rule:

1. Add it to `RuleCatalog.cs` with its page's prefix and the next number, saying what it is and why.
2. Show it on its page with a `ds:StoryRule`. When it can be shown, give it a `ds:RuleExample` with `Kind="Do"` and
   one with `Kind="Dont"`, each with a caption that says what to notice.
3. For a Must rule, name the tests that check it, and start their failure messages with its ID. A check of the
   product's source goes in `XMAT.Tests/DesignSystem/ProductRuleTests.cs`, which lists any use the rule allows, with
   why.

## Colors

Colors come in three layers:

1. **Base colors**, in [`XMAT/Themes/Palette.json`](../XMAT/Themes/Palette.json): a gray and four colors from Windows 11,
   Blue `#0078D4`, Green `#107C10` (Xbox green), Amber `#9D5D00` and Red `#C42B1C`.
2. **The palette**, in [`XMAT/Themes/Palette.xaml`](../XMAT/Themes/Palette.xaml), generated from the base colors. Each
   base color becomes a ramp of steps from light to dark: `Gray1` (white) to `Gray16`, and `Blue1` to `Blue12`, and so
   on. Each base color is one of the steps of its ramp, such as `Blue8`.
3. **Roles**, in `XMAT/Themes/LightTheme.xaml`, `DarkTheme.xaml` and `HighContrastTheme.xaml`: 31 named uses, such as
   `TextPrimaryBrush` or `SurfacePanelBrush`. The Light and Dark themes give each role a palette step; the High
   Contrast theme gives it a Windows system color, so it follows the user's contrast theme.

The design system app's Color page shows all three layers, read from these files, with every role's value in each theme
and a grid of the contrast between the colors that must stand out from each other. The roles, the component colors and
those contrast pairs are listed, with what each is for, in
[`XMAT.DesignSystem/Foundations/ColorCatalog.cs`](../XMAT.DesignSystem/Foundations/ColorCatalog.cs); the design system
tests check the themes against that list.

Views and styles use roles, never palette steps or color values:

```xml
<TextBlock Foreground="{DynamicResource TextSecondaryBrush}"/>
```

Use `DynamicResource`, so the view follows theme changes.

### Roles

| Role | Use |
| --- | --- |
| `SurfaceCanvasBrush` | Window chrome, such as menus, the status bar and unselected tabs, and the header band of a table |
| `SurfacePanelBrush` | Content panels, the selected tab, text boxes, toolbars and buttons |
| `SurfaceWellBrush` | Recessed wells: tables and text areas |
| `SurfaceHoverBrush` | A hovered button or row |
| `SurfaceSelectedBrush` | A chosen card, such as the device in New Capture. High Contrast keeps the window color and shows the choice with the highlight color |
| `TextPrimaryBrush` | All text people read, headings included. Size and weight, not color, make a heading |
| `TextSecondaryBrush` | Labels, captions, column headers and the status bar |
| `TextDisabledBrush` | Disabled controls |
| `TextLinkBrush` | Links, such as the release notes link in What's new. Links are also underlined, so color isn't their only sign |
| `BorderSubtleBrush` | Dividers, panel edges and table grid lines |
| `BorderStrongBrush` | The outline that identifies a control, such as a check box, scroll bar thumbs and the pane splitter while it's dragged |
| `BorderHoverBrush` | An outline under the pointer |
| `AccentBrush` | Selection outlines, the selected tab's indicator and chart lines |
| `AccentFillBrush` | A selected item's fill |
| `AccentOnFillBrush` | Text on `AccentFillBrush` and on the capture button |
| `AccentSoftBrush` | Pressed buttons and checked toggles |
| `SuccessFillBrush`, `SuccessOnFillBrush` | 2xx responses in the timeline and charts, and their labels |
| `SuccessTextBrush`, `SuccessSoftBrush` | Good news, such as an analysis check that found no issues: text, and its soft background |
| `WarningFillBrush`, `WarningOnFillBrush` | 4xx responses, and their labels |
| `WarningTextBrush`, `WarningSoftBrush` | Warnings and pending captures, and their soft background |
| `ErrorFillBrush`, `ErrorOnFillBrush` | 5xx responses, and their labels |
| `ErrorTextBrush`, `ErrorSoftBrush` | Errors, and their soft background |
| `InfoFillBrush`, `InfoOnFillBrush` | 3xx redirects, and their labels |
| `NeutralFillBrush`, `NeutralOnFillBrush` | Requests without a status, and their labels |

Each surface is a palette step away from the next, so a level needs no border to stand out: wells sit a step below
panels, and the chrome a step below wells. Headings take the body text color; their size and weight set them apart.

A few colors belong to one component and keep their own names: button text and outlines (`ButtonTextBrush`,
`ButtonBorderBrush`), the capture button (`CaptureAction*`), input outlines (`InputBorderBrush`), the edge of a well
(`WellEdgeBrush`), skeleton placeholders (`Skeleton*`), the recording indicator, table column dividers, the capture tab
strip (`TabStripBrush`) and the tabs' edge and shadow, the hover outline, the scroll bar track and the analysis
report's expanders. They follow the same rules as roles.

### Rules

- **COLOR-1** Views and styles use roles and component colors, never palette steps or color values.
  `ProductRuleTests` lists the few colors the product writes in, with why, such as shadows and colors that come from
  data.
- **COLOR-2** Views take theme colors with `DynamicResource`, so they follow theme changes.
- **COLOR-3** Text meets a 4.5:1 contrast ratio on every surface it appears on, and the outlines that identify
  controls meet 3:1 on the surfaces they sit on. `ColorCatalog.ContrastGroups` lists the pairs and the ratio each
  group needs. Status labels also meet 4.5:1 on their fills.
- **COLOR-4** Text stays readable in every Windows contrast theme.
- **COLOR-5** The Light and Dark themes take every color from the palette, and `Palette.xaml` matches `Palette.json`.
  Only `Transparent` and black or white overlays, such as `#38FFFFFF`, are written as values.
- **COLOR-6** The Light, Dark and High Contrast themes define every role, and the High Contrast theme defines every
  key of the other themes. Every color the themes define is a role or a component color in `ColorCatalog`, with what
  it is for, so the Color page shows it.
- **COLOR-7** Color is never the only signal: a status also has text or an icon.
- Don't alias a key with `<StaticResource x:Key="..." ResourceKey="..."/>` in a theme file: WPF resolves such aliases
  in compiled dictionaries to the wrong resource. Define a brush on the same palette step instead.

### Change a base color

1. Change the color's `base` in `Palette.json`. Keep its `baseStep`, the step the color takes in its ramp.
2. Regenerate `Palette.xaml`:

   ```powershell
   $env:XMAT_PALETTE_WRITE = "1"
   dotnet test XMAT.Tests\XMAT.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~PaletteTests"
   Remove-Item Env:XMAT_PALETTE_WRITE
   ```

3. Run the design system and theme tests. A role that no longer meets its contrast ratio fails with the colors and the
   ratio; give that role another step in the theme.

`XMAT.Tests/DesignSystem/PaletteGenerator.cs` describes how the ramps are made. In short, it works in OKLCH, a color
space whose lightness and colorfulness match what people see: every ramp shares the same lightness steps, the steps
near the base color keep its colorfulness, and the others follow a shared curve, so the ramps line up with each other.

### Add a role

Use an existing role when one fits. When a new use needs a color of its own:

1. Add the role to `LightTheme.xaml` and `DarkTheme.xaml` as a palette step, and to `HighContrastTheme.xaml` as a
   system color, in the matching group.
2. Add it, with what it is for, to its group in `XMAT.DesignSystem/Foundations/ColorCatalog.cs`, and to the table on
   this page. A color for one component goes in `ComponentColorGroups` instead.
3. If it is text, an outline that identifies a control, or a surface either appears on, add its pairs to the matching
   group in `ColorCatalog.ContrastGroups`.

### The original look

While the New look switch exists, the original look keeps its own themes in `XMAT/Themes/Original`. Views shared by
both looks use roles, and the original themes define those roles in their original colors. A few shared views still
use three old names, `MenuBackgroundBrush`, `AltRowBackgroundBrush` and `CapturePendingForegroundBrush`, and
`IssueBadge` uses the original issue colors in the original look. `ColorRoleTests` lists every remaining use; the list
only shrinks. Remove them, and the original themes, with the original look.

## Status

What a state means to the person looking at it sets how XMAT shows it, the same way in every view.
[`XMAT/Logic/StatusMeanings.cs`](../XMAT/Logic/StatusMeanings.cs) gives every state one of five meanings:

| Meaning | How it shows | States |
| --- | --- | --- |
| Problem | Red, with a cross | 5xx, a device that can't be reached, a web proxy that couldn't stop cleanly |
| Needs attention | Amber, with a warning sign | 4xx, a device whose proxy setting can't be read |
| Live | The recording dot and Capturing | A running capture or trace |
| Working | What's happening, in words, without color | A trace connecting, starting, stopping, downloading or disconnecting |
| Routine | No color | 2xx, 3xx, a request without a status yet, an idle capture, a proxy set as expected |

In charts, where color is the data, each status class has a fill of its own: 2xx green, 3xx blue, 4xx amber, 5xx red
and no status gray. A request without a status is routine until the proxy records the requests that fail; until then
it can't be told from one that is still waiting.

A table shows an HTTP status with `StatusText` ([`XMAT/Controls/StatusText.cs`](../XMAT/Controls/StatusText.cs)), drawn
by [`XMAT/Styles/Status.xaml`](../XMAT/Styles/Status.xaml) with the problem and warning icons. Screen readers hear the
code and what a problem or a warning means. The original look keeps its whole-row colors.

- **STATUS-1** Every state XMAT shows has one meaning, set in `StatusMeanings`. A state gets its meaning there before
  a view shows it; one without a meaning throws.
- **STATUS-2** In a table, only a problem's or a warning's status cell takes color, with its icon. The rest of the
  row stays body text.
- **STATUS-3** Charts color each status class the same way: the timeline, the traffic overview and the column
  summaries.
- **STATUS-4** In a table, the status codes line up: every row keeps the icon's space, whether or not it shows the
  icon.

## Components

The new look's resources start in [`XMAT/Looks/NewLook.xaml`](../XMAT/Looks/NewLook.xaml), which `App.xaml` merges. It
holds the corner, spacing and type size values every view shares, and merges one style file per component from
`XMAT/Styles`:

| File | Styles |
| --- | --- |
| `Buttons.xaml` | Buttons, icon buttons, the tab close button, toggle buttons and the filter toggles |
| `Toolbar.xaml` | The capture toolbar, its Start/Stop capture button and the recording indicator |
| `Inputs.xaml` | Text boxes, dropdowns, check boxes and radio buttons |
| `Tabs.xaml` | Capture, inspector and format tabs, and the More buttons that end a tab row |
| `ChoiceCards.xaml`, `CaptureIcons.xaml` | Choice cards, such as the devices and capture types in New Capture, and the pictures on them |
| `ScrollBars.xaml` | Scroll bars, with a thumb at least 24px long |
| `Menus.xaml` | Menu bars, dropdowns, context menus and the More menu |
| `TrafficOverview.xaml`, `AnalysisCharts.xaml`, `DataViz.xaml` | The traffic overview, the call rate charts, the column summaries a table's column headers open, and the histograms in analysis reports |
| `Status.xaml` | An HTTP status in a table, and the problem and warning icons |
| `Spacing.xaml`, `Motion.xaml` | The 4px spacing grid, and animation durations and curves |

Tables, tree views, tooltips, the status bar and text styles are still in `NewLook.xaml`; they move to
files of their own as they are worked on.

### Rules

- A style file takes `StaticResource` values, `BasedOn` included, only from itself or from a dictionary it merges, so
  it loads on its own. Keep a style in the same file as the style it is based on. Take theme colors, spacing and type
  sizes with `DynamicResource`; a file whose storyboards use `Motion.xaml`'s values merges `Motion.xaml`.
  `StyleFileTests` checks this.
- A new component gets its own file in `XMAT/Styles`. Merge it in `NewLook.xaml`, and add it to
  `XMAT.Tests/NewLookMarkup.cs` so tests can read its styles.

### Buttons

- **BUTTON-1** Label a button with the action it takes, using a verb, such as Add capture or Delete. Not OK, Yes or
  No.
- **BUTTON-2** A button that shows only an icon has a tooltip and an accessible name that say what it does.
- **BUTTON-3** Use a toggle button for an option that stays on, such as Wrap lines, and a standard button for an
  action.

### Choice cards

- **CHOICE-1** A field that an option needs shows on its card from the start, and using the field chooses the card.
  People see what an option asks for before they choose it, such as the address in New Capture's Xbox console by
  address. Tab reaches the field once its card is chosen.

## The design system app

The design system app, `XMAT.DesignSystem`, has a page for each foundation and component, listed under their sections:
**Foundations**, such as color, read from the files that define them, and **Components**, with what each is for, the
files that define it, and examples of it in the states it can be set to, such as disabled, checked or running. Each
page ends with its rules. Hover, press and keyboard focus show live. The settings above a page preview it in Light or
Dark, in a Windows contrast theme, and with Comfort view on or off. The app runs from source and isn't part of XMAT's
release build. Run it from the repository root:

```powershell
dotnet run --project XMAT.DesignSystem -p:Platform=x64
```

In Visual Studio, set `XMAT.DesignSystem` as the startup project.

Pages are XAML files in [`XMAT.DesignSystem/Stories`](../XMAT.DesignSystem/Stories). To add one:

1. Create `<Name>Story.xaml` with `ds:Story` as its root, setting `Title`, `Summary`, its `Section` (`Foundations`,
   `Components`, `Patterns` or `Layouts`) and `DefinedIn`: the files that define what it shows, relative to the
   repository and separated by commas, such as `XMAT/Styles/Buttons.xaml`.
2. Follow the pages' outline, leaving out what doesn't apply. A component's page starts with a `ds:StoryUsage`: what
   it is for, and what it isn't for with what to use instead.
3. Add a `ds:StorySection` for each variant, with a `Title`, a `Description` and the `StyleName` it uses, holding a
   `ds:StoryExample` for each state, with a `Caption`. Build examples the way views do, so a page shows how to use
   the component. XMAT's controls are in `clr-namespace:XMAT;assembly=XboxMultiplayerAnalysisTool`. A foundation
   page builds its examples from the files that define the foundation, as the Color page reads the themes, so it
   can't fall behind them.
4. End with a `ds:StoryRules` holding the page's rules, as [Rules](#rules) describes.
5. Take the new look's resources with `DynamicResource`. A page loads before it joins the window that holds them, so
   a `StaticResource` wouldn't find them.
6. Add the page to `XMAT.DesignSystem/StoryCatalog.cs`, with the other pages of its section. `DesignSystemWindowTests`
   checks that every page is in a section, names files that exist and shows its examples.
7. Run the design system tests. `StoryCoverageTests` asks for the styles the page now shows to be removed from
   `XMAT.Tests/DesignSystem/UncoveredStyles.txt`, and `ReferenceImageTests` asks for the page's pictures; create them
   as below.

### Checks

- **Every color is on the Color page.** `ColorRoleTests` fails when a theme defines a color that `ColorCatalog`
  doesn't list, when a theme misses one it lists, or when one of its contrast pairs falls short of the ratio its group
  needs: 4.5:1 for text, 3:1 for outlines.
- **The rules hold together.** `RuleCatalogTests` fails when a rule's ID doesn't name its page, when a Must rule names
  no test, a test that doesn't exist or one whose failures don't cite its ID, or when a page doesn't show its rules,
  each with a Do and a Don't example when it has examples.
- **The product follows the rules.** `ProductRuleTests` reads the product's XAML and C#, and its failures cite the rule
  broken: a color written in (COLOR-1), a theme color taken with `StaticResource` (COLOR-2), or a button that shows only
  an icon without a tooltip and a name (BUTTON-2). Its lists of the uses a rule allows only shrink.
- **Every style is on a page.** `StoryCoverageTests` opens every page, with its menus and dropdowns open, and fails
  when a style of the new look appears on none. Styles that no page shows yet are listed in
  `XMAT.Tests/DesignSystem/UncoveredStyles.txt`; the list only shrinks, because a new style comes with its example.
- **Pictures of every page.** `ReferenceImageTests` keeps pictures of each page in Light, in Dark with Comfort view
  off, and in the Night sky contrast theme, and of the open context menu, in
  [`XMAT.Tests/DesignSystem/Reference`](../XMAT.Tests/DesignSystem/Reference). Pages are rendered on their own, at 96
  DPI, 100% text size and without animations, so the pictures don't depend on the display. `XMAT.Tests.runsettings`
  runs the tests without DPI awareness, so WPF works at 96 DPI even when Windows was signed in at another scale. When a
  component changes, its pictures fail, and the test writes what it rendered and a picture of the differences, in
  magenta, to `XMAT.Tests/TestResults/DesignSystem`. When the change is intended, update the pictures and commit them
  with it, so review shows the before and after:

  ```powershell
  $env:XMAT_REFERENCE_WRITE = "1"
  dotnet test XMAT.Tests\XMAT.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ReferenceImageTests"
  Remove-Item Env:XMAT_REFERENCE_WRITE
  ```

  If pictures differ on a machine for reasons outside XMAT, such as another version of Windows' fonts, set
  `XMAT_REFERENCE_IMAGES=report` to report the differences without failing.
