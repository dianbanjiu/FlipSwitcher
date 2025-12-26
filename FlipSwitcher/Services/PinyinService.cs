using System;
using System.Text;
using TinyPinyin;

namespace FlipSwitcher.Services;

/// <summary>
/// Pinyin search service for Chinese character conversion and matching
/// </summary>
public class PinyinService
{
    private static PinyinService? _instance;
    public static PinyinService Instance => _instance ??= new PinyinService();

    private PinyinService() { }

    /// <summary>
    /// Get pinyin initials of a string
    /// </summary>
    public string GetPinyinInitials(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (PinyinHelper.IsChinese(c))
            {
                var pinyin = PinyinHelper.GetPinyin(c);
                if (!string.IsNullOrEmpty(pinyin))
                    sb.Append(pinyin[0]);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Get full pinyin of a string
    /// </summary>
    public string GetFullPinyin(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return PinyinHelper.GetPinyin(text, string.Empty);
    }

    /// <summary>
    /// Check if text matches pinyin search
    /// </summary>
    public bool MatchesPinyin(string text, string filter)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(filter))
            return false;

        var lowerFilter = filter.ToLowerInvariant();

        // 1. 首字母匹配
        var initials = GetPinyinInitials(text).ToLowerInvariant();
        if (initials.Contains(lowerFilter))
            return true;

        // 2. 完整拼音匹配
        var fullPinyin = GetFullPinyin(text).ToLowerInvariant();
        if (fullPinyin.Contains(lowerFilter))
            return true;

        return false;
    }
}

