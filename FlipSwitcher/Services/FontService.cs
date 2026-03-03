using System.Collections.Generic;
using System.Linq;
using System.Windows;
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
    /// 应用字体到应用程序（通过应用级资源，样式继承自动传播）
    /// </summary>
    public void ApplyFont(string fontFamilyName)
    {
        var app = Application.Current;
        if (app == null) return;

        var fontFamily = new FontFamily(
            string.IsNullOrWhiteSpace(fontFamilyName) ? DefaultFontFamily : fontFamilyName);

        app.Dispatcher.Invoke(() =>
        {
            var resources = app.Resources;
            if (resources.Contains(SegoeUIVariableKey))
                resources[SegoeUIVariableKey] = fontFamily;
            else
                resources.Add(SegoeUIVariableKey, fontFamily);

            // 通过设置 Window.FontFamily 触发继承，无需遍历视觉树
            foreach (Window window in app.Windows)
            {
                if (window?.IsLoaded == true)
                    window.FontFamily = fontFamily;
            }
        });
    }
}

