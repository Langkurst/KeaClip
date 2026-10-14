# KeaClip design system

The verified building blocks for every page. Reuse these instead of inventing new
colours, sizes or animation code. Source: `src/Klippi.Desktop/Theme/`,
`src/Klippi.Desktop/Controls/` and `src/Klippi.Desktop/Converters/`. The shared behaviour
is covered by `tests/Klippi.UiTests/DesignSystemTests.cs`.

## Principles

- Calm, professional, NZ-inspired: deep green-charcoal surfaces, one fresh green accent,
  a warm kea-orange used sparingly (trim handles, highlights, "not saved yet" dots). No
  gradients, no RGB.
- The default screen should feel effortless; advanced options stay collapsed.
- Every state is honest: skeletons while loading, real progress when known, an
  indeterminate ring only when it is not. An option that can't apply is shown disabled
  with the reason (tooltip with `ToolTip.ShowOnDisabled="True"`, plus a caption when the
  reason matters), never silently ignored. Disable the whole row, so its label dims with
  the control (TextBlocks do not dim on their own; Download styles
  `Border.inset Grid:disabled > TextBlock` with `AppFaint`).
- 60 FPS feel: animate only `Opacity` and transforms; never animate `Width`, `Height`,
  `Margin` or anything that forces layout per frame. Cache pens/brushes in custom
  `Render` code; render-loop work only while something is actually moving.
- Shared fixes live in the theme. A page never carries a local copy of a theme rule; if a
  page needs a workaround, fix the theme (or the control) and delete the page copy.

## Colour tokens (DynamicResource, dark and light variants)

| Token | Use |
| --- | --- |
| `AppBackground`, `AppSidebar` | Window and sidebar backgrounds |
| `AppSurface`, `AppSurfaceRaised`, `AppSurfaceHover`, `AppSurfacePressed` | Cards, raised panels, hover/pressed fills |
| `AppInput`, `AppInputHover` | Input/tile backgrounds |
| `AppBorder`, `AppBorderStrong` | 1px borders, dividers; stronger hover borders |
| `AppText`, `AppMuted`, `AppFaint` | Primary, secondary, tertiary text |
| `AppAccent` (+`Hover`/`Pressed`), `AccentText`, `AppAccentSubtle`, `AppAccentSubtleText` | Primary actions, selection, success |
| `AppWarm`, `AppWarmSubtle` | Kea orange: trim handles, highlights, unsaved-change dots |
| `AppDanger`, `AppWarning`, `AppInfo` (+`Subtle`) | Status colours and notice backgrounds |
| `AppVideoBackdrop`, `AppSkeleton`, `AppScrim` | Media frames, loading placeholders, overlays |
| `AppOnScrim` | Text and glyphs drawn on media: scrim badges on thumbnails and the video overlay (light in both themes) |
| `WaveformBackground`, `WaveformIdle`, `WaveformActive`, `WaveformSelection`, `Playhead` | Editor timeline |
| `ShadowCard`, `ShadowRaised`, `ShadowAccent` (BoxShadows) | Elevation |

Static: `RadiusCard` (14), `RadiusPill` (999), `ControlCornerRadius` (8).

Fluent keys are retinted in both theme dictionaries so stock controls match without page
styles: buttons, text boxes, combo boxes, sliders, toggles, check boxes, expanders,
tooltips, and every flyout and menu (`FlyoutPresenterBackground` /
`MenuFlyoutPresenterBackground` = `AppSurfaceRaised`, their borders = `AppBorderStrong`,
`MenuFlyoutItemBackgroundPointerOver` = `AppSurfaceHover`). A page may still set a
flyout's shape (`FlyoutPresenterClasses`, corner radius, padding), never its colours.
A disabled primary button keeps a readable label (at least 3:1 on its fill in both themes),
and its glyph dims with the label.

## Typography (TextBlock classes)

`title` 24 semibold (page title, shell only) · `h1` 19 · `section` 16 semibold (card
headings) · `strong` · `muted` (secondary, wraps) · `faint` · `caption` 12 · `label`
12 medium (field labels) · `overline` 11 semibold letter-spaced (group labels, write in
CAPS) · `accent` · `danger` · `tabular` (tabular digits for timecodes, sizes, percents).

## Surfaces (Border classes)

`card` (padding 22, radius 14, shadow) and `card compact` (padding 16) · `inset` (raised
nested panel) · `divider` (1px rule) · `chip` + `accent|warm|danger|warning|info`
(pill status badge) · `notice` + `warning|danger|accent` (inline message band) ·
`skeleton` (loading placeholder that breathes) · `media-frame` (thumbnail/video frame,
clips to radius 12) and `media-frame placeholder` (a quiet raised tile with a 1px border
until a picture arrives; bind it, for example
`Classes.placeholder="{Binding Thumbnail, Converter={x:Static ObjectConverters.IsNull}}"`) ·
`well` (36px rounded icon square on `AppAccentSubtle` whose glyph turns accent; anchors a
notice, row or heading; tones `warm`, `neutral` and `raised`). `well large` is the 38px
card-heading size with a 20px glyph, used by every card heading (Editor, Settings). A well
is top-aligned so it sits on the first line of wrapping text beside it; a page may centre
it on a short heading (Settings `Grid.section-head`).

Badges drawn on a picture use an `AppScrim` background with `AppOnScrim` text and glyphs.

## Buttons

Default `Button` is a secondary button. Variants: `primary` (accent, the one main action
per area), `ghost` (transparent until hover), `danger`, `link`, sizes `small` and
`large`, and `icon` (36px square; add `small` (30), `large` (46) or `round`). An icon
button keeps a zero inset in every size, so its glyph is drawn at its full `Size`; never
add page-local padding fixes. Put an icon and a label in a horizontal
`StackPanel Spacing="8"`; icon-only buttons need `ToolTip.Tip` and
`AutomationProperties.Name`.

Keyboard focus is a rounded accent ring with a thin background-coloured gap inside, drawn
on the button's edge (radius 8; `large` and `nav` 10; `round` fully round). It comes from
the theme for every page.

Shell-only classes: `nav` (sidebar items; `nav-dot` is a small warm dot at the end of an
item, used on Settings while changes are unsaved; an item with a trailing chip or dot sets
`HorizontalContentAlignment="Stretch"` and uses an unsized `*,Auto` grid, never a fixed
width, so its icon and label line up with the other items), `caption` (window buttons),
`shell-cancel` (the header's Cancel operation: always in layout, faded and disabled while
idle, so the title and subtitle never reflow when work starts; while idle it and its label
are `AccessibilityView="Raw"`, so a screen reader does not find the invisible button) and `on-scrim` (icon
buttons on video: glyphs use `AppOnScrim`). The fullscreen video overlay sits in a
`ThemeVariantScope RequestedThemeVariant="Dark"`, so its controls look like a player's in
both themes.

Segmented choices: `RadioButton Classes="tile"` with `GroupName`; checked tiles get the
accent border/fill, and a `c:StrokeIcon` inside turns accent automatically. The tile face
is inset 4px inside each RadioButton and keyboard focus is a separate rounded ring
(`AppText`, radius 16) around it, so focus never looks like the checked accent border.
Because of the inset, a grid of tiles uses a spacing 8px smaller than the visible gap it
wants and `Margin="-4"` so the faces line up with the content around them, for example
`ColumnSpacing="2" Margin="-4"` for a 10px gap (`0` and `-4` for an 8px gap).

Inputs: `TextBox` (add `hero` for the big URL field, `mono` for tabular digits),
`ComboBox` (items use the shared `ChoiceLabel` converter), `ToggleSwitch` for on/off
settings (On/Off text is suppressed and the switch is exactly its 40px track, so a
trailing switch in a `*,Auto` row reaches the right edge with no margin tricks; put the
label beside it), `Slider`, `NumericUpDown`, `Expander`.

## Icons

`<c:StrokeIcon Data="{StaticResource IconName}" Size="18" />` with
`xmlns:c="using:Klippi.Desktop.Controls"`. Classes `muted|faint|accent|warm|danger`.
`IsFilled="True"` for play/pause. The glyph is always drawn at `Size` (smaller only when
squeezed) and centred in whatever space the parent gives it, so an icon inside a badge,
well or tile needs no alignment styles. Available: Download, Queue, Library, Scissors,
Settings, Play, Pause, Stop, Volume, Mute, Fullscreen, ExitFullscreen, Folder, Link,
Paste, Search, Check, Close, Retry, ArrowUp, ArrowDown, Share, Trash, Music, Film,
Screen, Archive, External, Copy, Clock, Info, Alert, Sparkle, Plus, Minus, More (three
dots; draw with `StrokeThickness="2.8"`), File, OpenFile, Edit, Drive, Shield, Moon,
Package, Power, Tray, Question, ChevronLeft/Right/Down, Waveform, Broom, Keyboard, Bell,
Gauge, SetStart, SetEnd, ZoomIn, ZoomOut (all prefixed `Icon`), plus the window caption
glyphs (`IconCaptionMinimize|Maximize|Restore|Close`). Add new glyphs to
`Theme/Icons.axaml`, never as page resources.

## Mascot

`<c:KeaMascot Size="42" Pose="Idle|Carrying" Background="{DynamicResource AppAccentSubtle}" />`
draws the brand kea (vector, theme-aware, hinted for small sizes). The Carrying pose holds a
download card that pops in only under `Window.motion`; the shell sets it while downloads are
active (`Window.downloading`) and bobs the kea while any work runs (`Window.busy`). Use the
mascot only for the sidebar mark and the first-use empty states (Queue: tile on
`AppAccentSubtle`; Library: no tile, inside its round badge). Never use the kea PNG assets
in page content: they always show the dark tile. The one exception is the Settings About
card, which shows the app icon exactly as Windows shows it. Artwork source: `assets/kea.svg`; regenerate
icons with `pwsh -File assets/GenerateIcons.ps1`.

## Progress

`ProgressBar` (6px rounded; `thin` 4px; `danger`, `muted`) glides between values.
`c:ProgressRing` (`Value` 0–100 or `IsIndeterminate`) for compact activity/ETA.
`c:AnimatedNumberText Classes="count tabular" Value="{Binding Percent}"
Format="{}{0:0}%"` counts smoothly to each new value; it formats into a stack buffer each
frame and replaces its text only when the shown number changes, so it never allocates per
frame.

## Motion

All motion is scoped to `Window.motion`, which `MainWindow` sets only while reduced
motion is off (app setting or Windows preference); colour and opacity fades stay because
they do not move anything.

- `page` (set by the shell on each view): fades up 10px when shown.
- `reveal`: add to any element that appears via `IsVisible` (results, notices, list
  rows): fades up 8px. Do not put `reveal`/`pop` on Buttons (buttons use a
  TransformOperations press scale).
- `pop`: scale-in with a little overshoot (badges, success ticks, unsaved dots).
- Buttons and tiles scale slightly when pressed; nav indicator slides.

Keyframe animations must animate `TranslateTransform.*`, `ScaleTransform.*` or
`RotateTransform.Angle` (Avalonia 12 has no keyframe animator for TransformOperations);
transitions may use `TransformOperationsTransition`. Never combine both on one element.
A style that sets `Transitions` on a Button replaces the press-scale transition, so under
`Window.motion` it must list `TransformOperationsTransition Property="RenderTransform"`
too (see `Button.shell-cancel`). Infinite animations must stop when idle (bind them to a
state that turns off).

## Layout

The shell header is a centred 1040px grid: the page title with Cancel operation on the same
line (always in layout, so nothing moves when work starts), and the subtitle across the full
width below. Every page centres its own 1040px column so cards line up with the title at
any width:

```xml
<ScrollViewer Padding="36,4,36,28" HorizontalScrollBarVisibility="Disabled">
  <Grid>
    <Grid.ColumnDefinitions><ColumnDefinition Width="*" /><ColumnDefinition Width="100000*" MaxWidth="1040" /><ColumnDefinition Width="*" /></Grid.ColumnDefinitions>
    <StackPanel Grid.Column="1" Spacing="18"> … cards … </StackPanel>
  </Grid>
</ScrollViewer>
```

(A plain `StackPanel Spacing="18" MaxWidth="1040"` centres the same way.) Cards are the
unit of grouping; use `Grid ColumnSpacing`/`RowSpacing` and `StackPanel Spacing` rather
than per-element margins. Must not clip at 880px width or 125–200% scaling.

Pinned action bar (Download; use the same pattern for any page whose main action must stay
in reach): the page root is a `Grid RowDefinitions="*,Auto"` with the `ScrollViewer` in row
0 and, in row 1 outside the scroller, a `Border` with `Background="{DynamicResource AppSurface}"`,
`BorderBrush="{DynamicResource AppBorder}"`, `BorderThickness="0,1,0,0"` and
`Padding="36,12"` that contains the same centred three-column grid, so the bar's content
lines up with the page column. A floating bar that should not span the page (the Settings
save bar) uses the same centred grid with a raised `card`-like border instead.

List pages (Library) use a `ListBox` with an `ItemContainerTheme` (no stock hover or
selection fill), a `VirtualizingStackPanel` items panel and
`KeyboardNavigation.TabNavigation="Continue"`, so long lists only build the rows on screen
and the inline row actions stay reachable with Tab.

## Page-local patterns

These are used by one page today. Promote one into `Theme/Controls.axaml` (and this file)
when a second page needs it, and delete the page copy in the same change.

- Download: `Button.soft` (soft accent for an inline secondary step, with a neutral
  `:disabled` look), `Border.row` (picker row that highlights under the pointer),
  `Expander.compact` (a smaller disclosure inside a card), `Border.static-field` (a
  read-only value where a choice does not apply).

## Converters

`Klippi.Desktop.Converters.EnumEqualsConverter` (`xmlns:conv="using:Klippi.Desktop.Converters"`)
binds a `RadioButton` group to one enum property:
`IsChecked="{Binding DownloadMode, Mode=TwoWay, Converter={x:Static conv:EnumEqualsConverter.Instance}, ConverterParameter={x:Static core:DownloadMode.Audio}}"`.
It returns true when the value equals the parameter; writing back returns the parameter
only when the radio becomes checked (the radio losing the selection never overwrites the
new choice). The parameter may be an enum value (preferred) or a member name; one-way
checks may list alternatives (`"H264|Hevc"`). `EnumEqualsConverter.NotEqual` inverts the
one-way result and never writes back. The shared `ChoiceLabel` converter (App resources)
gives enum choices their display labels.

## Implementation notes (verified pitfalls)

- A Button whose `Click` handler closes its own Flyout must defer the close with
  `Dispatcher.UIThread.Post(...)`: Avalonia raises `Click` before it runs `Command`, and a
  button detached immediately loses its command binding (see `LibraryView` Clear history).
- `ListBox` swallows Enter for selection, so a row "open" handler must be added with
  `RoutingStrategies.Tunnel`.
- Bindings are reflection bindings (`AvaloniaUseCompiledBindingsByDefault=false`) and fail
  silently; each page's tests should capture Avalonia binding warnings (see
  `LibraryViewTests.BindingErrorSink`).
- A style setter on a Fluent template part does not always win over the value the template
  gives it (a `Margin` on the toggle switch's `SwitchAreaGrid` had no effect); restyle such
  parts through properties the template leaves unset, and prove it with a layout test.
- View-only presentation state that derives from non-notifying model properties lives in
  `MainViewModel.Presentation.cs`, which re-announces it from the model's own change
  notifications.
