using System;
using System.Collections.Concurrent;
using System.Text;
using TinyPinyin;

namespace FlipSwitcher.Services;

/// <summary>
/// Pinyin search service for Chinese character conversion and matching.
/// </summary>
/// <remarks>
/// Three layers of cache:
/// <list type="bullet">
///   <item>Per-character pinyin cache — TinyPinyin's per-call lookup is non-trivial; window
///         titles tend to share characters (e.g. "微信", "文件") so this saves a lot of work.</item>
///   <item>Per-string initials cache — keyed by the original text.</item>
///   <item>Per-string full pinyin cache — keyed by the original text.</item>
/// </list>
/// Caches live for the lifetime of the process. Bounded by typical window-title diversity.
/// </remarks>
public class PinyinService
{
    private static PinyinService? _instance;
    public static PinyinService Instance => _instance ??= new PinyinService();

    // Per-character cache. char => pinyin first letter (lowercase) for non-Chinese fallback.
    // We store the leading letter directly — that's what GetPinyinInitials needs.
    private readonly ConcurrentDictionary<char, char> _charInitialCache = new();

    // Per-character cache for the full pinyin syllable of a single Chinese character.
    private readonly ConcurrentDictionary<char, string> _charPinyinCache = new();

    // Per-string caches — keyed by the input text. Values are already lowercase to avoid
    // re-lowercasing on every comparison.
    private readonly ConcurrentDictionary<string, string> _initialsCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _fullPinyinCache = new(StringComparer.Ordinal);

    private PinyinService() { }

    /// <summary>
    /// Get the pinyin initials of <paramref name="text"/>, lowercased. Cached per input string.
    /// </summary>
    public string GetPinyinInitials(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        if (_initialsCache.TryGetValue(text, out var cached))
            return cached;

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (PinyinHelper.IsChinese(c))
            {
                if (!_charInitialCache.TryGetValue(c, out var initial))
                {
                    var pinyin = PinyinHelper.GetPinyin(c);
                    initial = !string.IsNullOrEmpty(pinyin) ? char.ToLowerInvariant(pinyin[0]) : '\0';
                    _charInitialCache[c] = initial;
                }
                if (initial != '\0')
                    sb.Append(initial);
            }
            else
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        var result = sb.ToString();
        _initialsCache[text] = result;
        return result;
    }

    /// <summary>
    /// Get the full pinyin transcription of <paramref name="text"/>, lowercased. Cached per input.
    /// </summary>
    public string GetFullPinyin(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        if (_fullPinyinCache.TryGetValue(text, out var cached))
            return cached;

        var sb = new StringBuilder(text.Length * 4);
        foreach (var c in text)
        {
            if (PinyinHelper.IsChinese(c))
            {
                if (!_charPinyinCache.TryGetValue(c, out var pinyin))
                {
                    pinyin = PinyinHelper.GetPinyin(c)?.ToLowerInvariant() ?? string.Empty;
                    _charPinyinCache[c] = pinyin;
                }
                sb.Append(pinyin);
            }
            else
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        var result = sb.ToString();
        _fullPinyinCache[text] = result;
        return result;
    }

    /// <summary>
    /// Returns true if <paramref name="text"/> matches <paramref name="filter"/> by either initials or full pinyin.
    /// </summary>
    public bool MatchesPinyin(string text, string filter)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(filter))
            return false;

        var lowerFilter = filter.ToLowerInvariant();

        if (GetPinyinInitials(text).Contains(lowerFilter, StringComparison.Ordinal))
            return true;

        if (GetFullPinyin(text).Contains(lowerFilter, StringComparison.Ordinal))
            return true;

        return false;
    }
}
