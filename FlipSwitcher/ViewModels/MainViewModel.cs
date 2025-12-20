using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FlipSwitcher.Models;
using FlipSwitcher.Services;

namespace FlipSwitcher.ViewModels;

/// <summary>
/// ViewModel for the main window switcher
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    private readonly WindowService _windowService;
    private string _searchText = string.Empty;
    private AppWindow? _selectedWindow;
    private ObservableCollection<AppWindow> _windows = new();
    private ObservableCollection<AppWindow> _filteredWindows = new();
    private bool _isGroupedByProcess;
    private string? _groupedProcessName;
    private AppWindow? _lastSelectedWindowBeforeGrouping;

    public MainViewModel()
    {
        _windowService = new WindowService();
        
        SwitchToWindowCommand = new RelayCommand<AppWindow>(SwitchToWindow);
        RefreshWindowsCommand = new RelayCommand(() => RefreshWindows());
        MoveSelectionUpCommand = new RelayCommand(MoveSelectionUp);
        MoveSelectionDownCommand = new RelayCommand(MoveSelectionDown);
        ActivateSelectedCommand = new RelayCommand(ActivateSelected);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText != value)
            {
                _searchText = value;
                OnPropertyChanged();
                FilterWindows();
            }
        }
    }

    public AppWindow? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (_selectedWindow != value)
            {
                if (_selectedWindow != null)
                    _selectedWindow.IsSelected = false;
                
                _selectedWindow = value;
                
                if (_selectedWindow != null)
                    _selectedWindow.IsSelected = true;
                
                OnPropertyChanged();
            }
        }
    }

    public ObservableCollection<AppWindow> FilteredWindows
    {
        get => _filteredWindows;
        set
        {
            _filteredWindows = value;
            OnPropertyChanged();
        }
    }

    public int WindowCount => FilteredWindows.Count;
    public bool HasWindows => FilteredWindows.Count > 0;
    public bool NoWindowsFound => FilteredWindows.Count == 0 && !string.IsNullOrEmpty(SearchText);

    public ICommand SwitchToWindowCommand { get; }
    public ICommand RefreshWindowsCommand { get; }
    public ICommand MoveSelectionUpCommand { get; }
    public ICommand MoveSelectionDownCommand { get; }
    public ICommand ActivateSelectedCommand { get; }

    public event EventHandler? WindowActivated;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Refresh the window list
    /// </summary>
    /// <param name="selectSecondWindow">If true, select the second window (Alt+Tab behavior)</param>
    public void RefreshWindows(bool selectSecondWindow = false)
    {
        _windows.Clear();
        var windows = _windowService.GetWindows();
        
        foreach (var window in windows)
        {
            _windows.Add(window);
        }

        // 如果处于分组模式，保持分组状态
        if (_isGroupedByProcess && _groupedProcessName != null)
        {
            var groupedWindows = _windows
                .Where(w => w.ProcessName == _groupedProcessName)
                .ToList();
            
            FilteredWindows = new ObservableCollection<AppWindow>(groupedWindows);
            
            OnPropertyChanged(nameof(WindowCount));
            OnPropertyChanged(nameof(HasWindows));
            OnPropertyChanged(nameof(NoWindowsFound));

            if (FilteredWindows.Count > 0)
            {
                SelectedWindow = FilteredWindows[0];
            }
            else
            {
                // 如果分组后没有窗口，退出分组模式
                _isGroupedByProcess = false;
                _groupedProcessName = null;
                FilterWindows(selectSecondWindow);
            }
        }
        else
        {
            FilterWindows(selectSecondWindow);
        }
    }

    private void FilterWindows()
    {
        FilterWindows(selectSecondWindow: false);
    }

    private void FilterWindows(bool selectSecondWindow)
    {
        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? _windows.ToList()
            : _windows.Where(w => w.MatchesFilter(SearchText)).ToList();

        FilteredWindows = new ObservableCollection<AppWindow>(filtered);
        
        OnPropertyChanged(nameof(WindowCount));
        OnPropertyChanged(nameof(HasWindows));
        OnPropertyChanged(nameof(NoWindowsFound));

        // Select window
        if (FilteredWindows.Count > 0)
        {
            // When opening fresh (selectSecondWindow=true), select the second window 
            // (index 1) because the first one is usually the current active window.
            // This matches standard Alt+Tab behavior.
            if (selectSecondWindow && FilteredWindows.Count > 1)
            {
                SelectedWindow = FilteredWindows[1];
            }
            else
            {
                SelectedWindow = FilteredWindows[0];
            }
        }
        else
        {
            SelectedWindow = null;
        }
    }

    public void MoveSelectionUp()
    {
        if (FilteredWindows.Count == 0) return;

        var currentIndex = SelectedWindow != null 
            ? FilteredWindows.IndexOf(SelectedWindow) 
            : 0;

        var newIndex = currentIndex > 0 ? currentIndex - 1 : FilteredWindows.Count - 1;
        SelectedWindow = FilteredWindows[newIndex];
    }

    public void MoveSelectionDown()
    {
        if (FilteredWindows.Count == 0) return;

        var currentIndex = SelectedWindow != null 
            ? FilteredWindows.IndexOf(SelectedWindow) 
            : -1;

        var newIndex = currentIndex < FilteredWindows.Count - 1 ? currentIndex + 1 : 0;
        SelectedWindow = FilteredWindows[newIndex];
    }

    public void ActivateSelected()
    {
        if (SelectedWindow != null)
        {
            SwitchToWindow(SelectedWindow);
        }
    }

    /// <summary>
    /// Close the selected window and refresh the list
    /// </summary>
    public void CloseSelectedWindow()
    {
        if (SelectedWindow == null) return;

        var windowToClose = SelectedWindow;
        var currentIndex = FilteredWindows.IndexOf(windowToClose);

        // Close the window
        windowToClose.Close();

        // Remove from collections
        _windows.Remove(windowToClose);
        FilteredWindows.Remove(windowToClose);

        // Update counts
        OnPropertyChanged(nameof(WindowCount));
        OnPropertyChanged(nameof(HasWindows));
        OnPropertyChanged(nameof(NoWindowsFound));

        // Select next window (or previous if we were at the end)
        if (FilteredWindows.Count > 0)
        {
            // Ensure index is within valid bounds (IndexOf returns -1 if not found)
            var newIndex = Math.Clamp(currentIndex, 0, FilteredWindows.Count - 1);
            SelectedWindow = FilteredWindows[newIndex];
        }
        else
        {
            // 如果分组模式下没有窗口了，退出分组模式并刷新列表
            if (_isGroupedByProcess)
            {
                _isGroupedByProcess = false;
                _groupedProcessName = null;
                FilterWindows();
                if (FilteredWindows.Count > 0)
                {
                    SelectedWindow = FilteredWindows[0];
                }
                else
                {
                    SelectedWindow = null;
                }
            }
            else
            {
                SelectedWindow = null;
            }
        }
    }

    private void SwitchToWindow(AppWindow? window)
    {
        if (window == null) return;

        window.Activate();
        WindowActivated?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSearch()
    {
        SearchText = string.Empty;
    }

    /// <summary>
    /// 按当前选中窗口的程序分组显示（右方向键）
    /// </summary>
    public void GroupByProcess()
    {
        if (SelectedWindow == null || FilteredWindows.Count == 0)
            return;

        // 保存当前状态
        _lastSelectedWindowBeforeGrouping = SelectedWindow;
        _groupedProcessName = SelectedWindow.ProcessName;
        _isGroupedByProcess = true;

        // 过滤出同一程序的窗口
        var groupedWindows = FilteredWindows
            .Where(w => w.ProcessName == _groupedProcessName)
            .ToList();

        FilteredWindows = new ObservableCollection<AppWindow>(groupedWindows);
        
        OnPropertyChanged(nameof(WindowCount));
        OnPropertyChanged(nameof(HasWindows));
        OnPropertyChanged(nameof(NoWindowsFound));

        // 选中第一个窗口
        if (FilteredWindows.Count > 0)
        {
            SelectedWindow = FilteredWindows[0];
        }
    }

    /// <summary>
    /// 返回总列表并定位到之前选中的程序（左方向键）
    /// </summary>
    public void UngroupFromProcess()
    {
        if (!_isGroupedByProcess)
            return;

        _isGroupedByProcess = false;
        var processNameToFind = _groupedProcessName;
        _groupedProcessName = null;

        // 重新过滤窗口（恢复总列表）
        FilterWindows();

        // 尝试定位到之前选中的程序
        if (processNameToFind != null && FilteredWindows.Count > 0)
        {
            // 查找第一个匹配该程序的窗口
            var targetWindow = FilteredWindows.FirstOrDefault(w => w.ProcessName == processNameToFind);
            
            if (targetWindow != null)
            {
                SelectedWindow = targetWindow;
            }
            else
            {
                // 如果程序的所有窗口都已关闭，定位到第一个窗口
                SelectedWindow = FilteredWindows[0];
            }
        }
        else if (FilteredWindows.Count > 0)
        {
            SelectedWindow = FilteredWindows[0];
        }

        _lastSelectedWindowBeforeGrouping = null;
    }

    /// <summary>
    /// 重置分组状态（窗口激活后调用）
    /// </summary>
    public void ResetGrouping()
    {
        if (_isGroupedByProcess)
        {
            _isGroupedByProcess = false;
            _groupedProcessName = null;
            _lastSelectedWindowBeforeGrouping = null;
        }
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

