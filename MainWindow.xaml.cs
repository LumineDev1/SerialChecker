using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SerialChecker.Models;

namespace SerialChecker
{
    public enum DisplayRowKind
    {
        Header,
        Item,
        Empty,
        Spacer
    }

    /// <summary>
    /// One row of the flattened, virtualized value list. Consecutive rows of a group draw
    /// the slices of a single card (top corners on the first, bottom corners on the last).
    /// </summary>
    public sealed class DisplayRow
    {
        public DisplayRowKind Kind { get; init; }
        public AuditItem? Item { get; init; }
        public string Title { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public string Icon { get; init; } = "";
        public string CategoryName { get; init; } = "";
        public bool ShowOpenLink { get; init; }
        public bool ShowDivider { get; init; }
        public Thickness CardBorder { get; init; }
        public CornerRadius CardCorners { get; init; }
    }

    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        // Full paths: a bare name would also search the app's folder for a look-alike exe.
        private static readonly string PowerShellPath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        private static readonly string ExplorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        private readonly DispatcherTimer _searchDebounce;
        private readonly DispatcherTimer _toastTimer;
        private AuditCategory? _selectedCategory;
        private AuditCategory? _categoryBeforeSearch;
        private bool _overview = true; // "All serials" page; the app opens on it
        private Dictionary<string, DateTime> _baselineTimes = new Dictionary<string, DateTime>();
        private IReadOnlyList<DisplayRow> _rows = Array.Empty<DisplayRow>();
        private string _searchText = "";
        private string _activeQuery = "";
        private int _searchMatchCount;
        private int _searchCategoryCount;
        private DateTime? _lastScan;
        private bool _isBusy;
        private bool _ghostCleanupRunning;
        private Action? _toastAction;

        public ObservableCollection<AuditCategory> Categories { get; } = new ObservableCollection<AuditCategory>();
        public ICollectionView CategoriesView { get; }

        public AuditCategory? SelectedCategory
        {
            get => _selectedCategory;
            set
            {
                if (ReferenceEquals(_selectedCategory, value))
                    return;

                _selectedCategory = value;
                OnPropertyChanged();

                // Null comes from the list being cleared or search/overview taking over; keep the current page.
                if (value == null)
                    return;

                // Picking a category leaves search and the overview and opens that category.
                _overview = false;
                if (IsSearching)
                    ClearSearch();

                RefreshPage(animate: IsLoaded);
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText == value)
                    return;

                _searchText = value ?? "";
                OnPropertyChanged();
                _searchDebounce.Stop();
                _searchDebounce.Start();
            }
        }

        public IReadOnlyList<DisplayRow> Rows
        {
            get => _rows;
            private set
            {
                _rows = value;
                OnPropertyChanged();
            }
        }

        private bool IsSearching => _activeQuery.Length > 0;
        public bool IsOverview => _overview && !IsSearching;

        public string HeaderTitle => IsSearching ? "Search results" : IsOverview ? "All serials" : SelectedCategory?.Name ?? "Serial Checker";
        public string HeaderIcon => IsSearching ? "\uE721" : IsOverview ? "\uE8A9" : CategoryVisuals.Icon(SelectedCategory?.Name);
        public string BreadcrumbGroup => IsSearching ? "Search" : IsOverview ? "Overview" : CategoryVisuals.Group(SelectedCategory?.Name);

        public string HeaderDescription => IsSearching
            ? $"Matches for “{_activeQuery}” in labels, values, and details."
            : IsOverview
                ? "Every value from this scan, grouped by tab."
                : CategoryVisuals.Description(SelectedCategory?.Name);

        public string HeaderCountText
        {
            get
            {
                if (IsSearching)
                {
                    return _searchMatchCount == 1
                        ? $"1 match · {_searchCategoryCount} {(_searchCategoryCount == 1 ? "category" : "categories")}"
                        : $"{_searchMatchCount} matches · {_searchCategoryCount} {(_searchCategoryCount == 1 ? "category" : "categories")}";
                }

                if (IsOverview)
                    return $"{TotalValueCount} values · {Categories.Count} tabs";

                int count = SelectedCategory == null ? 0 : CountValues(SelectedCategory);
                return count == 1 ? "1 value" : $"{count} values";
            }
        }

        public int TotalValueCount => Categories.Sum(CountValues);

        private static int CountValues(AuditCategory category) => category.Items.Count(item => !item.IsSeparator && !item.IsPlaceholder);

        // ── Baseline scope: the overview (and search) saves every tab; a category page saves only itself.

        private AuditCategory? BaselineScope => IsSearching || _overview ? null : SelectedCategory;

        public string SaveBaselineText => BaselineScope == null ? "Save baseline" : "Save tab baseline";

        public string SaveBaselineTooltip => BaselineScope is AuditCategory tab
            ? $"Save a baseline for {tab.Name} only. Other tabs keep their saved values. (Ctrl+S)"
            : "Save a baseline of every tab (Ctrl+S)";

        public bool HasScopeBaseline => BaselineScope is AuditCategory tab
            ? _baselineTimes.ContainsKey(tab.Name)
            : Categories.Count > 0 && Categories.All(c => _baselineTimes.ContainsKey(c.Name));

        public string BaselineStatusText
        {
            get
            {
                if (BaselineScope is AuditCategory tab)
                {
                    return _baselineTimes.TryGetValue(tab.Name, out DateTime saved)
                        ? $"Baseline saved {FormatSaveTime(saved)}"
                        : "No baseline for this tab";
                }

                int savedTabs = Categories.Count(c => _baselineTimes.ContainsKey(c.Name));
                if (savedTabs == 0)
                    return "No baseline saved";

                DateTime latest = Categories
                    .Where(c => _baselineTimes.ContainsKey(c.Name))
                    .Max(c => _baselineTimes[c.Name]);
                return savedTabs == Categories.Count
                    ? $"Baseline saved {FormatSaveTime(latest)}"
                    : $"Baseline: {savedTabs} of {Categories.Count} tabs";
            }
        }

        private static string FormatSaveTime(DateTime time) => time.Date == DateTime.Today
            ? $"today {time:HH:mm}"
            : $"{time:MMM d, HH:mm}";

        public bool ShowGhostCleanup => !IsSearching && SelectedCategory?.Name == "USB Peripherals";

        public string GhostCleanupText
        {
            get
            {
                if (_ghostCleanupRunning)
                    return "Cleaning…";

                int ghosts = SelectedCategory?.Items.Count(IsGhostItem) ?? 0;
                return ghosts > 0 ? $"Clean {ghosts} ghost device{(ghosts == 1 ? "" : "s")}" : "Clean ghost devices";
            }
        }

        public bool IsAdministrator => SystemInfoGatherer.IsAdministrator;
        public string PrivilegeText => IsAdministrator ? "Running as administrator" : "Limited mode · some IDs hidden";

        public string ScanStatusText
        {
            get
            {
                string version = Assembly.GetExecutingAssembly().GetName().Version is Version v
                    ? $"v{v.Major}.{v.Minor}.{v.Build}"
                    : "";
                return _lastScan is DateTime scanned
                    ? $"Scanned {scanned:HH:mm:ss} · {version}"
                    : version;
            }
        }

        public string RescanTooltip => _lastScan is DateTime scanned
            ? $"Scan again (F5) · last scan {scanned:HH:mm:ss}"
            : "Scan again (F5)";

        // ── Colour themes ────────────────────────────────────────

        public sealed record ThemeOption(string Key, string Name, Brush AccentBrush, Brush SurfaceBrush, bool IsCurrent);

        public IReadOnlyList<ThemeOption> ThemeOptions => ThemeManager.Themes
            .Select(theme => new ThemeOption(theme.Key, theme.Name, theme.AccentBrush, theme.SurfaceBrush, theme == ThemeManager.Current))
            .ToList();

        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            OnPropertyChanged(nameof(ThemeOptions));
            ThemePopup.IsOpen = true;
        }

        private void ThemeOption_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string key)
                return;

            ThemeManager.Apply(key);
            ThemePopup.IsOpen = false;
            OnPropertyChanged(nameof(ThemeOptions));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public MainWindow()
        {
            // "--no-elevate" runs in limited mode without the UAC prompt.
            bool skipElevation = Environment.GetCommandLineArgs()
                .Any(arg => arg.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase));
            if (!SystemInfoGatherer.IsAdministrator && !skipElevation && TryRelaunchElevated())
            {
                Environment.Exit(0);
                return;
            }

            CategoriesView = CollectionViewSource.GetDefaultView(Categories);
            CategoriesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AuditCategory.Name), new CategoryGroupConverter()));

            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce.Stop();
                ApplySearch();
            };

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
            _toastTimer.Tick += (_, _) => HideToast();

            WindowEffects.Attach(this);
            InitializeComponent();
            DataContext = this;
            StateChanged += (_, _) =>
            {
                MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
                MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
            };
        }

        private static bool TryRelaunchElevated()
        {
            try
            {
                string? processPath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(processPath))
                    return false;

                Process.Start(new ProcessStartInfo(processPath)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                });
                return true;
            }
            catch (Win32Exception)
            {
                // User declined the UAC prompt. Continue as a standard user.
                return false;
            }
            catch
            {
                return false;
            }
        }

        // ── Scanning ─────────────────────────────────────────────

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _baselineTimes = BaselineStore.LoadSaveTimes();
            await ScanAsync(initial: true);
        }

        private async Task<List<AuditCategory>?> ScanAsync(bool initial = false, string title = "Scanning this PC")
        {
            if (_isBusy)
                return null;

            _isBusy = true;
            ShowLoading(title);
            try
            {
                var progress = new Progress<AuditProgress>(p =>
                {
                    LoadingDetail.Text = $"Read {p.CategoryName} · {p.Completed} of {p.Total}";
                    double target = LoadingTrack.ActualWidth * p.Completed / Math.Max(1, p.Total);
                    LoadingBar.BeginAnimation(WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
                });

                List<AuditCategory> data = await Task.Run(() => SystemInfoGatherer.GetSystemAudit(progress));
                ReplaceCategories(data);
                _lastScan = DateTime.Now;
                OnPropertyChanged(nameof(ScanStatusText));
                OnPropertyChanged(nameof(RescanTooltip));
                return data;
            }
            catch (Exception ex)
            {
                ShowToast(initial ? "Scan failed" : "Rescan failed", ex.Message, ToastKind.Error);
                return null;
            }
            finally
            {
                HideLoading();
                _isBusy = false;
            }
        }

        private void ReplaceCategories(IEnumerable<AuditCategory> data)
        {
            string? selectedName = (_selectedCategory ?? _categoryBeforeSearch)?.Name;

            Categories.Clear();
            foreach (AuditCategory category in data)
                Categories.Add(category);

            AuditCategory? next = Categories.FirstOrDefault(c => c.Name == selectedName) ?? Categories.FirstOrDefault();
            if (IsSearching || _overview)
            {
                // Stay on the results/overview page and show it with the new values.
                _categoryBeforeSearch = _overview ? null : next;
                _selectedCategory = null;
                OnPropertyChanged(nameof(SelectedCategory));
                RefreshPage(animate: false);
                return;
            }

            _selectedCategory = null; // force a refresh even if the same name is reselected
            SelectedCategory = next;
        }

        private void ShowLoading(string title)
        {
            LoadingTitle.Text = title;
            LoadingDetail.Text = "Reading hardware IDs";
            LoadingBar.BeginAnimation(WidthProperty, null);
            LoadingBar.Width = 0;
            LoadingScreen.BeginAnimation(OpacityProperty, null);
            LoadingScreen.Opacity = 1;
            LoadingScreen.Visibility = Visibility.Visible;
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
        }

        private void HideLoading()
        {
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (_, _) =>
            {
                if (_isBusy)
                    return;
                LoadingScreen.Visibility = Visibility.Collapsed;
                SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            };
            LoadingScreen.BeginAnimation(OpacityProperty, fadeOut);
        }

        // ── Page content ─────────────────────────────────────────

        private void ApplySearch()
        {
            bool wasSearching = IsSearching;
            _activeQuery = _searchText.Trim();

            if (IsSearching && !wasSearching)
            {
                // Results span every category, so nothing in the sidebar is selected meanwhile.
                _categoryBeforeSearch = _selectedCategory;
                _selectedCategory = null;
                OnPropertyChanged(nameof(SelectedCategory));
            }
            else if (!IsSearching && wasSearching)
            {
                // Return to wherever search started: the overview or the previous category.
                if (!_overview)
                    _selectedCategory ??= _categoryBeforeSearch ?? Categories.FirstOrDefault();
                _categoryBeforeSearch = null;
                OnPropertyChanged(nameof(SelectedCategory));
            }
            else if (!IsSearching)
            {
                return;
            }

            RefreshPage(animate: true);
        }

        private void ClearSearch()
        {
            _searchDebounce.Stop();
            _searchText = "";
            _activeQuery = "";
            _categoryBeforeSearch = null;
            OnPropertyChanged(nameof(SearchText));
        }

        private void RefreshPage(bool animate)
        {
            Rows = IsSearching
                ? BuildSearchRows(_activeQuery)
                : IsOverview
                    ? BuildOverviewRows()
                    : BuildCategoryRows(SelectedCategory);

            OnPropertyChanged(nameof(IsOverview));
            OnPropertyChanged(nameof(HeaderTitle));
            OnPropertyChanged(nameof(HeaderIcon));
            OnPropertyChanged(nameof(BreadcrumbGroup));
            OnPropertyChanged(nameof(HeaderDescription));
            OnPropertyChanged(nameof(HeaderCountText));
            OnPropertyChanged(nameof(TotalValueCount));
            OnPropertyChanged(nameof(ShowGhostCleanup));
            OnPropertyChanged(nameof(GhostCleanupText));
            RaiseBaselineStatus();

            GetRowsScrollViewer()?.ScrollToTop();
            if (animate)
                AnimatePageIn();
        }

        private void AnimatePageIn()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            RowsList.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            RowsTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        }

        private ScrollViewer? GetRowsScrollViewer()
        {
            RowsList.ApplyTemplate();
            return RowsList.Template?.FindName("RowsScroll", RowsList) as ScrollViewer;
        }

        private static IReadOnlyList<DisplayRow> BuildCategoryRows(AuditCategory? category)
        {
            var rows = new List<DisplayRow>();
            if (category == null)
                return rows;

            AuditItem? placeholder = category.Items.FirstOrDefault(item => item.IsPlaceholder);
            if (placeholder != null && category.Items.All(item => item.IsPlaceholder || item.IsSeparator))
            {
                rows.Add(new DisplayRow
                {
                    Kind = DisplayRowKind.Empty,
                    Icon = CategoryVisuals.Icon(category.Name),
                    Title = placeholder.Value ?? "Nothing found",
                    Subtitle = placeholder.TooltipText ?? ""
                });
                return rows;
            }

            List<List<AuditItem>> blocks = SplitIntoBlocks(category.Items);
            bool titled = blocks.Count > 1 || category.Items.Any(item => item.IsSeparator);
            string noun = CategoryVisuals.BlockNoun(category.Name);
            string icon = CategoryVisuals.Icon(category.Name);

            for (int i = 0; i < blocks.Count; i++)
            {
                if (i > 0)
                    rows.Add(new DisplayRow { Kind = DisplayRowKind.Spacer });

                AppendCard(rows, blocks[i],
                    title: titled ? $"{noun} {i + 1}" : null,
                    subtitle: titled ? GetBlockSubtitle(category.Name, blocks[i]) : "",
                    icon: icon,
                    categoryName: category.Name,
                    showOpenLink: false);
            }

            return rows;
        }

        private void RaiseBaselineStatus()
        {
            OnPropertyChanged(nameof(SaveBaselineText));
            OnPropertyChanged(nameof(SaveBaselineTooltip));
            OnPropertyChanged(nameof(HasScopeBaseline));
            OnPropertyChanged(nameof(BaselineStatusText));
        }

        private IReadOnlyList<DisplayRow> BuildOverviewRows()
        {
            var rows = new List<DisplayRow>();
            foreach (AuditCategory category in Categories)
            {
                var items = category.Items.Where(item => !item.IsSeparator).ToList();
                if (items.Count == 0)
                    continue;

                if (rows.Count > 0)
                    rows.Add(new DisplayRow { Kind = DisplayRowKind.Spacer });

                int count = CountValues(category);
                AppendCard(rows, items,
                    title: category.Name,
                    subtitle: count == 1 ? "1 value" : $"{count} values",
                    icon: CategoryVisuals.Icon(category.Name),
                    categoryName: category.Name,
                    showOpenLink: true);
            }

            return rows;
        }

        private IReadOnlyList<DisplayRow> BuildSearchRows(string query)
        {
            var rows = new List<DisplayRow>();
            int matches = 0;
            int categoriesWithMatches = 0;

            foreach (AuditCategory category in Categories)
            {
                bool categoryMatches = category.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
                var hits = category.Items
                    .Where(item => !item.IsSeparator && !item.IsPlaceholder)
                    .Where(item => categoryMatches || Matches(item, query))
                    .ToList();

                if (hits.Count == 0)
                    continue;

                if (rows.Count > 0)
                    rows.Add(new DisplayRow { Kind = DisplayRowKind.Spacer });

                AppendCard(rows, hits,
                    title: category.Name,
                    subtitle: hits.Count == 1 ? "1 match" : $"{hits.Count} matches",
                    icon: CategoryVisuals.Icon(category.Name),
                    categoryName: category.Name,
                    showOpenLink: true);

                matches += hits.Count;
                categoriesWithMatches++;
            }

            _searchMatchCount = matches;
            _searchCategoryCount = categoriesWithMatches;

            if (rows.Count == 0)
            {
                rows.Add(new DisplayRow
                {
                    Kind = DisplayRowKind.Empty,
                    Icon = "\uE721",
                    Title = "No matches",
                    Subtitle = $"Nothing in this scan contains “{query}”."
                });
            }

            return rows;
        }

        private static bool Matches(AuditItem item, string query)
        {
            return (item.Label?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.Value?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.TooltipText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        private static void AppendCard(List<DisplayRow> rows, IReadOnlyList<AuditItem> items, string? title, string subtitle,
            string icon, string categoryName, bool showOpenLink)
        {
            bool hasHeader = !string.IsNullOrEmpty(title);
            if (hasHeader)
            {
                rows.Add(new DisplayRow
                {
                    Kind = DisplayRowKind.Header,
                    Title = title!,
                    Subtitle = subtitle,
                    Icon = icon,
                    CategoryName = categoryName,
                    ShowOpenLink = showOpenLink
                });
            }

            for (int i = 0; i < items.Count; i++)
            {
                bool first = i == 0;
                bool last = i == items.Count - 1;
                bool roundTop = first && !hasHeader;

                rows.Add(new DisplayRow
                {
                    Kind = DisplayRowKind.Item,
                    Item = items[i],
                    CategoryName = categoryName,
                    ShowDivider = !roundTop,
                    CardBorder = new Thickness(1, roundTop ? 1 : 0, 1, last ? 1 : 0),
                    CardCorners = new CornerRadius(roundTop ? 10 : 0, roundTop ? 10 : 0, last ? 10 : 0, last ? 10 : 0)
                });
            }
        }

        private static List<List<AuditItem>> SplitIntoBlocks(IEnumerable<AuditItem> items)
        {
            var blocks = new List<List<AuditItem>>();
            var current = new List<AuditItem>();
            foreach (AuditItem item in items)
            {
                if (item.IsSeparator)
                {
                    if (current.Count > 0)
                        blocks.Add(current);
                    current = new List<AuditItem>();
                    continue;
                }

                if (!item.IsPlaceholder)
                    current.Add(item);
            }

            if (current.Count > 0)
                blocks.Add(current);
            return blocks;
        }

        private static string GetBlockSubtitle(string categoryName, IReadOnlyList<AuditItem> block)
        {
            string? Find(string label) => block.FirstOrDefault(item => item.Label == label)?.Value?.Trim();

            string? subtitle = categoryName switch
            {
                "Disk Drive Information" => Find("DISK_STORAGE_MODEL"),
                "GPU Information" => Find("GPU Name"),
                "Physical Memory (RAM)" => string.Join(" · ", new[] { Find("Manufacturer"), Find("Capacity") }
                    .Where(part => !string.IsNullOrWhiteSpace(part) && !CategoryVisuals.IsMutedValue(part))),
                _ => null
            };

            return string.IsNullOrWhiteSpace(subtitle) ? "" : subtitle;
        }

        private static bool IsGhostItem(AuditItem item) => item.Label?.Contains("[GHOST]", StringComparison.Ordinal) == true;

        // ── Window chrome ────────────────────────────────────────

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaximizeButton_Click(object sender, RoutedEventArgs e) => WindowEffects.ToggleMaximize(this);

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            if (ctrl && e.Key == Key.F)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && (SearchBox.IsKeyboardFocusWithin || IsSearching))
            {
                _searchDebounce.Stop();
                _searchText = "";
                OnPropertyChanged(nameof(SearchText));
                ApplySearch();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                RefreshButton_Click(sender, e);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.E)
            {
                ExportButton_Click(sender, e);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.S)
            {
                SaveBaseline_Click(sender, e);
                e.Handled = true;
            }
        }

        // ── Actions ──────────────────────────────────────────────

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (await ScanAsync(title: "Scanning again") != null)
                ShowToast("Scan complete", "All values were read again.", ToastKind.Success);
        }

        private void OpenCategory_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string name)
                return;

            AuditCategory? category = Categories.FirstOrDefault(c => c.Name == name);
            if (category == null)
                return;

            ClearSearch();
            _selectedCategory = null;
            SelectedCategory = category;
        }

        private void CopyValue_Click(object sender, RoutedEventArgs e)
        {
            string value = (sender as FrameworkElement)?.Tag as string ?? "";
            try
            {
                Clipboard.SetText(value);
                ShowToast("Copied", value.Length > 80 ? value[..80] + "…" : value, ToastKind.Success);
            }
            catch (Exception ex)
            {
                ShowToast("Copy failed", ex.Message, ToastKind.Error);
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (Categories.Count == 0)
                return;

            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string path = Path.Combine(desktop, "Master_System_Hardware_Audit.txt");

                var sb = new StringBuilder();
                sb.AppendLine("==========================================");
                sb.AppendLine("          SERIAL CHECKER AUDIT          ");
                sb.AppendLine("             Made by Lumine             ");
                sb.AppendLine("==========================================");
                sb.AppendLine($"Generated: {DateTime.Now}");
                sb.AppendLine();

                foreach (AuditCategory category in Categories)
                {
                    sb.AppendLine($"[{category.Name.ToUpper()}]");
                    foreach (AuditItem item in category.Items)
                        sb.AppendLine($"{item.Label,-25}: {item.Value}");
                    sb.AppendLine();
                }

                File.WriteAllText(path, sb.ToString());
                ShowToast("Export saved", "Master_System_Hardware_Audit.txt is on your Desktop.", ToastKind.Success,
                    "Show file", () => Process.Start(new ProcessStartInfo(ExplorerPath, $"/select,\"{path}\"") { UseShellExecute = true }));
            }
            catch (Exception ex)
            {
                ShowToast("Export failed", ex.Message, ToastKind.Error);
            }
        }

        private void SaveBaseline_Click(object sender, RoutedEventArgs e)
        {
            if (Categories.Count == 0)
                return;

            AuditCategory? tab = BaselineScope;
            try
            {
                if (tab == null)
                {
                    BaselineStore.SaveAll(Categories);
                    ShowToast("Baseline saved", $"All {Categories.Count} tabs saved. Run the spoofer, then press Compare.", ToastKind.Success);
                }
                else
                {
                    BaselineStore.SaveCategory(tab, Categories.Select(c => c.Name).ToList());
                    ShowToast($"{tab.Name} baseline saved", "Only this tab was updated. Other tabs keep their saved values.", ToastKind.Success);
                }

                _baselineTimes = BaselineStore.LoadSaveTimes();
                RaiseBaselineStatus();
            }
            catch (Exception ex)
            {
                ShowToast("Baseline failed", ex.Message, ToastKind.Error);
            }
        }

        private void OverviewButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsSearching)
                ClearSearch();

            _overview = true;
            _selectedCategory = null;
            OnPropertyChanged(nameof(SelectedCategory));
            RefreshPage(animate: true);
        }

        private async void CompareButton_Click(object sender, RoutedEventArgs e)
        {
            List<AuditCategory>? saved;
            try
            {
                saved = BaselineStore.Load();
            }
            catch (Exception ex)
            {
                ShowToast("Baseline unreadable", ex.Message, ToastKind.Error);
                return;
            }

            if (saved == null || saved.Count == 0)
            {
                ShowToast("No baseline yet", "Save a baseline first, then compare after your changes.", ToastKind.Info);
                return;
            }

            var oldData = new ObservableCollection<AuditCategory>(saved);
            DateTime savedAt = saved.Max(c => c.SavedAt ?? DateTime.MinValue);

            List<AuditCategory>? current = await ScanAsync(title: "Scanning to compare");
            if (current == null)
                return;

            try
            {
                var window = new ComparisonWindow(oldData, new ObservableCollection<AuditCategory>(current), savedAt)
                {
                    Owner = this
                };
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                ShowToast("Compare failed", ex.Message, ToastKind.Error);
            }
        }

        // ── Ghost device cleanup ─────────────────────────────────

        private async void GhostCleanup_Click(object sender, RoutedEventArgs e)
        {
            if (_ghostCleanupRunning || SelectedCategory is not { Name: "USB Peripherals" } usbCategory)
                return;

            List<AuditItem> ghostDevices = usbCategory.Items.Where(IsGhostItem).ToList();
            if (ghostDevices.Count == 0)
            {
                ShowToast("No ghost devices", "No disconnected USB records were found.", ToastKind.Info);
                return;
            }

            _ghostCleanupRunning = true;
            GhostCleanupBtn.IsEnabled = false;
            OnPropertyChanged(nameof(GhostCleanupText));

            try
            {
                List<string> instanceIds = ghostDevices
                    .Select(ghost => ghost.TooltipText ?? "")
                    .Where(id => id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // The script goes to PowerShell as an encoded command rather than a temp .ps1, which
                // another process could swap between writing it and the elevated run. Batches keep
                // each command line under the Windows length limit.
                foreach (string[] batch in instanceIds.Chunk(80))
                {
                    string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildGhostCleanupScript(batch)));
                    var psi = new ProcessStartInfo
                    {
                        FileName = PowerShellPath,
                        Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}",
                        UseShellExecute = true,
                        Verb = "runas", // UAC prompt when not already elevated
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    using Process? proc = Process.Start(psi);
                    if (proc != null)
                        await proc.WaitForExitAsync();
                }

                AuditCategory updatedUsb = await Task.Run(SystemInfoGatherer.GetUsbPeripheralsCategory);
                int index = Categories.IndexOf(usbCategory);
                if (index >= 0)
                {
                    bool wasShowing = ReferenceEquals(_selectedCategory, usbCategory);
                    Categories[index] = updatedUsb;
                    if (wasShowing)
                    {
                        _selectedCategory = null;
                        SelectedCategory = updatedUsb;
                    }
                }

                int remaining = updatedUsb.Items.Count(IsGhostItem);
                ShowToast("Cleanup finished",
                    $"Requested removal of {ghostDevices.Count} device record(s). {remaining} remain.",
                    ToastKind.Success);
            }
            catch (Exception)
            {
                ShowToast("Cleanup failed", "Approve the administrator prompt to remove these records.", ToastKind.Error);
            }
            finally
            {
                _ghostCleanupRunning = false;
                GhostCleanupBtn.IsEnabled = true;
                OnPropertyChanged(nameof(GhostCleanupText));
            }
        }

        private static string BuildGhostCleanupScript(IEnumerable<string> instanceIds)
        {
            // Single-quoted PowerShell literals: nothing inside an ID is expanded or run.
            string ids = string.Join(", ", instanceIds.Select(id => "'" + id.Replace("'", "''") + "'"));
            return string.Join("\n", new[]
            {
                "$ErrorActionPreference = 'SilentlyContinue'",
                "$sys = [Environment]::SystemDirectory",
                "foreach ($id in @(" + ids + ")) {",
                "    $removed = $false",
                "    try { Get-PnpDevice -InstanceId $id -ErrorAction Stop | Remove-PnpDevice -Confirm:$false -ErrorAction Stop; $removed = $true } catch {}",
                "    if (-not $removed) {",
                "        & (Join-Path $sys 'pnputil.exe') /remove-device $id 2>&1 | Out-Null",
                "        if ($LASTEXITCODE -eq 0) { $removed = $true }",
                "    }",
                "    if (-not $removed) {",
                "        & (Join-Path $sys 'reg.exe') delete ('HKLM\\SYSTEM\\CurrentControlSet\\Enum\\' + $id) /f 2>&1 | Out-Null",
                "    }",
                "}"
            });
        }

        // ── Toast ────────────────────────────────────────────────

        private enum ToastKind
        {
            Success,
            Error,
            Info
        }

        private void ShowToast(string title, string subtitle, ToastKind kind, string? actionLabel = null, Action? action = null)
        {
            (string glyph, string brushKey) = kind switch
            {
                ToastKind.Error => ("\uEA39", "Danger"),
                ToastKind.Info => ("\uE946", "Accent.Text"),
                _ => ("\uE73E", "Status.Pass")
            };

            ToastIcon.Text = glyph;
            ToastIcon.Foreground = (Brush)FindResource(brushKey);
            ToastTitle.Text = title;
            ToastSubtitle.Text = subtitle;
            ToastSubtitle.Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible;

            _toastAction = action;
            ToastActionBtn.Content = actionLabel;
            ToastActionBtn.Visibility = action != null && actionLabel != null ? Visibility.Visible : Visibility.Collapsed;

            ToastCard.Visibility = Visibility.Visible;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ToastCard.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            ToastTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });

            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromSeconds(kind == ToastKind.Error || action != null ? 7 : 3.5);
            _toastTimer.Start();
        }

        private void HideToast()
        {
            _toastTimer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
            fade.Completed += (_, _) =>
            {
                if (ToastCard.Opacity <= 0.01)
                    ToastCard.Visibility = Visibility.Collapsed;
            };
            ToastCard.BeginAnimation(OpacityProperty, fade);
        }

        private void ToastDismiss_Click(object sender, RoutedEventArgs e) => HideToast();

        private void ToastAction_Click(object sender, RoutedEventArgs e)
        {
            Action? action = _toastAction;
            HideToast();
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                ShowToast("Couldn't open", ex.Message, ToastKind.Error);
            }
        }
    }
}
