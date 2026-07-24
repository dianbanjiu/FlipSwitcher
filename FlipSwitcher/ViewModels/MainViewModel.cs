using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipSwitcher.Models;
using FlipSwitcher.Services;

namespace FlipSwitcher.ViewModels;

/// <summary>
/// ViewModel for the main window switcher.
/// </summary>
/// <remarks>
/// Performance notes (rev 2):
/// <list type="bullet">
///   <item><c>_windows</c> is now a plain <see cref="List{T}"/> — it is never bound to the UI,
///         only used as the source for filtering. <c>FilteredWindows</c> is the only
///         <see cref="ObservableCollection{T}"/> in this VM.</item>
///   <item><see cref="SearchText"/> setter no longer filters synchronously. Instead it kicks
///         off a <see cref="DispatcherTimer"/> that coalesces rapid key strokes into a single
///         filter pass.</item>
///   <item><see cref="UpdateFilteredWindows"/> reworked: in the common case the order matches
///         the previous list (we just diff in place); otherwise we Clear + AddRange. This
///         avoids the O(n²) Insert/RemoveAt notification storm of the previous diff.</item>
/// </list>
/// </remarks>
public class MainViewModel : ObservableObject, IDisposable
{
    private readonly WindowService _windowService;
    private string _searchText = string.Empty;
    private AppWindow? _selectedWindow;
    private List<AppWindow> _windows = new();
    private ObservableCollection<AppWindow> _filteredWindows = new();
    private bool _isGroupedByProcess;
    private string? _groupedProcessName;
    private AppWindow? _lastSelectedWindowBeforeGrouping;

    // Debounce timer for SearchText. Idle filter passes are cheap on small lists, but
    // typing 5 characters quickly on a list with pinyin enabled used to trigger 5 full
    // pinyin computations — this collapses to one.
    private readonly DispatcherTimer _searchDebounceTimer;
    private const int SearchDebounceMs = 30;

    public bool ShowMonitorInfo => SettingsService.Instance.Settings.ShowMonitorInfo;

    public MainViewModel()
    {
        _windowService = new WindowService();

        SwitchToWindowCommand = new RelayCommand<AppWindow>(SwitchToWindow);
        RefreshWindowsCommand = new RelayCommand(() => RefreshWindows());
        MoveSelectionUpCommand = new RelayCommand(MoveSelectionUp);
        MoveSelectionDownCommand = new RelayCommand(MoveSelectionDown);
        ActivateSelectedCommand = new RelayCommand(ActivateSelected);

        _searchDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(SearchDebounceMs)
        };
        _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;

        SettingsService.Instance.SettingsChanged += OnSettingsChanged;
    }

    private void SearchDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        FilterWindows();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(ShowMonitorInfo));

    public void Dispose()
    {
        SettingsService.Instance.SettingsChanged -= OnSettingsChanged;
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Tick -= SearchDebounceTimer_Tick;
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText != value)
            {
                bool wasActive = IsSearchActive;
                _searchText = value;
                OnPropertyChanged();
                if (wasActive != IsSearchActive)
                    OnPropertyChanged(nameof(IsSearchActive));

                // Coalesce rapid keystrokes into a single filter pass.
                _searchDebounceTimer.Stop();
                _searchDebounceTimer.Start();
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
    /// <summary>
    /// True whenever the list is empty for any reason. The empty-state panel uses this for
    /// visibility, and switches its caption based on <see cref="IsSearchActive"/> so that
    /// "no matches for search" and "no switchable windows at all" stay distinguishable.
    /// (Pre-rev: this used to require <c>!string.IsNullOrEmpty(SearchText)</c>, which left
    /// the middle of the window blank — neither list nor empty-state — when the OS truly had
    /// no enumerable windows. That blank gap looked like a hung loading screen.)
    /// </summary>
    public bool NoWindowsFound => FilteredWindows.Count == 0;
    public bool IsSearchActive => !string.IsNullOrEmpty(SearchText);

    public ICommand SwitchToWindowCommand { get; }
    public ICommand RefreshWindowsCommand { get; }
    public ICommand MoveSelectionUpCommand { get; }
    public ICommand MoveSelectionDownCommand { get; }
    public ICommand ActivateSelectedCommand { get; }

    public event EventHandler? WindowActivated;

    /// <summary>
    /// Update <see cref="_filteredWindows"/> in place with minimal collection-changed notifications.
    /// </summary>
    /// <remarks>
    /// Strategy:
    /// <list type="number">
    ///   <item>Common case: items are mostly the same — perform an index-by-index Replace where
    ///         differences appear. Replace fires a single Replace notification (not Remove+Insert)
    ///         and ListBox handles it efficiently with virtualization.</item>
    ///   <item>If the size differs significantly, fall back to Clear + add-back, which is one
    ///         Reset notification — cheaper than dozens of incremental notifications.</item>
    /// </list>
    /// </remarks>
    private void UpdateFilteredWindows(IReadOnlyList<AppWindow> newList)
    {
        int oldCount = _filteredWindows.Count;
        int newCount = newList.Count;

        // Big delta — Reset is the cheapest single notification.
        if (oldCount == 0 || newCount == 0 || Math.Abs(oldCount - newCount) > newCount / 2 + 4)
        {
            _filteredWindows.Clear();
            for (int i = 0; i < newCount; i++)
                _filteredWindows.Add(newList[i]);
            return;
        }

        // Trim excess.
        while (_filteredWindows.Count > newCount)
            _filteredWindows.RemoveAt(_filteredWindows.Count - 1);

        // Replace mismatches in place.
        int common = Math.Min(_filteredWindows.Count, newCount);
        for (int i = 0; i < common; i++)
        {
            if (!ReferenceEquals(_filteredWindows[i], newList[i]))
                _filteredWindows[i] = newList[i];
        }

        // Append new tail.
        for (int i = _filteredWindows.Count; i < newCount; i++)
            _filteredWindows.Add(newList[i]);
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
    /// Refresh the window list.
    /// </summary>
    /// <param name="selectSecondWindow">If true, select the second window (Alt+Tab behavior).</param>
    public void RefreshWindows(bool selectSecondWindow = false)
    {
        // Cancel any pending debounced filter — we're refreshing now.
        _searchDebounceTimer.Stop();

        _windows = _windowService.GetWindows();
        ApplyRefreshedWindows(selectSecondWindow);
    }

    // Guards against overlapping background enumerations. WindowService reuses mutable scratch
    // buffers and is NOT thread-safe, so only one GetWindows() may run at a time. Rapid
    // open/close toggles could otherwise enter RefreshWindowsAsync concurrently.
    private bool _isRefreshing;

    /// <summary>
    /// Refresh the window list, running the expensive <see cref="WindowService.GetWindows"/>
    /// enumeration on a background thread so the switcher window can paint immediately.
    /// The collection/selection update resumes on the UI thread (no ConfigureAwait(false)).
    /// </summary>
    /// <param name="selectSecondWindow">If true, select the second window (Alt+Tab behavior).</param>
    public async Task RefreshWindowsAsync(bool selectSecondWindow = false)
    {
        if (_isRefreshing)
            return;

        _isRefreshing = true;
        try
        {
            // Cancel any pending debounced filter — we're refreshing now.
            _searchDebounceTimer.Stop();

            // EnumWindows + per-window Win32/DWM calls are the slow part and touch no WPF objects,
            // so they run off the UI thread. The AppWindow instances created here are plain models;
            // their icons still load lazily/async via the Icon getter.
            _windows = await Task.Run(() => _windowService.GetWindows());

            ApplyRefreshedWindows(selectSecondWindow);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <summary>
    /// Apply the freshly enumerated <see cref="_windows"/> to the bound collection and selection.
    /// Must run on the UI thread.
    /// </summary>
    private void ApplyRefreshedWindows(bool selectSecondWindow)
    {
        // If in grouped mode, maintain the grouping state.
        if (_isGroupedByProcess && _groupedProcessName != null)
        {
            var groupedWindows = new List<AppWindow>();
            for (int i = 0; i < _windows.Count; i++)
            {
                if (_windows[i].ProcessName == _groupedProcessName)
                    groupedWindows.Add(_windows[i]);
            }

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
        List<AppWindow> filtered;
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            filtered = new List<AppWindow>(_windows);
        }
        else
        {
            filtered = new List<AppWindow>(_windows.Count);
            for (int i = 0; i < _windows.Count; i++)
            {
                if (_windows[i].MatchesFilter(SearchText))
                    filtered.Add(_windows[i]);
            }
        }

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
    /// Close the selected window and refresh the list.
    /// </summary>
    /// <returns>true if closed successfully, false if the window is elevated and we're not admin.</returns>
    public bool CloseSelectedWindow()
    {
        if (SelectedWindow == null) return true;

        var windowToClose = SelectedWindow;

        // Check if admin privileges are required.
        if (windowToClose.IsElevated && !Services.AdminService.IsRunningAsAdmin())
        {
            return false;
        }

        var currentIndex = FilteredWindows.IndexOf(windowToClose);

        // Close() returns false when it only dismissed an active modal child dialog (e.g. the
        // "Environment Variables" dialog owned by System Properties). The root window is still
        // open in that case, so we must NOT remove it from the list — doing so unconditionally
        // previously desynced the switcher from reality (entry gone, window still on screen).
        bool rootClosed = windowToClose.Close();
        if (!rootClosed)
            return true;

        _windows.Remove(windowToClose);
        FilteredWindows.Remove(windowToClose);
        NotifyWindowCountChanged();
        SelectWindowAfterRemoval(currentIndex);
        return true;
    }

    /// <summary>
    /// Kill the process of the currently selected window.
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
            return;
        }

        // Remove all windows belonging to the killed process.
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            if (_windows[i].ProcessId == targetProcessId)
                _windows.RemoveAt(i);
        }
        for (int i = FilteredWindows.Count - 1; i >= 0; i--)
        {
            if (FilteredWindows[i].ProcessId == targetProcessId)
                FilteredWindows.RemoveAt(i);
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
        // Stop debounce timer — we're clearing synchronously.
        _searchDebounceTimer.Stop();
        if (_searchText != string.Empty)
        {
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(IsSearchActive));
            // Don't call FilterWindows here — RefreshWindows will do it.
        }
    }

    /// <summary>
    /// Group windows by the process of the currently selected window (Right arrow key).
    /// </summary>
    public void GroupByProcess()
    {
        if (SelectedWindow == null || FilteredWindows.Count == 0)
            return;

        _lastSelectedWindowBeforeGrouping = SelectedWindow;
        _groupedProcessName = SelectedWindow.ProcessName;
        _isGroupedByProcess = true;

        var groupedWindows = new List<AppWindow>();
        for (int i = 0; i < FilteredWindows.Count; i++)
        {
            if (FilteredWindows[i].ProcessName == _groupedProcessName)
                groupedWindows.Add(FilteredWindows[i]);
        }

        UpdateFilteredWindows(groupedWindows);
        NotifyWindowCountChanged();

        if (FilteredWindows.Count > 0)
        {
            SelectedWindow = FilteredWindows[0];
        }
    }

    /// <summary>
    /// Return to the full list and navigate to the previously selected process (Left arrow key).
    /// </summary>
    public void UngroupFromProcess()
    {
        if (!_isGroupedByProcess)
            return;

        _isGroupedByProcess = false;
        var processNameToFind = _groupedProcessName;
        _groupedProcessName = null;

        FilterWindows();

        if (processNameToFind != null && FilteredWindows.Count > 0)
        {
            var targetWindow = FilteredWindows.FirstOrDefault(w => w.ProcessName == processNameToFind);

            if (targetWindow != null)
            {
                SelectedWindow = targetWindow;
            }
            else
            {
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
    /// Reset grouping state (called after window activation).
    /// </summary>
    public void ResetGrouping()
    {
        if (_isGroupedByProcess)
        {
            ExitGroupingMode();
        }
    }
}
