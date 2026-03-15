using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipSwitcher.Models;
using FlipSwitcher.Services;

namespace FlipSwitcher.ViewModels;

/// <summary>
/// ViewModel for the main window switcher
/// </summary>
public class MainViewModel : ObservableObject, IDisposable
{
    private readonly WindowService _windowService;
    private string _searchText = string.Empty;
    private AppWindow? _selectedWindow;
    private ObservableCollection<AppWindow> _windows = new();
    private ObservableCollection<AppWindow> _filteredWindows = new();
    private bool _isGroupedByProcess;
    private string? _groupedProcessName;
    private AppWindow? _lastSelectedWindowBeforeGrouping;

    public bool ShowMonitorInfo => SettingsService.Instance.Settings.ShowMonitorInfo;

    public MainViewModel()
    {
        _windowService = new WindowService();
        
        SwitchToWindowCommand = new RelayCommand<AppWindow>(SwitchToWindow);
        RefreshWindowsCommand = new RelayCommand(() => RefreshWindows());
        MoveSelectionUpCommand = new RelayCommand(MoveSelectionUp);
        MoveSelectionDownCommand = new RelayCommand(MoveSelectionDown);
        ActivateSelectedCommand = new RelayCommand(ActivateSelected);

        SettingsService.Instance.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(ShowMonitorInfo));

    public void Dispose()
    {
        SettingsService.Instance.SettingsChanged -= OnSettingsChanged;
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

    private void UpdateFilteredWindows(System.Collections.Generic.List<AppWindow> newList)
    {
        var newSet = new System.Collections.Generic.HashSet<AppWindow>(newList);

        for (int i = _filteredWindows.Count - 1; i >= 0; i--)
        {
            if (!newSet.Contains(_filteredWindows[i]))
                _filteredWindows.RemoveAt(i);
        }
        for (int i = 0; i < newList.Count; i++)
        {
            if (i >= _filteredWindows.Count)
            {
                _filteredWindows.Add(newList[i]);
            }
            else if (_filteredWindows[i] != newList[i])
            {
                _filteredWindows.Insert(i, newList[i]);
                int oldIdx = i + 1;
                if (oldIdx < _filteredWindows.Count && !newSet.Contains(_filteredWindows[oldIdx]))
                    _filteredWindows.RemoveAt(oldIdx);
            }
        }
        while (_filteredWindows.Count > newList.Count)
            _filteredWindows.RemoveAt(_filteredWindows.Count - 1);
    }

    private void NotifyWindowCountChanged()
    {
        OnPropertyChanged(nameof(WindowCount));
        OnPropertyChanged(nameof(HasWindows));
        OnPropertyChanged(nameof(NoWindowsFound));
    }

    private void ExitGroupingMode()
    {
        _isGroupedByProcess = false;
        _groupedProcessName = null;
        _lastSelectedWindowBeforeGrouping = null;
    }

    private void SelectWindowAfterRemoval(int currentIndex)
    {
        if (FilteredWindows.Count > 0)
        {
            var newIndex = Math.Clamp(currentIndex, 0, FilteredWindows.Count - 1);
            SelectedWindow = FilteredWindows[newIndex];
        }
        else if (_isGroupedByProcess)
        {
            ExitGroupingMode();
            FilterWindows();
            SelectedWindow = FilteredWindows.Count > 0 ? FilteredWindows[0] : null;
        }
        else
        {
            SelectedWindow = null;
        }
    }

    /// <summary>
    /// Refresh the window list
    /// </summary>
    /// <param name="selectSecondWindow">If true, select the second window (Alt+Tab behavior)</param>
    public void RefreshWindows(bool selectSecondWindow = false)
    {
        var windows = _windowService.GetWindows();
        _windows = new ObservableCollection<AppWindow>(windows);

        // If in grouped mode, maintain the grouping state
        if (_isGroupedByProcess && _groupedProcessName != null)
        {
            var groupedWindows = _windows
                .Where(w => w.ProcessName == _groupedProcessName)
                .ToList();

            UpdateFilteredWindows(groupedWindows);
            NotifyWindowCountChanged();

            if (FilteredWindows.Count > 0)
            {
                SelectedWindow = FilteredWindows[0];
            }
            else
            {
                ExitGroupingMode();
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

        UpdateFilteredWindows(filtered);
        NotifyWindowCountChanged();

        if (_filteredWindows.Count > 0)
        {
            var index = selectSecondWindow && _filteredWindows.Count > 1 ? 1 : 0;
            SelectedWindow = _filteredWindows[index];
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
    /// <returns>true if closed successfully, false if the window is elevated and we're not admin</returns>
    public bool CloseSelectedWindow()
    {
        if (SelectedWindow == null) return true;

        var windowToClose = SelectedWindow;

        // Check if admin privileges are required
        if (windowToClose.IsElevated && !Services.AdminService.IsRunningAsAdmin())
        {
            return false;
        }

        var currentIndex = FilteredWindows.IndexOf(windowToClose);

        // Close the window
        windowToClose.Close();

        _windows.Remove(windowToClose);
        FilteredWindows.Remove(windowToClose);
        NotifyWindowCountChanged();
        SelectWindowAfterRemoval(currentIndex);
        return true;
    }

    /// <summary>
    /// Kill the process of the currently selected window
    /// </summary>
    public void StopSelectedProcess()
    {
        if (SelectedWindow == null) return;

        var targetProcessId = SelectedWindow.ProcessId;
        var currentIndex = FilteredWindows.IndexOf(SelectedWindow);

        try
        {
            using var process = Process.GetProcessById((int)targetProcessId);
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Return immediately if the process cannot be terminated
            return;
        }

        var windowsToRemove = _windows
            .Where(w => w.ProcessId == targetProcessId)
            .ToList();

        foreach (var window in windowsToRemove)
        {
            _windows.Remove(window);
            FilteredWindows.Remove(window);
        }

        NotifyWindowCountChanged();
        SelectWindowAfterRemoval(currentIndex);
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
    /// Group windows by the process of the currently selected window (Right arrow key)
    /// </summary>
    public void GroupByProcess()
    {
        if (SelectedWindow == null || FilteredWindows.Count == 0)
            return;

        // Save current state
        _lastSelectedWindowBeforeGrouping = SelectedWindow;
        _groupedProcessName = SelectedWindow.ProcessName;
        _isGroupedByProcess = true;

        // Filter windows belonging to the same process
        var groupedWindows = FilteredWindows
            .Where(w => w.ProcessName == _groupedProcessName)
            .ToList();

        UpdateFilteredWindows(groupedWindows);
        NotifyWindowCountChanged();

        if (FilteredWindows.Count > 0)
        {
            SelectedWindow = FilteredWindows[0];
        }
    }

    /// <summary>
    /// Return to the full list and navigate to the previously selected process (Left arrow key)
    /// </summary>
    public void UngroupFromProcess()
    {
        if (!_isGroupedByProcess)
            return;

        _isGroupedByProcess = false;
        var processNameToFind = _groupedProcessName;
        _groupedProcessName = null;

        // Re-filter windows (restore full list)
        FilterWindows();

        // Try to navigate to the previously selected process
        if (processNameToFind != null && FilteredWindows.Count > 0)
        {
            // Find the first window matching that process
            var targetWindow = FilteredWindows.FirstOrDefault(w => w.ProcessName == processNameToFind);
            
            if (targetWindow != null)
            {
                SelectedWindow = targetWindow;
            }
            else
            {
                // If all windows of that process are closed, select the first window
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
    /// Reset grouping state (called after window activation)
    /// </summary>
    public void ResetGrouping()
    {
        if (_isGroupedByProcess)
        {
            ExitGroupingMode();
        }
    }

}

