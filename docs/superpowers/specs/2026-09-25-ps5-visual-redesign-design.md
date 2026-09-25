# PS5 Visual Redesign

## Goal

Adapt the visual language of the Playnite fullscreen theme `PS5reborn_saVantCZ` to AudioSwitcher while preserving the application's existing features, navigation structure, controller mappings, and interaction flows.

The primary reference is the Playnite theme's settings surface, especially `Views/SettingsMenus.xaml`. Game-library views may be consulted for isolated effects, but they do not define the redesign.

The result should feel like the PS5reborn settings interface translated into AudioSwitcher, not like Playnite embedded into the application. AudioSwitcher must not gain any runtime or build-time dependency on Playnite.

## Scope

The redesign covers:

- background and atmospheric layers;
- colors, brushes, typography, spacing, and sizing;
- navigation categories and settings-style rows;
- device, display, running-program, and launch-entry lists;
- toggle, text-input, button, and controller-hint styles;
- focus, hover, pressed, selected, disabled, and error states;
- right-side drawers, confirmation surfaces, and the launch-entry dialog;
- section, focus, toggle, and drawer animations;
- reduced-motion behavior;
- visual performance and DPI behavior.

The redesign does not change:

- business logic;
- navigation topology;
- controller button assignments;
- audio, display, window-transfer, launch, edit, or delete behavior;
- data models;
- Playnite integration, because none will be introduced.

## Source Material

Use these PS5reborn resources as design references:

- `Images/PS5/Background/PS5Background.png` for the application background;
- `Views/SettingsMenus.xaml` for settings categories, rows, typography, spacing, focus borders, and checkbox composition;
- `Constants.xaml` for the source theme's neutral palette and gradient principles;
- `PS5Border` and `PS5Cover` for the focus outline and one-shot focus flash;
- `Images/PS5/CheckBox/CheckOff.png` and `CheckOn.png` as the visual reference for the toggle.

Do not carry over:

- `ButtonEx`, `CheckBoxEx`, `SliderEx`, `FadeImage`, or any other Playnite control;
- `{ThemeFile}`, `{Settings}`, `{PluginSettings}`, or Playnite bindings;
- game covers, trophies, Store, Plus, or PlayStation logos;
- background video, theme sounds, particle loops, or game-library choreography;
- Playnite view models, commands, named template parts, or plugins.

The background image is the only theme bitmap required by the application design. Branded PlayStation assets are excluded.

## Visual Foundation

### Background

Use `PS5Background.png` as a static full-window image with `UniformToFill`. Apply one uniform dark scrim only when needed to guarantee text contrast. Do not use `PS5Games.png`, the existing gold-particle background, a runtime `BlurEffect`, video, animated particles, or cyclic background effects.

The background should remain visible through most content. Opaque surfaces are reserved for drawers, confirmation surfaces, and input areas where contrast requires them.

### Palette

Define semantic tokens rather than hue-named resources. Initial target values are:

| Token | Value | Purpose |
|---|---:|---|
| `Ps5WindowBackground` | `#090C15` | fallback behind the image |
| `Ps5PanelSurface` | `#E612161E` | drawers and confirmation surfaces |
| `Ps5FocusedSurface` | `#24FFFFFF` | focused-row flash and held focus tint |
| `Ps5HoverSurface` | `#12FFFFFF` | pointer hover |
| `Ps5PrimaryText` | `#F5F6F8` | headings and primary labels |
| `Ps5SecondaryText` | `#B9BEC8` | values and supporting text |
| `Ps5Divider` | `#20FFFFFF` | optional list separators |
| `Ps5FocusOutline` | `#F8FAFF` | focus border |
| `Ps5ErrorText` | `#E7AAA6` | errors and destructive choices |

Inactive items do not receive saturated accent fills. Hierarchy comes from brightness, opacity, outline, and type weight.

### Typography

Use `Segoe UI Variable Display` with `Segoe UI` as fallback. The source theme's `Segoe UI Light` character is represented by weights 300–350.

The source theme is designed around 1920×1080. Translate its measurements to AudioSwitcher's 1360×765 baseline at approximately 0.71 scale:

| Role | Source | AudioSwitcher target |
|---|---:|---:|
| screen title | 48 | 34–36 |
| category or setting label | 32 | 22–24 |
| setting value | 22 | 16–18 |
| supporting text | — | 16–17 |
| controller symbol | — | 15–16, semibold |

Text must remain readable from television distance. Long labels use wrapping or trimming deliberately; they must not overflow their allotted region.

### Depth and Shape

Rows are transparent by default. Focus introduces an outline and a brief internal flash instead of turning every row into a card. Corner radii stay between 2 and 6 pixels. Avoid heavy shadows, decorative glass, and nested surfaces.

## Component System

### Settings Categories

Base the left navigation on `SettingsMenuButton`:

- transparent background;
- left-aligned label;
- target height 72–80 pixels;
- width constrained by the existing navigation column;
- primary text for the active category and secondary text for inactive categories;
- no persistent filled card;
- focus displayed by the shared outline and flash treatment.

The active category and the current focus are separate concepts. Active category uses text brightness. Focus uses the visible outline.

Small scale motion may be used on the large category entries only if it does not move neighboring elements. Settings rows do not scale on focus.

### Settings Rows

Audio, display, application, launch-entry, and option rows share one visual vocabulary derived from `SettingsSectionCheckbox`:

- label on the left;
- current value, status, or control on the right;
- transparent default background;
- target height 64–80 pixels depending on content density;
- a 3-pixel focus outline;
- one-shot focus flash;
- optional divider for long lists only.

States are defined independently:

| State | Visual treatment |
|---|---|
| Default | transparent, primary/secondary text |
| Hover | subtle surface tint, no focus flash |
| Focused | 3 px outline, held tint, one-shot flash |
| Pressed | brief opacity reduction; no layout shift |
| Selected | persistent low-opacity tint without focus outline |
| Disabled | 50% opacity |
| Error | error text plus explanation; never color alone |

System state such as “Active”, “Primary”, or “Running” remains textual. Selection never substitutes for focus.

### Toggle

Reproduce the visual form of `CheckOff.png` and `CheckOn.png` with vector WPF elements:

- gray rounded track;
- off state shows the track without a visible thumb;
- on state shows a light circular thumb at the right;
- adjacent “On”/“Off” text remains for unambiguous state communication;
- state transition uses a 160–180 ms fade/slide.

Do not ship the checkbox PNG files; they are references for shape and proportions.

### Text Inputs

Inputs remain minimal and settings-like:

- transparent or panel-matched background;
- visible field label outside the input;
- primary text and caret;
- focus outline consistent with other controls;
- error outline plus readable error text;
- no decorative shadows.

### Controller Hints

Keep the current context-sensitive `A`, `X`, `Y`, menu, and `B` mappings. Present each symbol in a 24–28 pixel circular outline with an 8-pixel gap before the label. Hide unavailable actions rather than dimming meaningless commands.

## Screen Mapping

### Control

“Audio device” and “Primary display” become two settings rows. Their current values appear to the right. Confirmation opens the corresponding right-side drawer.

### Running Applications

Each running application is a settings row with:

- application name as the primary label;
- supporting details below when needed;
- state or marker on the right;
- transfer and close actions represented in the controller-hint footer.

### Launch Entries

Use the same row style as running applications. Present the entry name, path or description, and state marker without introducing a second card language.

### Application Settings

The existing switch-to-application option uses the PS5 settings toggle described above: label and explanation on the left, state text and toggle on the right.

### Drawers and Confirmations

Device selection and delete confirmation remain right-side drawers because that preserves AudioSwitcher's existing interaction model.

- target width: 440–470 pixels;
- `Ps5PanelSurface` background;
- main content dimmed by 45–55%;
- settings-row styling inside the drawer;
- initial focus assigned to a safe meaningful choice;
- focus returned to the invoking element when the drawer closes;
- destructive choices use error text, not a saturated red card.

### Launch Entry Dialog

The separate editor window uses the same background, typography, controls, and focus treatment. It remains a compact child window rather than pretending to be a full-screen settings page.

## Motion System

The source settings view uses a 200 ms delay and a 400 ms fade. AudioSwitcher shortens this choreography to keep repeated utility actions responsive.

| Motion | Duration | Behavior |
|---|---:|---|
| initial content reveal | 180 ms | opacity only, no delay |
| outgoing section | 120 ms | fade plus 12–16 px horizontal translation |
| incoming section | 180 ms | fade plus opposing translation |
| drawer open/close | 200 ms | 24 px horizontal translation plus fade |
| backdrop | 160 ms | opacity |
| focus acquisition | 160 ms | outline/tint; category may scale to 1.015 |
| focus release | 120 ms | return to rest state |
| press feedback | about 140 ms | brief opacity/scale response without layout change |
| toggle | 160–180 ms | thumb fade/slide |
| focus flash | about 220 ms | opacity `0 → 0.18 → 0` |

Animate only `Opacity` and `RenderTransform` for screen and drawer transitions. Do not animate width, height, margin, grid columns, blur radius, or shadow radius. Do not use looping storyboards.

Mouse hover uses tint and outline without the controller-focus flash. This keeps keyboard/controller focus visually dominant.

When Windows client-area animation is disabled, replace movement with an instant transition or a minimal crossfade. Focus outlines, state labels, and all functionality remain available.

## Resource Architecture

Place the design system in focused dictionaries:

```text
src/AudioSwitcher/
├── Assets/PS5/
│   └── Background.png
└── Themes/PS5/
    ├── Tokens.xaml
    ├── Typography.xaml
    ├── Controls.xaml
    ├── Lists.xaml
    ├── Overlays.xaml
    └── Motion.xaml
```

Responsibilities:

- `Tokens.xaml`: semantic colors, brushes, spacing, sizes, and radii;
- `Typography.xaml`: text roles;
- `Controls.xaml`: categories, buttons, inputs, toggles, and hints;
- `Lists.xaml`: shared row and list templates;
- `Overlays.xaml`: drawers, backdrops, and confirmations;
- `Motion.xaml`: durations, easing functions, and reusable storyboards.

`App.xaml` merges these dictionaries and does not remain the primary storage location for large control templates. Use named styles such as `Ps5SettingsCategory`, `Ps5SettingsRow`, `Ps5Toggle`, and `Ps5Drawer`; do not globally replace every WPF control template.

## Performance

- Remove the current runtime blur from the background image.
- Use the prepared bitmap directly and render it once.
- Freeze brushes, geometries, and bitmap resources that do not change.
- Avoid animated blur, large software-rendered shadows, and cyclic storyboards.
- Stop or remove storyboards when a surface is hidden.
- Prefer transforms and opacity so animation does not trigger repeated layout passes.
- Keep branded and unused PS5reborn assets out of the application package.

## Focus and Accessibility

- All application functionality remains usable with a controller.
- Keyboard and mouse retain equivalent functionality.
- Focus is visible over every background region.
- Selected state and focused state are visually distinct.
- Status and error information is never communicated by color alone.
- Closing a drawer or dialog restores focus to the invoking element.
- Text and interactive elements remain readable at television distance.
- The interface remains functional with animation disabled.
- Verify layout at 100%, 150%, and 200% DPI.

## Implementation Sequence

1. Add the resource dictionaries, background, palette, typography, and sizing tokens.
2. Remove runtime background blur and establish the static background stack.
3. Implement category, row, list, toggle, input, and controller-hint styles.
4. Apply the shared styles to Control, Running Applications, Launch Entries, and Application Settings.
5. Restyle drawers and confirmation surfaces; verify focus restoration.
6. Add section, focus, toggle, backdrop, and drawer motion with reduced-motion behavior.
7. Restyle the launch-entry child dialog.
8. Run focused build, layout, navigation, focus, DPI, and visual verification.

## Acceptance Criteria

The redesign is complete when:

- the application is immediately recognizable as an adaptation of the PS5reborn settings interface;
- AudioSwitcher's structure and functionality are unchanged;
- no Playnite dependency or Playnite-specific XAML remains;
- all screens share the same palette, typography, row language, and focus treatment;
- controller navigation reaches every action without a mouse;
- keyboard and mouse remain fully usable;
- active, selected, focused, disabled, and error states are distinct;
- drawers restore focus correctly;
- interaction animations stay within the defined 120–220 ms range;
- disabling animation does not remove feedback or functionality;
- content does not clip at 100%, 150%, or 200% DPI;
- the static background causes no material continuous GPU load;
- no PlayStation logos, Store, Plus, trophies, or other branded assets are added.

## Verification Strategy

Verification remains proportional to the change:

- Release build of `src/AudioSwitcher/AudioSwitcher.csproj`;
- affected static XAML and design checks;
- resource-key validation;
- focused controller-navigation and focus-restoration checks;
- manual smoke testing of Control, Running Applications, Launch Entries, settings, drawers, confirmation, and the launch-entry dialog;
- visual review at representative DPI scales;
- reduced-motion review.

Do not run the full test suite when focused checks cover the affected behavior.
