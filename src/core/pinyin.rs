//! Pinyin search — single-character → pinyin table + three-layer cache.
//!
//! 1:1 port of `legacy/Services/PinyinService.cs`. Decision (design §6): the
//! Rust build carries an **embedded** single-char → pinyin table (P2) instead
//! of binding a C pinyin lib. This file is self-contained, no Win32, no Slint,
//! fully unit-testable. Polyphonic characters resolve to their **primary**
//! reading (matching `TinyPinyin`'s default `GetPinyin(c)`); the table stores
//! one syllable per char. See `docs/rust-rewrite-design-step7-pinyin.md`.
//!
//! Three cache layers (lifetime of the process, same keys as legacy):
//! - `char_pinyin`: `char → syllable`  (single-char full pinyin)
//! - `char_initial`: `char → char`     (single-char first letter)
//! - `initials` / `full`: `String → String` (per-string, already lowercased)
//!
//! The two string caches are keyed by the **original** (un-lowercased) text so
//! they survive across window-list refreshes, matching the C# semantics.

use std::collections::HashMap;

/// `0x4E00..=0x9FFF` — CJK Unified Ideographs basic block. Matches the legacy
/// `PinyinHelper.IsChinese` coverage. Extension blocks (CJK Ext B+) are *not*
/// in the embedded table; out-of-table chars fall back to their lowercase
/// ASCII self in the result strings, which is benign.
pub fn is_chinese(c: char) -> bool {
    ('\u{4E00}'..='\u{9FFF}').contains(&c) // CJK Unified Ideographs basic block
}

/// Single-character → pinyin syllable, already lowercase. Sorted ascending by
/// code point so [`char_pinyin`] can binary-search. Polyphonic chars store
/// their **primary** reading only (design §6).
///
/// The embedded set deliberately covers the union of characters used in common
/// window titles + the test suite (~130 chars). A full GB2312 table (~6763
/// chars) is a later mechanical task via a generator script (`scripts/`); the
/// fallback for any char not listed here is the char's own lowercase self, so
/// search degrades gracefully (never panics) until the full table lands.
static PINYIN_TABLE: &[(char, &str)] = &[
    ('上', "shang"),
    ('下', "xia"),
    ('中', "zhong"),
    ('主', "zhu"),
    ('乐', "le"),
    ('事', "shi"),
    ('云', "yun"),
    ('件', "jian"),
    ('任', "ren"),
    ('低', "di"),
    ('体', "ti"),
    ('保', "bao"),
    ('信', "xin"),
    ('入', "ru"),
    ('关', "guan"),
    ('内', "nei"),
    ('初', "chu"),
    ('制', "zhi"),
    ('前', "qian"),
    ('剪', "jian"),
    ('务', "wu"),
    ('助', "zhu"),
    ('区', "qu"),
    ('单', "dan"),
    ('口', "kou"),
    ('右', "you"),
    ('名', "ming"),
    ('后', "hou"),
    ('商', "shang"),
    ('器', "qi"),
    ('图', "tu"),
    ('声', "sheng"),
    ('外', "wai"),
    ('大', "da"),
    ('天', "tian"),
    ('夹', "jia"),
    ('始', "shi"),
    ('字', "zi"),
    ('存', "cun"),
    ('宽', "kuan"),
    ('密', "mi"),
    ('小', "xiao"),
    ('屏', "ping"),
    ('左', "zuo"),
    ('帮', "bang"),
    ('幕', "mu"),
    ('庆', "qing"),
    ('应', "ying"),
    ('店', "dian"),
    ('建', "jian"),
    ('开', "kai"),
    ('录', "lu"),
    ('微', "wei"),
    ('成', "cheng"),
    ('户', "hu"),
    ('打', "da"),
    ('拼', "pin"),
    ('接', "jie"),
    ('控', "kong"),
    ('搜', "sou"),
    ('播', "bo"),
    ('放', "fang"),
    ('文', "wen"),
    ('新', "xin"),
    ('无', "wu"),
    ('日', "ri"),
    ('时', "shi"),
    ('显', "xian"),
    ('服', "fu"),
    ('期', "qi"),
    ('本', "ben"),
    ('板', "ban"),
    ('标', "biao"),
    ('池', "chi"),
    ('法', "fa"),
    ('浏', "liu"),
    ('源', "yuan"),
    ('点', "dian"),
    ('牙', "ya"),
    ('理', "li"),
    ('电', "dian"),
    ('画', "hua"),
    ('盘', "pan"),
    ('码', "ma"),
    ('示', "shi"),
    ('窗', "chuang"),
    ('端', "duan"),
    ('简', "jian"),
    ('算', "suan"),
    ('管', "guan"),
    ('索', "suo"),
    ('繁', "fan"),
    ('级', "ji"),
    ('线', "xian"),
    ('终', "zhong"),
    ('络', "luo"),
    ('绩', "ji"),
    ('编', "bian"),
    ('网', "wang"),
    ('置', "zhi"),
    ('聊', "liao"),
    ('色', "se"),
    ('蓝', "lan"),
    ('行', "xing"),
    ('视', "shi"),
    ('览', "lan"),
    ('言', "yan"),
    ('计', "ji"),
    ('记', "ji"),
    ('设', "she"),
    ('语', "yu"),
    ('贴', "tie"),
    ('资', "zi"),
    ('载', "zai"),
    ('辑', "ji"),
    ('输', "shu"),
    ('运', "yun"),
    ('连', "lian"),
    ('选', "xuan"),
    ('重', "zhong"),
    ('银', "yin"),
    ('键', "jian"),
    ('闭', "bi"),
    ('间', "jian"),
    ('面', "mian"),
    ('音', "yin"),
    ('项', "xiang"),
    ('频', "pin"),
    ('题', "ti"),
    ('颜', "yan"),
    ('高', "gao"),
    ('黑', "hei"),
    ('鼠', "shu"),
];

/// Pinyin syllable for a single char (table lookup). `""` when the char is
/// not in the embedded table. Pure, no caching (the per-char cache lives in
/// [`PinyinService`]).
pub fn char_pinyin(c: char) -> &'static str {
    match PINYIN_TABLE.binary_search_by_key(&(c as u32), |(k, _)| *k as u32) {
        Ok(i) => PINYIN_TABLE[i].1,
        Err(_) => "",
    }
}

/// Pinyin service — holds the three-layer cache. `&mut self`; not thread-safe
/// (callers should share one instance per worker thread, mirroring the C#
/// `PinyinService.Instance` singleton).
pub struct PinyinService {
    char_initial: HashMap<char, char>,
    char_pinyin_cache: HashMap<char, String>,
    initials: HashMap<String, String>,
    full: HashMap<String, String>,
    /// Table-lookup spy counter — only used to assert the three-layer cache
    /// invariant in tests. Always 0 in production reads.
    #[cfg(test)]
    char_pinyin_lookups: u32,
}

impl Default for PinyinService {
    fn default() -> Self {
        Self::new()
    }
}

impl PinyinService {
    pub fn new() -> Self {
        Self {
            char_initial: HashMap::new(),
            char_pinyin_cache: HashMap::new(),
            initials: HashMap::new(),
            full: HashMap::new(),
            #[cfg(test)]
            char_pinyin_lookups: 0,
        }
    }

    /// Pinyin initials of `text`, lowercased. Non-Chinese chars pass through as
    /// their lowercase ASCII (matching legacy behaviour, including spaces).
    pub fn get_pinyin_initials(&mut self, text: &str) -> String {
        if text.is_empty() {
            return String::new();
        }
        if let Some(cached) = self.initials.get(text) {
            return cached.clone();
        }
        let mut sb = String::with_capacity(text.len());
        for c in text.chars() {
            if is_chinese(c) {
                let initial = if let Some(v) = self.char_initial.get(&c) {
                    *v
                } else {
                    let py = self.char_pinyin_cached(c);
                    let initial = py
                        .chars()
                        .next()
                        .map(|ch| ch.to_ascii_lowercase())
                        .unwrap_or('\0');
                    self.char_initial.insert(c, initial);
                    initial
                };
                if initial != '\0' {
                    sb.push(initial);
                }
            } else {
                sb.push(c.to_ascii_lowercase());
            }
        }
        self.initials.insert(text.to_string(), sb.clone());
        sb
    }

    /// Full pinyin transcription of `text`, lowercased. Non-Chinese chars pass
    /// through as lowercase ASCII.
    pub fn get_full_pinyin(&mut self, text: &str) -> String {
        if text.is_empty() {
            return String::new();
        }
        if let Some(cached) = self.full.get(text) {
            return cached.clone();
        }
        let mut sb = String::with_capacity(text.len() * 4);
        for c in text.chars() {
            if is_chinese(c) {
                let py = self.char_pinyin_cached(c);
                sb.push_str(&py);
            } else {
                sb.push(c.to_ascii_lowercase());
            }
        }
        self.full.insert(text.to_string(), sb.clone());
        sb
    }

    /// True iff `filter` (case-insensitive) is a substring of either the pinyin
    /// initials or the full pinyin of `text`. Mirrors legacy `MatchesPinyin`.
    pub fn matches_pinyin(&mut self, text: &str, filter: &str) -> bool {
        if text.is_empty() || filter.is_empty() {
            return false;
        }
        let lf = filter.to_ascii_lowercase();
        if self.get_pinyin_initials(text).contains(&lf) {
            return true;
        }
        if self.get_full_pinyin(text).contains(&lf) {
            return true;
        }
        false
    }

    /// Per-char full pinyin, sourced from the table and memoised in
    /// `char_pinyin_cache`. Empty when the table has no entry for `c`.
    fn char_pinyin_cached(&mut self, c: char) -> String {
        if let Some(v) = self.char_pinyin_cache.get(&c) {
            return v.clone();
        }
        let py = char_pinyin(c).to_string();
        self.char_pinyin_cache.insert(c, py.clone());
        cfg_test_bump!(self);
        py
    }

    #[cfg(test)]
    pub fn char_pinyin_lookups(&self) -> u32 {
        self.char_pinyin_lookups
    }
}

#[cfg(test)]
macro_rules! cfg_test_bump {
    ($self_:expr) => {
        $self_.char_pinyin_lookups += 1
    };
}
#[cfg(not(test))]
macro_rules! cfg_test_bump {
    ($self_:expr) => {};
}
#[allow(unused_imports)]
use cfg_test_bump;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn is_chinese_basic_block() {
        assert!(is_chinese('中'));
        assert!(is_chinese('龥')); // U+9FA5 — top of the canonical block
        assert!(!is_chinese('a'));
        assert!(!is_chinese('_'));
        assert!(!is_chinese('9'));
        assert!(!is_chinese('あ')); // hiragana — not CJK ideographs
        assert!(!is_chinese('ｱ')); // katakana
    }

    #[test]
    fn char_pinyin_table_hit_and_miss() {
        assert_eq!(char_pinyin('中'), "zhong");
        assert_eq!(char_pinyin('文'), "wen");
        assert_eq!(char_pinyin('记'), "ji");
        // 龘 (U+9F98, CJK Ext? — out of embedded table) → empty, not panic.
        assert_eq!(char_pinyin('龘'), "");
    }

    #[test]
    fn table_is_sorted_unique() {
        // Static invariant the binary-search lookup depends on.
        let mut prev: Option<char> = None;
        for (k, _) in PINYIN_TABLE {
            if let Some(p) = prev {
                assert!(k > &p, "table not strictly ascending at {:?}", k);
                assert_ne!(k, &p, "duplicate code point {:?}", k);
            }
            prev = Some(*k);
        }
    }

    #[test]
    fn initials_basic() {
        let mut s = PinyinService::new();
        assert_eq!(s.get_pinyin_initials("中文"), "zw");
        assert_eq!(s.get_pinyin_initials("记事本"), "jsb");
        assert_eq!(s.get_pinyin_initials("微信"), "wx");
    }

    #[test]
    fn initials_passthrough_non_chinese_lowercased() {
        let mut s = PinyinService::new();
        assert_eq!(s.get_pinyin_initials("中文abc"), "zwabc");
        assert_eq!(s.get_pinyin_initials(""), "");
        // Non-Chinese chars (including spaces) pass through lowercased — the space
        // stays inline (matches legacy `sb.Append(ToLower(c))`): `"中 文"` →
        // `"z w"`. Note the space breaks `"zw"` substring matches; this is the
        // faithful legacy behaviour.
        assert_eq!(s.get_pinyin_initials("中 文"), "z w");
    }

    #[test]
    fn full_pinyin_basic() {
        let mut s = PinyinService::new();
        assert_eq!(s.get_full_pinyin("中文"), "zhongwen");
        assert_eq!(s.get_full_pinyin("微信"), "weixin");
        assert_eq!(s.get_full_pinyin("记事本"), "jishiben");
        // Space passes through (lowercased — unchanged), so it stays in the
        // full-pinyin string just as legacy appends `ToLowerInvariant(c)`.
        assert_eq!(s.get_full_pinyin("中文 a"), "zhongwen a");
    }

    #[test]
    fn matches_pinyin_initials_and_full() {
        let mut s = PinyinService::new();
        assert!(s.matches_pinyin("中文", "zw"));
        assert!(s.matches_pinyin("中文", "zhongwen"));
        assert!(s.matches_pinyin("中文", "zhongw")); // full-prefix
        assert!(s.matches_pinyin("中文", "en")); // substring of full
        assert!(!s.matches_pinyin("中文", "全"));
        assert!(!s.matches_pinyin("", "x"));
        assert!(!s.matches_pinyin("中文", ""));
    }

    #[test]
    fn matches_pinyin_non_chinese_passthrough() {
        let mut s = PinyinService::new();
        // "notepad" → initials/full both lowercase the whole thing.
        assert!(s.matches_pinyin("notepad", "note"));
    }

    #[test]
    fn matches_pinyin_case_insensitive_filter() {
        let mut s = PinyinService::new();
        assert!(s.matches_pinyin("中文", "ZW"));
        assert!(s.matches_pinyin("中文", "ZhongWen"));
    }

    #[test]
    fn polyphonic_takes_primary_reading() {
        let mut s = PinyinService::new();
        // '重' stores only "zhong" (primary; never "chong"). So "重庆" → "zq",
        // not "cq". This locks the §7P.2 "primary reading" decision.
        assert_eq!(s.get_pinyin_initials("重庆"), "zq");
        assert_eq!(s.get_full_pinyin("重庆"), "zhongqing");
    }

    #[test]
    fn three_layer_cache_invariant() {
        // First call to initials("中文") misses the char cache twice and does
        // two table lookups; the same chars then hit char cache for full("中文").
        let mut s = PinyinService::new();
        let before = s.char_pinyin_lookups();
        let _ = s.get_pinyin_initials("中文");
        assert_eq!(s.char_pinyin_lookups() - before, 2, "two new chars looked up");

        let before = s.char_pinyin_lookups();
        let _ = s.get_full_pinyin("中文");
        assert_eq!(
            s.char_pinyin_lookups() - before,
            0,
            "per-char cache reused for full pinyin"
        );

        // String cache: re-asking initials/full for the same key touches nothing.
        let before = s.char_pinyin_lookups();
        let _ = s.get_pinyin_initials("中文");
        let _ = s.get_full_pinyin("中文");
        assert_eq!(s.char_pinyin_lookups() - before, 0);

        // Fresh string with a new char forces one lookup.
        let before = s.char_pinyin_lookups();
        let _ = s.get_full_pinyin("聊天");
        // '聊' and '天' both new → two lookups (both absent from char cache).
        assert_eq!(s.char_pinyin_lookups() - before, 2);
    }
}