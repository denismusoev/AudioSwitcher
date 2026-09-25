# PS5 Visual Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply the PS5reborn settings visual language to AudioSwitcher without changing its existing behavior or navigation.

**Architecture:** Move shared presentation into focused WPF resource dictionaries, use one static PS5 settings background, and restyle existing controls in place. Preserve current names and event wiring so controller, keyboard, mouse, and focus behavior remain unchanged.

**Tech Stack:** .NET 10, WPF XAML, existing `AudioSwitcher.WindowChecks` executable checks.

**Spec:** `docs/superpowers/specs/2026-09-25-ps5-visual-redesign-design.md`

## Global Constraints

- No Playnite assemblies, markup extensions, controls, plugins, or bindings.
- Preserve all business logic, navigation topology, and controller mappings.
- Use `SettingsMenus.xaml` as the main visual reference.
- Use a static background with no runtime blur or looping animation.
- Keep interaction motion within 120–220 ms and honor disabled Windows animations.
- Do not add PlayStation logos or branded Store, Plus, or trophy assets.

## Review Focus

- Controller focus must remain visually distinct from selection and return after drawers close.
- Large text and 100%, 150%, and 200% DPI must not clip rows, footer hints, or drawers.
- Hidden surfaces must not retain running storyboards or accept focus.
- Mouse hover must not overpower keyboard/controller focus.
- The background and focus effects must avoid continuous software-rendered blur or shadow work.

---

### Task 1: Design-system foundation

**Files:**
- Create: `src/AudioSwitcher/Themes/PS5/Tokens.xaml`
- Create: `src/AudioSwitcher/Themes/PS5/Typography.xaml`
- Create: `src/AudioSwitcher/Themes/PS5/Motion.xaml`
- Create: `src/AudioSwitcher/Assets/PS5/Background.png`
- Modify: `src/AudioSwitcher/App.xaml`
- Modify: `src/AudioSwitcher/AudioSwitcher.csproj`
- Test: `tests/AudioSwitcher.WindowChecks/FixedDesignChecks.cs`

- [ ] Add failing checks for merged PS5 dictionaries, semantic brushes, static background, and absence of `BlurEffect`.
- [ ] Run `dotnet run --project tests/AudioSwitcher.WindowChecks -- --targeted-visual` and confirm the new checks fail.
- [ ] Add the dictionaries and background, merge them from `App.xaml`, and remove duplicated legacy tokens where safe.
- [ ] Run the targeted visual check and Release build.

### Task 2: Settings controls and lists

**Files:**
- Create: `src/AudioSwitcher/Themes/PS5/Controls.xaml`
- Create: `src/AudioSwitcher/Themes/PS5/Lists.xaml`
- Modify: `src/AudioSwitcher/MainWindow.xaml`
- Modify: `src/AudioSwitcher/Controls/ProgramsView.xaml`
- Test: `tests/AudioSwitcher.WindowChecks/FixedDesignChecks.cs`

- [ ] Add failing checks for 3 px settings focus outlines, transparent resting rows, distinct selected/focused states, vector toggle composition, and settings typography.
- [ ] Run the targeted visual check and confirm failure.
- [ ] Apply named PS5 settings styles to categories, action rows, device/program lists, toggle, and controller hints without changing handlers or element names.
- [ ] Run targeted visual, selection-navigation, and DPI checks.

### Task 3: Drawers, dialog, and motion

**Files:**
- Create: `src/AudioSwitcher/Themes/PS5/Overlays.xaml`
- Modify: `src/AudioSwitcher/MainWindow.xaml`
- Modify: `src/AudioSwitcher/Controls/ProgramsView.xaml`
- Modify: `src/AudioSwitcher/Controls/LaunchEntryDialog.xaml`
- Modify only if required for state transitions: `src/AudioSwitcher/MainWindow.xaml.cs`
- Test: `tests/AudioSwitcher.WindowChecks/FixedDesignChecks.cs`

- [ ] Add failing checks for drawer width/surface, background dimming, reusable transition durations, no row scaling, and focus restoration.
- [ ] Run the targeted visual check and confirm failure.
- [ ] Apply the drawer, confirmation, embedded editor, focus-flash, and reduced-motion styles; animate only opacity and transforms.
- [ ] Run targeted visual and selected-fixes checks.

### Task 4: Final visual verification

**Files:**
- Modify only when a verified defect requires it: files from Tasks 1–3.

- [ ] Run `dotnet build src/AudioSwitcher/AudioSwitcher.csproj -c Release`.
- [ ] Run the targeted visual, selection-navigation, selected DPI, and selected-fixes checks once each.
- [ ] Inspect captured control, list, drawer, settings, and editor surfaces for clipping, inconsistent focus, or background artifacts.
- [ ] Confirm the resulting executable exists at `src/AudioSwitcher/bin/Release/net10.0-windows/AudioSwitcher.exe`.
