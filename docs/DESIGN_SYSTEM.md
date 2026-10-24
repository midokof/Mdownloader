# Design System (NovaDownload)

Last updated: 2026-10-24

## Theme strategy

Two colour dictionaries with identical keys — `Themes/Light.xaml` (default) and `Themes/Dark.xaml`.
`ThemeService.Apply()` replaces `Application.Current.Resources.MergedDictionaries[0]` at runtime, so the
swap is live with no window rebuild. **Every** theme-dependent value in views and styles must use
`{DynamicResource ...}`; `StaticResource` freezes the brush and the control stops following the theme.

Merged dictionary order in `App.xaml` (index matters — index 0 is the swappable theme):

| # | File | Contents |
|---|------|----------|
| 0 | `Themes/Light.xaml` / `Dark.xaml` | colours + brushes (swapped at runtime) |
| 1 | `Themes/Typography.xaml` | `H1Text`, `H2Text`, `BodyText`, `SecondaryText`, `CaptionText`, `MetricText`, `UiFont` |
| 2 | `Themes/Controls.xaml` | all control styles/templates |
| 3 | `Themes/Icons.xaml` | `Geometry` resources, 24×24 Material paths |

## Tokens

| Token | Light | Dark | Use |
|---|---|---|---|
| `BgColor` | `#F4F6FB` | `#0B0E14` | window / page background |
| `SidebarColor` | `#FFFFFF` | `#11151D` | sidebar panel |
| `SurfaceColor` | `#FFFFFF` | `#161B24` | cards, list rows |
| `SurfaceAltColor` | `#F7F9FD` | `#1C2230` | insets, input frames, hovered rows |
| `BorderColor` | `#E6EAF2` | `#232B39` | 1px hairlines |
| `TextPrimaryColor` | `#10131A` | `#E9EEF8` | headings, values |
| `TextSecondaryColor` | `#5B6474` | `#98A3B8` | body, labels |
| `TextMutedColor` | `#98A1B2` | `#647085` | captions, placeholders |
| `PrimaryColor` | `#3B82F6` | `#5B8CFF` | actions, active nav, focus |
| `PrimarySoftColor` | `#E8F0FE` | `#1B2740` | active pill background |
| `AccentColor` | `#14B8A6` | `#2DD4BF` | throughput icon |
| `SuccessColor` / `Soft` | `#22C55E` / `#E7F8EE` | `#34D399` / `#12301F` | completed, seeded progress |
| `WarningColor` / `Soft` | `#F59E0B` / `#FEF3E2` | `#FBBF24` / `#33270A` | paused |
| `ErrorColor` / `Soft` | `#EF4444` / `#FDECEC` | `#F87171` / `#3A1618` | failed, cancel, error strip |

Gradients: `PrimaryGradientBrush` (`#3B82F6 → #6366F1`, brand tile, primary button) and
`ProgressGradientBrush` (`#22C55E → #84CC16`, transfer bars).

## Typography

`Segoe UI Variable Text, Segoe UI` — 26 H1, 15 H2, 13 body, 11.5 caption, 24 metric value.
Numeric readouts (`SizeText`, `SpeedText`, `EtaText`) format through `Formatters`, which reads
`Localizer.Culture` — binary units (`Ko/Mo/Go` in French, `KB/MB/GB` in English) and the culture's
decimal separator (`1,5 Mo` / `1.5 MB`). Time-of-day stamps stay 24-hour and culture-neutral.

## Localization

Two catalogs, no `.resx`: `LocaleCatalog.En` (the fallback) and `LocaleCatalog.Fr` in
`NovaDownload.Core/Localization`, each a plain `Dictionary<string,string>` keyed by
`Area_Name` (`Nav_Settings`, `Status_Downloading`, `Unit_MB`, `Eta_Minutes`, …).

```csharp
Localizer.Apply("Auto");     // "Auto" follows the Windows display language, "en"/"fr" pin one
Localizer.Get("Nav_Settings");            // plain lookup, English fallback, key echo if unknown
Localizer.Format("Dash_Percent", value);  // culture-aware string.Format
Localizer.Plural("Chip_DoneOne", "Chip_Done", count, count);  // 0/1 singular in French, 1 in English
```

`Apply` sets `CultureInfo.DefaultThreadCurrentCulture`/`UiCulture` (and the current thread's) so
numbers, units and dates flip with the language, and raises `LanguageChanged` once. The Windows
culture is captured in `Localizer.OsCulture` before any override, so `"Auto"` stays honest.
Preferences live in `AppSettings.Language` and are applied in `App.OnStartup` **before** the window
is resolved.

Live switching uses a markup extension, not a page rebuild:

```xml
xmlns:loc="clr-namespace:NovaDownload.App.Markup"
<TextBlock Text="{loc:Tr Nav_Settings}"/>
```

`TrExtension` returns a `Binding` to the indexer of `LocalizationProxy` (a singleton
`INotifyPropertyChanged`); on `LanguageChanged` the proxy raises `Item[]` and `""`, which refreshes
every `{loc:Tr}` binding in the tree. View models that compose text (`PageTitle`, `SidebarSummary`,
`Summary`, chips) derive from `LocalizedViewModel` and re-raise their own properties;
`DownloadStore` re-projects item text (`StatusText`, `MetricPrimary`, …) on the dispatcher once per
language change.

Two traps worth remembering:

- **XAML trims attribute whitespace.** `<Run Text=" active"/>` arrives as `"active"`, so
  `"3 active"` renders as `"3active"`. Counted labels are format strings composed in the view model
  (`Chip_Done` = `"{0} terminés"`, `Chip_DoneOne` = `"{0} terminé"`), never a literal glued onto a
  number in XAML.
- **`{loc:Tr}` only works on dependency properties.** It returns a binding, so it cannot target
  `Run.Text` inside a `MultiBinding`-free heredoc or a plain CLR property.

Automation contracts stay language-independent through `AutomationProperties.AutomationId`
(`NavSettings`, `ClearFinished`, `UrlBox`, `StartDownload`, `LanguageFrench`, …). Accessible `Name`
follows the language, so the UI harness addresses elements by id.

## Layout grid

- Shell: 246px sidebar / fluid content, 76px header, 1px `BorderColor` separators.
- Page padding: `26,22,26,26`. Card gap: 16. Row gap: 10. Button padding: `16,8`.
- Cards: `CornerRadius 14`, 1px border, 20px padding, soft shadow (opacity 0.04, blur 18, depth 3).
- Rows: `CornerRadius 14`, `16,14` padding, hover raises border to `PrimaryBrush`.

## Components (Controls.xaml)

| Key | Type | Notes |
|---|---|---|
| `Card` | Border | surface + hairline + shadow |
| `SoftCard` | Border | inset panel, no shadow |
| `PrimaryButton` | Button | gradient + `PrimaryColor` glow (literal `#3B82F6`, never DynamicResource on `DropShadowEffect.Color`) |
| `SecondaryButton` | Button | surface fill, hover border → primary |
| `IconButton` / `DangerIconButton` | Button | 34×34 square, hover tints |
| `WindowButton` | Button | 42×30 caption buttons, close tints red |
| `NavButton` | RadioButton | 11px radius pill; `Tag` carries the icon `Geometry`, bound with a full `Binding` + `RelativeSource TemplatedParent` (TemplateBinding does not convert string → Geometry) |
| `InputBox` | TextBox | **no custom template** — transparent background, no border, `Padding 12,0` |
| `InputFrame` | Border | rounded 42px-high frame; hover and `IsKeyboardFocusWithin` triggers recolour the border |
| `ThinProgress` / `PausedProgress` | ProgressBar | 6px, `PART_Indicator` gradient / amber |
| `PreciseSlider` | Slider | 4px track, 16px ring thumb |
| `ToggleCheck` | CheckBox | 18px rounded box, primary fill when checked |
| `ChoiceChip` | RadioButton | pill for single-choice settings (language); `IsChecked` trigger fills `PrimarySoftBrush` |
| implicit `ScrollBar` | ScrollBar | 8px slim, transparent track |

### Load-bearing pattern — inputs

A custom `ControlTemplate` on `TextBox` **does not render text or caret**: the stock template owns the
`PART_ContentHost` plumbing that hosts `TextBoxView`. The working shape is a frame around a default
`TextBox`:

```xml
<Border Style="{StaticResource InputFrame}">
    <TextBox Style="{StaticResource InputBox}"
             Text="{Binding Url, UpdateSourceTrigger=PropertyChanged}"/>
</Border>
```

Do not reintroduce a templated `TextBox`.

### Load-bearing pattern — navigation

`NavButton` radio buttons carry `CommandParameter` (page key) and a `Checked="Nav_Checked"` handler;
the code-behind dispatches to the view-model command. **Never attach `Command` directly** — WPF
unchecks sibling radios by setting `IsChecked`, `ToggleButton.OnToggle` raises `Click`, and the command
fires for every radio being cleared (this once made the app open *New download* at startup).

## Icons

`Geometry` resources named `IconDashboard`, `IconDownload`, `IconPlus`, `IconHistory`, `IconSettings`,
`IconPause`, `IconPlay`, `IconClose`, `IconFolder`, `IconSearch`, `IconMinimize`, `IconMaximize`,
`IconRestore`, `IconTrash`, `IconCheck`, `IconAlert`, `IconLink`, `IconSpeed`, `IconBolt`, `IconClock`.
Used as `Path Data="{StaticResource IconX}" Width="12–18" Stretch="Uniform"`.

## Status colouring

`StatusToBrushConverter` maps `DownloadStatus` → `{Success,Primary,Warning,Error,TextMuted}Brush`, with
`ConverterParameter="bg"` returning the soft variant for pills and file badges.

## Accessibility

Focus visible on buttons, inputs and nav pills; caption buttons and all icon-only row actions carry
`AutomationProperties.Name` plus tooltips; body text ≥ 11.5px at 125% DPI; contrast ≥ 4.5:1 for
`TextPrimary`/`TextSecondary` on every surface.

Every element the UI harness drives has a stable `AutomationProperties.AutomationId`; the row
icon actions keep a literal English `Name` on purpose, because that is the automation contract and
their user-facing label is the localized `ToolTip`.

