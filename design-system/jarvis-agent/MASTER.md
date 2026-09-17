# Jarvis Agent — WPF design system

Release: 1.0.64. Platform: Windows desktop, existing WPF application. This replaces the generic automatically generated IoT/web recommendation with a product-specific developer-operations design. Page overrides are in `design-system/jarvis-agent/pages/`.

## Method and scope

UI UX Pro Max was read from the local reference checkout, including SKILL.md, WPF search guidance, pro-rules and the complete quick-reference checklist. Applied rules: platform-native controls, semantic states rather than color alone, explicit labels, INotifyDataErrorInfo, accessible names/help, keyboard alternatives, real empty/error/loading/disconnected states, readable contrast, bounded/virtualized lists and no decorative animation. Reference downloads are not shipped or checked in.

The existing Connection center and Tool permissions remain at navigation indices 0/1. Execution limits and Sessions are new indices 2/3. The sidebar remains the only visible navigation; no duplicate tab header surface is introduced.

## Tokens and components

Use existing Segoe UI system typography, not downloadable/bundled fonts. Body 13 DIP; secondary 12 DIP; primary section headings 28 DIP. Wrap explanatory copy. Ellipsize list paths with full text in details/tooltips. Standard spacing 8/12/16/20/24 DIP; finite list viewport and outer vertical scrolling keep details reachable at 870x650.

Semantic brushes are Bg #0D1018, Panel #151923, Text #EEEFF7, Muted #8D95AF, Accent #9474EF, Danger #FFA5B7 and Warning #F5C584. New accent actions use dark OnAccent #0D1018 rather than white: the white/accent pair fails the normal-text contrast threshold. Dynamic resources on the new pages map to Windows system colors when high contrast changes. No full historical-app accessibility certification is implied.

`Themes/SessionControls.xaml` owns reusable body/hint/error/card/input/button/row/focus styles and the numeric-field template. New keyboard controls have a 2-DIP dynamic-color focus adorner. Errors have inline text and a polite live annotation; status meaning is stated in words. Save is disabled for invalid or unchanged drafts; Close uses a confirmation defaulting to No. No icon-only actions or motion effects are required.

## Verification

`tests/test_session_ui_assets.py` checks semantic text contrast, accessible field names, Ctrl+S / F5, focus style and bounded scrollable sessions layout. Its contrast/focus checks were observed failing before the fixes and passing afterwards. `Jarvis.Agent.UiSmoke` renders synthetic empty/populated/filtered/error/narrow layouts, checks binding errors and persistence, and never connects or arms the user's live agent. Release evidence and limitations are recorded in `docs/BUILD-STATUS.md`; actual screen-reader, OS high-contrast and multi-monitor live interaction remain separate manual acceptance.
