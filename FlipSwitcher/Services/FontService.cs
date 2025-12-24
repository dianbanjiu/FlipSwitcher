using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FlipSwitcher.Services;

public class FontService
{
    private static FontService? _instance;
    public static FontService Instance => _instance ??= new FontService();

    private const string DefaultFontFamily = "Segoe UI Variable, Segoe UI, sans-serif";
    private const string SegoeUIVariableKey = "SegoeUIVariable";

    private FontService()
    {
    }

    /// <summary>
    /// 获取系统已安装的字体列表
    /// </summary>
    public List<string> GetInstalledFonts()
    {
        return Fonts.SystemFontFamilies
            .OrderBy(f => f.Source)
            .Select(f => f.Source)
            .ToList();
    }

    /// <summary>
    /// 应用字体到应用程序
    /// </summary>
    public void ApplyFont(string fontFamilyName)
    {
        var app = Application.Current;
        if (app == null) return;

        var fontFamily = new FontFamily(
            string.IsNullOrWhiteSpace(fontFamilyName) ? DefaultFontFamily : fontFamilyName);

        var resources = app.Resources;
        if (resources.Contains(SegoeUIVariableKey))
        {
            resources[SegoeUIVariableKey] = fontFamily;
        }
        else
        {
            resources.Add(SegoeUIVariableKey, fontFamily);
        }

        app.Dispatcher.Invoke(() =>
        {
            foreach (Window window in app.Windows)
            {
                if (window?.IsLoaded == true)
                {
                    UpdateWindowFonts(window, fontFamily);
                    window.InvalidateVisual();
                }
            }
        });
    }

    /// <summary>
    /// 递归更新窗口及其所有子控件的字体
    /// </summary>
    private void UpdateWindowFonts(DependencyObject element, FontFamily fontFamily)
    {
        if (element == null) return;

        // 更新 TextBlock 的字体
        if (element is TextBlock textBlock)
        {
            // 直接设置字体，覆盖样式中的设置
            textBlock.FontFamily = fontFamily;
        }
        // 更新 Control 的字体（包括 TextBox, Button 等）
        else if (element is Control control)
        {
            control.FontFamily = fontFamily;
        }

        // 递归处理子元素
        int childrenCount = VisualTreeHelper.GetChildrenCount(element);
        for (int i = 0; i < childrenCount; i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            UpdateWindowFonts(child, fontFamily);
        }
    }
}

