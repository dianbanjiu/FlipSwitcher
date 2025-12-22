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

    private FontService()
    {
    }

    /// <summary>
    /// 获取系统已安装的字体列表
    /// </summary>
    public List<string> GetInstalledFonts()
    {
        var fonts = new List<string>();
        var fontFamilies = Fonts.SystemFontFamilies.OrderBy(f => f.Source);

        foreach (var fontFamily in fontFamilies)
        {
            fonts.Add(fontFamily.Source);
        }

        return fonts;
    }

    /// <summary>
    /// 应用字体到应用程序
    /// </summary>
    public void ApplyFont(string fontFamilyName)
    {
        var app = Application.Current;
        if (app == null) return;

        var resources = app.Resources;
        
        // 如果字体名称为空，使用默认字体
        if (string.IsNullOrWhiteSpace(fontFamilyName))
        {
            fontFamilyName = "Segoe UI Variable, Segoe UI, sans-serif";
        }

        // 更新字体资源
        var fontFamily = new FontFamily(fontFamilyName);
        
        // 在应用级别资源中覆盖字体资源（优先级高于 MergedDictionaries）
        if (resources.Contains("SegoeUIVariable"))
        {
            resources["SegoeUIVariable"] = fontFamily;
        }
        else
        {
            resources.Add("SegoeUIVariable", fontFamily);
        }

        // 由于 StaticResource 不会自动更新，需要遍历所有控件强制更新字体
        // 使用 Dispatcher 确保在 UI 线程上执行
        app.Dispatcher.Invoke(() =>
        {
            foreach (Window window in app.Windows)
            {
                if (window != null && window.IsLoaded)
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

