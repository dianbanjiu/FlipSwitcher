using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlipSwitcher.Services;

namespace FlipSwitcher.Views;

public enum FluentDialogButton
{
    OK,
    OKCancel,
    YesNo,
    YesNoCancel
}

public enum FluentDialogIcon
{
    None,
    Information,
    Warning,
    Error,
    Question
}

public enum FluentDialogResult
{
    None,
    OK,
    Cancel,
    Yes,
    No
}

public partial class FluentDialog : Window
{
    public FluentDialogResult Result { get; private set; } = FluentDialogResult.None;

    public FluentDialog()
    {
        InitializeComponent();
    }

    public static FluentDialogResult Show(
        string message,
        string title,
        FluentDialogButton buttons = FluentDialogButton.OK,
        FluentDialogIcon icon = FluentDialogIcon.Information,
        Window? owner = null)
    {
        var dialog = new FluentDialog();
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.SetIcon(icon);
        dialog.SetButtons(buttons);

        if (owner != null)
        {
            dialog.Owner = owner;
        }
        else if (Application.Current.MainWindow?.IsLoaded == true)
        {
            dialog.Owner = Application.Current.MainWindow;
        }

        dialog.ShowDialog();
        return dialog.Result;
    }

    private void SetIcon(FluentDialogIcon icon)
    {
        string iconData;
        Brush iconBackground;
        Brush iconFill = Brushes.White;

        switch (icon)
        {
            case FluentDialogIcon.Information:
                iconData = "M12 2C6.48 2 2 6.48 2 12C2 17.52 6.48 22 12 22C17.52 22 22 17.52 22 12C22 6.48 17.52 2 12 2ZM13 17H11V11H13V17ZM13 9H11V7H13V9Z";
                iconBackground = (Brush)FindResource("AccentDefaultBrush");
                break;
            case FluentDialogIcon.Warning:
                iconData = "M1 21H23L12 2L1 21ZM13 18H11V16H13V18ZM13 14H11V10H13V14Z";
                iconBackground = new SolidColorBrush(Color.FromRgb(255, 185, 0));
                iconFill = Brushes.Black;
                break;
            case FluentDialogIcon.Error:
                iconData = "M12 2C6.48 2 2 6.48 2 12C2 17.52 6.48 22 12 22C17.52 22 22 17.52 22 12C22 6.48 17.52 2 12 2ZM13 17H11V15H13V17ZM13 13H11V7H13V13Z";
                iconBackground = new SolidColorBrush(Color.FromRgb(196, 43, 28));
                break;
            case FluentDialogIcon.Question:
                iconData = "M12 2C6.48 2 2 6.48 2 12C2 17.52 6.48 22 12 22C17.52 22 22 17.52 22 12C22 6.48 17.52 2 12 2ZM13 19H11V17H13V19ZM15.07 11.25L14.17 12.17C13.45 12.9 13 13.5 13 15H11V14.5C11 13.4 11.45 12.4 12.17 11.67L13.41 10.41C13.78 10.05 14 9.55 14 9C14 7.9 13.1 7 12 7C10.9 7 10 7.9 10 9H8C8 6.79 9.79 5 12 5C14.21 5 16 6.79 16 9C16 9.88 15.64 10.68 15.07 11.25Z";
                iconBackground = (Brush)FindResource("AccentDefaultBrush");
                break;
            default:
                IconBorder.Visibility = Visibility.Collapsed;
                return;
        }

        IconBorder.Background = iconBackground;
        IconPath.Data = Geometry.Parse(iconData);
        IconPath.Fill = iconFill;
    }

    private void SetButtons(FluentDialogButton buttons)
    {
        ButtonPanel.Children.Clear();

        switch (buttons)
        {
            case FluentDialogButton.OK:
                AddButton(LanguageService.GetString("DialogOK"), FluentDialogResult.OK, true);
                break;
            case FluentDialogButton.OKCancel:
                AddButton(LanguageService.GetString("DialogCancel"), FluentDialogResult.Cancel, false);
                AddButton(LanguageService.GetString("DialogOK"), FluentDialogResult.OK, true);
                break;
            case FluentDialogButton.YesNo:
                AddButton(LanguageService.GetString("DialogNo"), FluentDialogResult.No, false);
                AddButton(LanguageService.GetString("DialogYes"), FluentDialogResult.Yes, true);
                break;
            case FluentDialogButton.YesNoCancel:
                AddButton(LanguageService.GetString("DialogCancel"), FluentDialogResult.Cancel, false);
                AddButton(LanguageService.GetString("DialogNo"), FluentDialogResult.No, false);
                AddButton(LanguageService.GetString("DialogYes"), FluentDialogResult.Yes, true);
                break;
        }
    }

    private void AddButton(string content, FluentDialogResult result, bool isAccent)
    {
        var button = new Button
        {
            Content = content,
            MinWidth = 80,
            Margin = new Thickness(8, 0, 0, 0),
            Style = isAccent
                ? (Style)FindResource("FluentAccentButtonStyle")
                : (Style)FindResource("FluentButtonStyle")
        };

        button.Click += (s, e) =>
        {
            Result = result;
            Close();
        };

        ButtonPanel.Children.Add(button);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // ESC acts as Cancel/No or just closes the dialog
            if (ButtonPanel.Children.Count > 1)
            {
                // For YesNo/OKCancel dialogs, use the non-accent button result
                Result = FluentDialogResult.Cancel;
            }
            else
            {
                Result = FluentDialogResult.OK;
            }
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            // Enter acts as the primary (accent) button
            foreach (var child in ButtonPanel.Children)
            {
                if (child is Button btn && btn.Style == FindResource("FluentAccentButtonStyle"))
                {
                    btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    e.Handled = true;
                    break;
                }
            }
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Result = FluentDialogResult.Cancel;
        Close();
    }
}

