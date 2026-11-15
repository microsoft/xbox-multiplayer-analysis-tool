# Design system

XMAT's views take their colors from a small set of named roles, so that every view looks consistent in the Light,
Dark and High Contrast themes and a color can be changed in one place. Their controls take their styles from one file
per component. Tests in `XMAT.Tests/DesignSystem` and `XMAT.Tests/HighContrastThemeTests.cs` check the rules on this
page.

## Colors

Colors come in three layers:

1. **Base colors**, in [`XMAT/Themes/Palette.json`](../XMAT/Themes/Palette.json): a gray and four colors from Windows 11,
   Blue `#0078D4`, Green `#107C10` (Xbox green), Amber `#9D5D00` and Red `#C42B1C`.
2. **The palette**, in [`XMAT/Themes/Palette.xaml`](../XMAT/Themes/Palette.xaml), generated from the base colors. Each
   base color becomes a ramp of steps from light to dark: `Gray1` (white) to `Gray16`, and `Blue1` to `Blue12`, and so
   on. Each base color is one of the steps of its ramp, such as `Blue8`.
3. **Roles**, in `XMAT/Themes/LightTheme.xaml`, `DarkTheme.xaml` and `HighContrastTheme.xaml`: 38 named uses, such as
   `TextPrimaryBrush` or `SurfacePanelBrush`. The Light and Dark themes give each role a palette step; the High
   Contrast theme gives it a Windows system color, so it follows the user's contrast theme.

Views and styles use roles, never palette steps or color values:

```xml
<TextBlock Foreground="{DynamicResource TextSecondaryBrush}"/>
```

Use `DynamicResource`, so the view follows theme changes.

### Roles

| Role | Use |
| --- | --- |
| `SurfaceCanvasBrush` | Window chrome: the window, menus, status bar and unselected tabs |
| `SurfacePanelBrush` | Content panels, the selected tab, text boxes and toolbars |
| `SurfaceWellBrush` | Recessed wells: tables and text areas |
| `SurfaceHeaderBrush` | The header band of a table |
| `SurfaceStripBrush` | The capture tab strip |
| `SurfaceHoverBrush` | A hovered button or row |
| `SurfaceControlBrush` | A button's face |
| `TextPrimaryBrush` | Body text |
| `TextSecondaryBrush` | Labels, captions, column headers and the status bar |
| `TextDisabledBrush` | Disabled controls |
| `TextHeadingBrush` | Section headings |
| `TextControlBrush` | Button text |
| `BorderSubtleBrush` | Table grid lines and the faintest dividers |
| `BorderDefaultBrush` | Panel borders and dividers |
| `BorderControlBrush` | Button outlines and the pane splitter |
| `BorderStrongBrush` | Strong outlines and scroll bar thumbs |
| `BorderStrongHoverBrush` | A strong outline under the pointer |
| `BorderWellBrush` | A text area's outline |
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

A few colors belong to one component and keep their own names: the capture button (`CaptureAction*`), input outlines
(`InputBorderBrush`), skeleton placeholders (`Skeleton*`), the recording indicator, table column dividers, the capture
tabs' edge and shadow, the hover outline, the scroll bar track and the analysis report's expanders. They follow the same
rules as roles.

### Rules

- Views and styles use roles and component colors, never palette steps or color values.
- The Light and Dark themes take every color from the palette. Only `Transparent` and black or white overlays, such as
  `#38FFFFFF`, are written as values.
- The Light, Dark and High Contrast themes define every role, and the High Contrast theme defines every key of the
  other themes.
- Text roles meet a 4.5:1 contrast ratio on every surface they appear on, and the status text and label roles on
  their fills and soft backgrounds.
- `Palette.xaml` matches `Palette.json`.
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
2. Add it to `Roles` in `XMAT.Tests/DesignSystem/ColorRoleTests.cs` and to the table on this page.
3. If it is text, or a fill that holds text, add its contrast pairs to the tests.

### The original look

While the New look switch exists, the original look keeps its own themes in `XMAT/Themes/Original`. Views shared by
both looks use roles, and the original themes define those roles in their original colors. A few shared views still
use three old names, `MenuBackgroundBrush`, `AltRowBackgroundBrush` and `CapturePendingForegroundBrush`, and
`IssueBadge` uses the original issue colors in the original look. `ColorRoleTests` lists every remaining use; the list
only shrinks. Remove them, and the original themes, with the original look.

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
| `Menus.xaml` | Menu bars, dropdowns, context menus and the More menu |
| `TrafficOverview.xaml`, `AnalysisCharts.xaml`, `DataViz.xaml` | The traffic overview, the call rate charts, and the column summaries a table's column headers open |
| `Spacing.xaml`, `Motion.xaml` | The 4px spacing grid, and animation durations and curves |

Tables, scroll bars, tree views, tooltips, the status bar and text styles are still in `NewLook.xaml`; they move to
files of their own as they are worked on.

### Rules

- A style file takes `StaticResource` values, `BasedOn` included, only from itself or from a dictionary it merges, so
  it loads on its own. Keep a style in the same file as the style it is based on. Take theme colors, spacing and type
  sizes with `DynamicResource`; a file whose storyboards use `Motion.xaml`'s values merges `Motion.xaml`.
  `StyleFileTests` checks this.
- A new component gets its own file in `XMAT/Styles`. Merge it in `NewLook.xaml`, and add it to
  `XMAT.Tests/NewLookMarkup.cs` so tests can read its styles.
