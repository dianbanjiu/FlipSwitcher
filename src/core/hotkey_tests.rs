//! Pure-function tests for `decode_hook_event`.
//!
//! The FFI layer (`HotkeyHook`) isn't unit-tested here — it needs a real
//! message pump and the system hook thread. The state machine it drives is
//! the regression-prone part, and it's a pure function over
//! `(keydown, keyup, vk, alt, shift, state)`. Mirrors the event matrix in
//! `legacy/Services/HotkeyService.cs::KeyboardHookCallback` and the contract in
//! `docs/rust-rewrite-design-step4-5.md` §4.5.

use super::*;
use crate::core::win32::HotkeyState;

fn state() -> HotkeyState {
    let s = HotkeyState::new();
    s.set_use_alt_tab(true);
    s.set_visible(true);
    s
}

fn ev(
    is_keydown: bool,
    is_keyup: bool,
    vk: u32,
    alt: bool,
    shift: bool,
    st: &HotkeyState,
) -> Option<(HotkeyEvent, bool)> {
    decode_hook_event(is_keydown, is_keyup, vk, alt, shift, st)
}

#[test]
fn alt_tab_disabled_passes_everything_through() {
    let s = HotkeyState::new();
    s.set_use_alt_tab(false);
    s.set_visible(true);
    assert_eq!(ev(true, false, vk::TAB, true, false, &s), None);
    assert_eq!(ev(true, false, vk::ESCAPE, true, false, &s), None);
}

#[test]
fn escape_when_visible_is_swallowed() {
    let s = state();
    assert_eq!(
        ev(true, false, vk::ESCAPE, false, false, &s),
        Some((HotkeyEvent::EscapePressed, true))
    );
}

#[test]
fn escape_when_settings_open_and_alt_held_even_if_invisible() {
    let s = HotkeyState::new();
    s.set_use_alt_tab(true);
    s.set_visible(false);
    s.set_settings_open(true);
    assert_eq!(
        ev(true, false, vk::ESCAPE, true, false, &s),
        Some((HotkeyEvent::EscapePressed, true))
    );
}

#[test]
fn escape_invisible_without_settings_alt_passes_through() {
    let s = HotkeyState::new();
    s.set_use_alt_tab(true);
    s.set_visible(false);
    // settings open but no Alt → not triggered (mirrors the C# `IsAltPressed` guard).
    s.set_settings_open(true);
    assert_eq!(ev(true, false, vk::ESCAPE, false, false, &s), None);
}

#[test]
fn escape_keyup_is_not_handled() {
    let s = state();
    assert_eq!(ev(false, true, vk::ESCAPE, false, false, &s), None);
}

#[test]
fn alt_release_when_visible_and_not_ignored_passes_through() {
    let s = state();
    assert_eq!(
        ev(false, true, VK_MENU, false, false, &s),
        Some((HotkeyEvent::AltReleased, false))
    );
    // left / right Alt variants also count.
    assert_eq!(
        ev(false, true, VK_LMENU, false, false, &s),
        Some((HotkeyEvent::AltReleased, false))
    );
    assert_eq!(
        ev(false, true, VK_RMENU, false, false, &s),
        Some((HotkeyEvent::AltReleased, false))
    );
}

#[test]
fn alt_release_ignored_or_invisible_passes_through() {
    let s = state();
    s.set_ignore_alt_release(true);
    assert_eq!(ev(false, true, VK_MENU, false, false, &s), None);

    let s2 = HotkeyState::new();
    s2.set_use_alt_tab(true);
    s2.set_visible(false);
    assert_eq!(ev(false, true, VK_MENU, false, false, &s2), None);
}

#[test]
fn alt_release_non_alt_keyup_passes_through() {
    let s = state();
    assert_eq!(ev(false, true, vk::TAB, false, false, &s), None);
}

#[test]
fn tab_when_hidden_presses_hotkey() {
    let s = HotkeyState::new();
    s.set_use_alt_tab(true);
    s.set_visible(false);
    assert_eq!(
        ev(true, false, vk::TAB, true, false, &s),
        Some((HotkeyEvent::HotkeyPressed, true))
    );
}

#[test]
fn tab_when_visible_navigates_next_or_previous_with_shift() {
    let s = state();
    assert_eq!(
        ev(true, false, vk::TAB, true, false, &s),
        Some((HotkeyEvent::NavigationRequested(NavDirection::Next), true))
    );
    assert_eq!(
        ev(true, false, vk::TAB, true, true, &s),
        Some((HotkeyEvent::NavigationRequested(NavDirection::Previous), true))
    );
}

#[test]
fn tab_without_alt_passes_through() {
    let s = state();
    assert_eq!(ev(true, false, vk::TAB, false, false, &s), None);
}

#[test]
fn up_down_navigate_only_when_visible_and_not_search_mode() {
    let s = state();
    assert_eq!(
        ev(true, false, vk::UP, true, false, &s),
        Some((HotkeyEvent::NavigationRequested(NavDirection::Previous), true))
    );
    assert_eq!(
        ev(true, false, vk::DOWN, true, false, &s),
        Some((HotkeyEvent::NavigationRequested(NavDirection::Next), true))
    );

    // search mode → arrows pass through.
    let s2 = state();
    s2.set_search_mode(true);
    assert_eq!(ev(true, false, vk::UP, true, false, &s2), None);
    assert_eq!(ev(true, false, vk::DOWN, true, false, &s2), None);

    // hidden → arrows pass through.
    let s3 = HotkeyState::new();
    s3.set_use_alt_tab(true);
    s3.set_visible(false);
    assert_eq!(ev(true, false, vk::UP, true, false, &s3), None);
}

#[test]
fn left_right_group_ungroup_only_when_visible() {
    let s = state();
    assert_eq!(
        ev(true, false, vk::RIGHT, true, false, &s),
        Some((HotkeyEvent::GroupByProcessRequested, true))
    );
    assert_eq!(
        ev(true, false, vk::LEFT, true, false, &s),
        Some((HotkeyEvent::UngroupFromProcessRequested, true))
    );

    let s2 = HotkeyState::new();
    s2.set_use_alt_tab(true);
    s2.set_visible(false);
    assert_eq!(ev(true, false, vk::RIGHT, true, false, &s2), None);
    assert_eq!(ev(true, false, vk::LEFT, true, false, &s2), None);
}

#[test]
fn visible_shortcuts_w_d_s_comma() {
    let s = state();
    assert_eq!(
        ev(true, false, vk::W, true, false, &s),
        Some((HotkeyEvent::CloseWindowRequested, true))
    );
    assert_eq!(
        ev(true, false, vk::D, true, false, &s),
        Some((HotkeyEvent::StopProcessRequested, true))
    );
    assert_eq!(
        ev(true, false, vk::S, true, false, &s),
        Some((HotkeyEvent::SearchModeRequested, true))
    );
    assert_eq!(
        ev(true, false, vk::OEM_COMMA, true, false, &s),
        Some((HotkeyEvent::SettingsRequested, true))
    );
}

#[test]
fn visible_shortcuts_require_visibility() {
    let s = HotkeyState::new();
    s.set_use_alt_tab(true);
    s.set_visible(false);
    assert_eq!(ev(true, false, vk::W, true, false, &s), None);
    assert_eq!(ev(true, false, vk::D, true, false, &s), None);
    assert_eq!(ev(true, false, vk::S, true, false, &s), None);
    assert_eq!(ev(true, false, vk::OEM_COMMA, true, false, &s), None);
}

#[test]
fn non_keydown_keyup_message_passes_through() {
    let s = state();
    assert_eq!(ev(false, false, vk::TAB, true, false, &s), None);
}

#[test]
fn keydown_without_alt_passes_through_after_escape_check() {
    // Escape is handled without Alt; everything else below the Alt guard
    // requires Alt. A plain keydown (no Alt) of, say, 'A' passes through.
    let s = state();
    assert_eq!(ev(true, false, 0x41, false, false, &s), None);
}

#[test]
fn set_visible_false_clears_search_mode() {
    let s = HotkeyState::new();
    s.set_use_alt_tab(true);
    s.set_visible(true);
    s.set_search_mode(true);
    assert!(s.is_search_mode.load(std::sync::atomic::Ordering::Relaxed));
    s.set_visible(false);
    assert!(
        !s.is_search_mode.load(std::sync::atomic::Ordering::Relaxed),
        "set_visible(false) must clear search mode"
    );
}
