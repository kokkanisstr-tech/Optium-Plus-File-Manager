using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text;
using OptiumPlusFileManager.Api;
using OptiumPlusFileManager.Application;
using OptiumPlusFileManager.Domain;
using OptiumPlusFileManager.Infrastructure;
using OptiumPlusFileManager.Worker;

namespace OptiumPlusFileManager;

internal sealed class MainForm : Form
{
    private readonly FileSorterRuntime _runtime;
    private readonly LocalApiServer _apiServer;
    private readonly ApiTokenStore _apiTokens;
    private readonly CancellationTokenSource _startupCancellation = new();
    private readonly bool _startMinimized;
    private readonly Panel _pageHost = new();
    private readonly Dictionary<string, Control> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _navigationButtons = new(StringComparer.Ordinal);
    private readonly Label _status = new();
    private readonly TextBox _globalSearch = new();
    private readonly Button _globalSearchButton = new();
    private readonly Label _apiStatus = new();
    private readonly Label _apiAddress = new();
    private readonly TextBox _readToken = new();
    private readonly TextBox _writeToken = new();
    private readonly Button _apiToggle = new();
    private readonly Button _sortNow = new();
    private readonly CheckBox _automatic = new();
    private readonly CheckBox _openDestinationAfterSort = new();
    private readonly CheckBox _recursive = new();
    private readonly CheckBox _startWithWindows = new();
    private readonly CheckBox _rememberFileNameRule = new();
    private readonly ListBox _folderList = new();
    private readonly DataGridView _recentGrid = new();
    private readonly DataGridView _reviewGrid = new();
    private readonly DataGridView _historyGrid = new();
    private readonly DataGridView _searchGrid = new();
    private readonly ComboBox _reviewCategory = new();
    private readonly ComboBox _historyStatus = new();
    private readonly ComboBox _historyCategory = new();
    private readonly ComboBox _historySource = new();
    private readonly ComboBox _historyDate = new();
    private readonly ComboBox _providerMode = new();
    private readonly NumericUpDown _maxFileSize = new();
    private readonly NumericUpDown _monthlyAiRequestLimit = new();
    private readonly Label _monthlyAiUsage = new();
    private readonly TextBox _openAiModel = new();
    private readonly TextBox _geminiModel = new();
    private readonly TextBox _claudeModel = new();
    private readonly Label _openAiHealth = new();
    private readonly Label _geminiHealth = new();
    private readonly Label _claudeHealth = new();
    private readonly TextBox _searchQuery = new();
    private readonly Button _searchButton = new();
    private readonly Button _cancelSearchButton = new();
    private readonly Label _searchStatus = new();
    private readonly TreeView _sortedTree = new();
    private readonly Dictionary<string, Button> _sortedShortcutButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TreeNode> _sortedTreeNodesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Image> _sortedShortcutImages = [];
    private readonly Label _sortedTreeStatus = new();
    private readonly CheckBox _darkTheme = new();
    private readonly NotifyIcon _trayIcon = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 5000 };
    private readonly Dictionary<string, Label> _metrics = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openedSortedFolders = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<FileRecord> _reviewItems = [];
    private IReadOnlyList<FileRecord> _historyItems = [];
    private IReadOnlyList<FileSearchHit> _searchItems = [];
    private CancellationTokenSource? _activeSearch;
    private bool _initializing;
    private bool _refreshing;
    private bool _allowClose;
    private DateTime _lastSortedTreeRefreshUtc = DateTime.MinValue;
    private ImageList? _sortedTreeImages;
    private readonly List<Image> _sortedTreeImageSources = [];
    private Task? _startupTask;

    public MainForm(FileSorterRuntime runtime, bool startMinimized = false)
    {
        _runtime = runtime;
        _startMinimized = startMinimized;
        _apiTokens = new ApiTokenStore();
        _apiServer = new LocalApiServer(runtime, _apiTokens);

        Text = "Optium+ File Manager";
        StartPosition = FormStartPosition.CenterScreen;
        var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        MinimumSize = new Size(Math.Min(1040, workArea.Width), Math.Min(700, workArea.Height));
        Size = new Size(Math.Min(1320, workArea.Width), Math.Min(860, workArea.Height));
        BackColor = Color.FromArgb(246, 248, 251);
        Font = new Font("Segoe UI", 9.5f);
        KeyPreview = true;
        KeyDown += HandleGlobalShortcut;
        AllowDrop = true;
        DragEnter += HandleDragEnter;
        DragDrop += HandleDragDrop;

        BuildWindow();
        LoadSettings();
        ApplyTheme(_runtime.Settings.DarkTheme);
        ConfigureTrayIcon();

        Shown += HandleShown;
        FormClosing += HandleFormClosing;
        FormClosed += (_, _) =>
        {
            _sortedTreeImages?.Dispose();
            foreach (var image in _sortedTreeImageSources) image.Dispose();
            foreach (var image in _sortedShortcutImages) image.Dispose();
            _startupCancellation.Dispose();
        };
        Resize += HandleResize;
        _refreshTimer.Tick += async (_, _) => await RefreshAllAsync(refreshSortedFolders: false);
        _runtime.FilesChanged += QueueRefresh;
        _runtime.StatusChanged += message => PostToUi(() => SetStatus(message));
        _runtime.FileSorted += HandleFileSorted;
    }

    private void BuildWindow()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(18, 14, 18, 12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);

        var workspace = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 188));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        workspace.Controls.Add(BuildNavigation(), 0, 0);

        _pageHost.Dock = DockStyle.Fill;
        _pageHost.Margin = new Padding(12, 0, 0, 0);
        var pageDefinitions = new (string Key, Panel Page)[]
        {
            ("home", BuildDashboardTab()),
            ("search", BuildSearchTab()),
            ("review", BuildReviewTab()),
            ("history", BuildHistoryTab()),
            ("settings", BuildSettingsTab()),
            ("api", BuildApiTab())
        };
        foreach (var (key, page) in pageDefinitions)
        {
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _pages.Add(key, page);
            _pageHost.Controls.Add(page);
        }
        workspace.Controls.Add(_pageHost, 1, 0);
        root.Controls.Add(workspace, 0, 1);
        SelectPage("home");

        var statusPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(2, 4, 2, 0) };
        _status.Dock = DockStyle.Fill;
        _status.Text = "Готово к работе.";
        _status.ForeColor = Color.FromArgb(78, 88, 103);
        statusPanel.Controls.Add(_status);
        root.Controls.Add(statusPanel, 0, 2);
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill };
        var title = new Label
        {
            Text = "Optium+ File Manager",
            Font = new Font("Segoe UI Semibold", 21, FontStyle.Bold),
            ForeColor = Color.FromArgb(28, 42, 63),
            AutoSize = true,
            Location = new Point(2, 0)
        };
        var subtitle = new Label
        {
            Text = "Автоматическая сортировка и быстрый поиск по файлам",
            ForeColor = Color.FromArgb(94, 106, 124),
            AutoSize = true,
            Location = new Point(4, 42)
        };
        _darkTheme.Text = "Тёмная тема";
        _darkTheme.AutoSize = true;
        _darkTheme.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _darkTheme.Location = new Point(0, 16);
        _darkTheme.CheckedChanged += (_, _) =>
        {
            if (_initializing) return;
            _runtime.SetDarkTheme(_darkTheme.Checked);
            ApplyTheme(_darkTheme.Checked);
            SetStatus(_darkTheme.Checked ? "Тёмная тема включена." : "Светлая тема включена.");
        };
        header.Resize += (_, _) =>
        {
            _darkTheme.Left = Math.Max(700, header.ClientSize.Width - _darkTheme.Width);
            _globalSearch.Width = Math.Max(220, _darkTheme.Left - _globalSearch.Left - _globalSearchButton.Width - 20);
            _globalSearchButton.Left = _globalSearch.Right + 8;
        };
        _globalSearch.PlaceholderText = "Поиск по имени и содержимому…  Ctrl+K";
        _globalSearch.Location = new Point(398, 16);
        _globalSearch.Size = new Size(390, 30);
        _globalSearch.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _globalSearch.KeyDown += async (_, args) =>
        {
            if (args.KeyCode != Keys.Enter) return;
            args.SuppressKeyPress = true;
            await RunGlobalSearchAsync();
        };
        _globalSearchButton.Text = "Найти";
        _globalSearchButton.Size = new Size(76, 32);
        _globalSearchButton.Location = new Point(796, 15);
        _globalSearchButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _globalSearchButton.Tag = "accent";
        _globalSearchButton.FlatStyle = FlatStyle.Flat;
        _globalSearchButton.FlatAppearance.BorderSize = 0;
        _globalSearchButton.Click += async (_, _) => await RunGlobalSearchAsync();
        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        header.Controls.Add(_globalSearch);
        header.Controls.Add(_globalSearchButton);
        header.Controls.Add(_darkTheme);
        return header;
    }

    private Control BuildNavigation()
    {
        var navigation = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
        var heading = new Label
        {
            Text = "РАЗДЕЛЫ",
            Dock = DockStyle.Top,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(112, 133, 120),
            Padding = new Padding(10, 0, 0, 0)
        };
        var items = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        var sections = new (string Key, string Title)[]
        {
            ("home", "⌂   Главная"),
            ("search", "⌕   Быстрый поиск"),
            ("review", "✓   На проверку"),
            ("history", "◷   История"),
            ("settings", "⚙   Настройки"),
            ("api", "⌘   Интеграции")
        };
        foreach (var (key, title) in sections)
        {
            var button = new Button
            {
                Text = title,
                Width = 164,
                Height = 44,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 4, 0),
                Margin = new Padding(0, 2, 0, 2),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Tag = "nav"
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => SelectPage(key);
            _navigationButtons.Add(key, button);
            items.Controls.Add(button);
        }
        navigation.Controls.Add(items);
        navigation.Controls.Add(heading);
        return navigation;
    }

    private void SelectPage(string key)
    {
        if (!_pages.TryGetValue(key, out var selected)) return;
        foreach (var (pageKey, page) in _pages)
            page.Visible = pageKey == key;
        selected.BringToFront();
        foreach (var (buttonKey, button) in _navigationButtons)
        {
            var selectedPage = buttonKey == key;
            button.Tag = selectedPage ? "nav-active" : "nav";
            button.BackColor = selectedPage
                ? (_darkTheme.Checked ? Color.FromArgb(91, 143, 106) : Color.FromArgb(45, 102, 177))
                : (_darkTheme.Checked ? Color.FromArgb(22, 33, 27) : Color.White);
            button.ForeColor = selectedPage || !_darkTheme.Checked
                ? (selectedPage ? Color.White : Color.FromArgb(28, 42, 63))
                : Color.FromArgb(226, 235, 228);
            button.FlatAppearance.MouseOverBackColor = selectedPage
                ? (_darkTheme.Checked ? Color.FromArgb(91, 143, 106) : Color.FromArgb(45, 102, 177))
                : (_darkTheme.Checked ? Color.FromArgb(35, 51, 41) : Color.FromArgb(235, 242, 250));
        }
    }

    private void HandleGlobalShortcut(object? sender, KeyEventArgs args)
    {
        if (args.Control && args.KeyCode == Keys.K)
        {
            args.SuppressKeyPress = true;
            _globalSearch.Focus();
            _globalSearch.SelectAll();
        }
    }

    private Panel BuildDashboardTab()
    {
        var page = CreateTab("Обзор");
        var layout = NewLayout(1, 5);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        page.Controls.Add(layout);

        var cards = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 2, 0, 2) };
        cards.Controls.Add(CreateMetricCard("queue", "В очереди", "0", Color.FromArgb(70, 111, 184)));
        cards.Controls.Add(CreateMetricCard("today", "Обработано сегодня", "0", Color.FromArgb(41, 142, 111)));
        cards.Controls.Add(CreateMetricCard("review", "На проверке", "0", Color.FromArgb(208, 143, 39)));
        cards.Controls.Add(CreateMetricCard("errors", "Ошибки / карантин", "0", Color.FromArgb(187, 77, 77)));
        layout.Controls.Add(cards, 0, 0);

        var actionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(0, 8, 0, 0), AutoScroll = false };
        _sortNow.Text = "Сортировать входящие";
        _sortNow.Width = 190;
        _sortNow.Height = 36;
        _sortNow.BackColor = Color.FromArgb(45, 102, 177);
        _sortNow.ForeColor = Color.White;
        _sortNow.Tag = "accent";
        _sortNow.FlatStyle = FlatStyle.Flat;
        _sortNow.FlatAppearance.BorderSize = 0;
        _sortNow.Click += async (_, _) => await RunActionAsync(SortNowAsync);
        var addFile = MakeAsyncButton("Добавить файлы", PickFilesAsync);
        var openSorted = MakeButton("Открыть результаты", () => OpenFolder(_runtime.Paths.Sorted));
        _automatic.Text = "Автосортировка";
        _automatic.AutoSize = true;
        _automatic.Margin = new Padding(14, 9, 0, 0);
        _automatic.CheckedChanged += async (_, _) =>
        {
            if (_initializing) return;
            await RunActionAsync(async () => await _runtime.SetAutomaticSortingAsync(_automatic.Checked));
        };
        _openDestinationAfterSort.Text = "Открывать папки";
        _openDestinationAfterSort.AutoSize = true;
        _openDestinationAfterSort.Margin = new Padding(12, 9, 0, 0);
        _openDestinationAfterSort.CheckedChanged += (_, _) =>
        {
            if (_initializing) return;
            _runtime.SetOpenDestinationFolderAfterSort(_openDestinationAfterSort.Checked);
            SetStatus(_openDestinationAfterSort.Checked
                ? "Папка назначения будет открываться при первой сортировке в неё за этот запуск."
                : "Автоматическое открытие папки назначения выключено.");
        };
        actionRow.Controls.AddRange([_sortNow, addFile, openSorted, _automatic, _openDestinationAfterSort]);
        layout.Controls.Add(actionRow, 0, 1);

        var section = new Label
        {
            Text = "Отсортированные папки и готовые файлы",
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            ForeColor = Color.FromArgb(49, 62, 81)
        };
        layout.Controls.Add(section, 0, 2);

        var sortedArea = NewLayout(1, 3);
        sortedArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 166));
        sortedArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sortedArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        sortedArea.Controls.Add(BuildSortedFolderShortcuts(), 0, 0);
        ConfigureSortedTree();
        sortedArea.Controls.Add(_sortedTree, 0, 1);
        var treeActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0)
        };
        treeActions.Controls.Add(MakeButton("Открыть выбранное", OpenSelectedSortedNode));
        treeActions.Controls.Add(MakeButton("Обновить список", RefreshSortedTree));
        treeActions.Controls.Add(MakeButton("Открыть папку результатов", () => OpenFolder(_runtime.Paths.Sorted)));
        sortedArea.Controls.Add(treeActions, 0, 2);
        layout.Controls.Add(sortedArea, 0, 3);

        _sortedTreeStatus.Dock = DockStyle.Fill;
        _sortedTreeStatus.Text = "Папки загружаются…";
        _sortedTreeStatus.TextAlign = ContentAlignment.MiddleLeft;
        _sortedTreeStatus.ForeColor = Color.FromArgb(83, 97, 115);
        layout.Controls.Add(_sortedTreeStatus, 0, 4);
        return page;
    }

    private Panel BuildSearchTab()
    {
        var page = CreateTab("Поиск файлов");
        var layout = NewLayout(1, 5);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        page.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Text = "Опишите файл обычными словами. Локальный поиск проверяет имя и извлечённое содержимое документов, кода и изображений с доступным OCR. Настроенный ИИ может дополнительно оценить найденные результаты.",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(83, 97, 115),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var queryRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        queryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        queryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        _searchQuery.Dock = DockStyle.Fill;
        _searchQuery.PlaceholderText = "Например: математический модуль на C#";
        _searchQuery.Margin = new Padding(0, 5, 10, 5);
        _searchQuery.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await RunFileSearchAsync();
        };
        _searchButton.Text = "Найти файл";
        _searchButton.Dock = DockStyle.Fill;
        _searchButton.Margin = new Padding(0, 5, 0, 5);
        _searchButton.FlatStyle = FlatStyle.Flat;
        _searchButton.FlatAppearance.BorderSize = 0;
        _searchButton.BackColor = Color.FromArgb(45, 102, 177);
        _searchButton.ForeColor = Color.White;
        _searchButton.Click += async (_, _) => await RunFileSearchAsync();
        queryRow.Controls.Add(_searchQuery, 0, 0);
        queryRow.Controls.Add(_searchButton, 1, 0);
        layout.Controls.Add(queryRow, 0, 1);

        _searchStatus.Dock = DockStyle.Fill;
        _searchStatus.Text = "Поиск выполняется по папке результатов сортировки.";
        _searchStatus.ForeColor = Color.FromArgb(78, 88, 103);
        _searchStatus.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(_searchStatus, 0, 2);

        ConfigureGrid(_searchGrid);
        AddTextColumn(_searchGrid, "Name", "Файл", 230);
        AddTextColumn(_searchGrid, "Type", "Тип", 90);
        AddTextColumn(_searchGrid, "Folder", "Папка", 230);
        AddTextColumn(_searchGrid, "Score", "Совпадение", 100);
        AddTextColumn(_searchGrid, "Reason", "Почему найден", 300);
        _searchGrid.CellDoubleClick += (_, _) => OpenSelectedSearchFile();
        layout.Controls.Add(_searchGrid, 0, 3);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0), WrapContents = false };
        var show = MakeButton("Показать файл в папке", OpenSelectedSearchFile);
        _cancelSearchButton.Text = "Остановить поиск";
        _cancelSearchButton.AutoSize = true;
        _cancelSearchButton.Enabled = false;
        _cancelSearchButton.Click += (_, _) => _activeSearch?.Cancel();
        actions.Controls.AddRange([show, _cancelSearchButton,
            MakeButton("Открыть папку результатов", () => OpenFolder(_runtime.Paths.Sorted))]);
        layout.Controls.Add(actions, 0, 4);
        return page;
    }

    private Panel BuildReviewTab()
    {
        var page = CreateTab("На проверку");
        var layout = NewLayout(1, 4);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        page.Controls.Add(layout);

        ConfigureGrid(_reviewGrid);
        AddTextColumn(_reviewGrid, "Name", "Файл", 230);
        AddTextColumn(_reviewGrid, "Format", "Формат", 120);
        AddTextColumn(_reviewGrid, "Category", "Предложенная категория", 220);
        AddTextColumn(_reviewGrid, "Confidence", "Уверенность", 100);
        AddTextColumn(_reviewGrid, "Reason", "Причина", 320);
        _reviewGrid.SelectionChanged += (_, _) =>
        {
            _rememberFileNameRule.Checked = false;
            UpdateReviewButtons();
        };
        _reviewGrid.CellDoubleClick += (_, _) => OpenSelectedReviewFile();
        layout.Controls.Add(_reviewGrid, 0, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), WrapContents = false };
        _reviewCategory.DropDownStyle = ComboBoxStyle.DropDownList;
        _reviewCategory.Width = 290;
        _reviewCategory.DisplayMember = nameof(CategoryChoice.DisplayName);
        _reviewCategory.ValueMember = nameof(CategoryChoice.Key);
        _reviewCategory.DataSource = CategoryCatalog.AllChoices()
            .Select(choice => new CategoryChoice(choice.CategoryId, choice.SubcategoryId, choice.DisplayName)).ToList();
        var approve = MakeAsyncButton("Подтвердить и переместить", ApproveSelectedAsync);
        approve.Name = "approve";
        var reanalyze = MakeAsyncButton("Повторный анализ", () => ReanalyzeSelectedAsync(_reviewGrid));
        var skip = MakeAsyncButton("Не сортировать", SkipSelectedAsync);
        skip.Name = "skip";
        var open = MakeButton("Показать в папке", OpenSelectedReviewFile);
        actions.Controls.AddRange([_reviewCategory, approve, reanalyze, skip, open]);
        layout.Controls.Add(actions, 0, 1);
        _rememberFileNameRule.Text = "Запомнить категорию для файлов с таким же именем";
        _rememberFileNameRule.AutoSize = true;
        _rememberFileNameRule.Margin = new Padding(4, 5, 0, 0);
        layout.Controls.Add(_rememberFileNameRule, 0, 2);
        var help = new Label
        {
            Text = "Низкая уверенность, конфликт формата и повреждённые документы остаются здесь до решения пользователя.",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(94, 106, 124),
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(help, 0, 3);
        return page;
    }

    private Panel BuildHistoryTab()
    {
        var page = CreateTab("История");
        var layout = NewLayout(1, 3);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(layout);

        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0), WrapContents = false };
        _historyStatus.DropDownStyle = ComboBoxStyle.DropDownList;
        _historyStatus.Width = 165;
        _historyStatus.Items.AddRange(["Все статусы", nameof(FileStatus.Detected), nameof(FileStatus.Ready), nameof(FileStatus.Extracting),
            nameof(FileStatus.Classifying), nameof(FileStatus.Moving), nameof(FileStatus.Completed), nameof(FileStatus.NeedsReview),
            nameof(FileStatus.Failed), nameof(FileStatus.Quarantined), nameof(FileStatus.Reverted), nameof(FileStatus.Duplicate), nameof(FileStatus.Skipped)]);
        _historyStatus.SelectedIndex = 0;
        _historyCategory.DropDownStyle = ComboBoxStyle.DropDownList;
        _historyCategory.Width = 195;
        _historyCategory.DisplayMember = nameof(HistoryFilterChoice.DisplayName);
        _historyCategory.ValueMember = nameof(HistoryFilterChoice.Value);
        _historyCategory.DataSource = new[] { new HistoryFilterChoice("", "Все категории") }
            .Concat(CategoryCatalog.Categories.Select(category => new HistoryFilterChoice(category.Id, category.DisplayName))).ToList();
        _historySource.DropDownStyle = ComboBoxStyle.DropDownList;
        _historySource.Width = 140;
        _historySource.DisplayMember = nameof(HistoryFilterChoice.DisplayName);
        _historySource.ValueMember = nameof(HistoryFilterChoice.Value);
        _historySource.DataSource = new[]
        {
            new HistoryFilterChoice("", "Все источники"),
            new HistoryFilterChoice("watch", "Наблюдение"),
            new HistoryFilterChoice("manual", "Вручную"),
            new HistoryFilterChoice("api", "HTTP API"),
            new HistoryFilterChoice("mcp", "MCP")
        };
        _historyDate.DropDownStyle = ComboBoxStyle.DropDownList;
        _historyDate.Width = 155;
        _historyDate.DisplayMember = nameof(HistoryFilterChoice.DisplayName);
        _historyDate.ValueMember = nameof(HistoryFilterChoice.Value);
        _historyDate.DataSource = new[]
        {
            new HistoryFilterChoice("0", "За всё время"),
            new HistoryFilterChoice("1", "За 24 часа"),
            new HistoryFilterChoice("7", "За 7 дней"),
            new HistoryFilterChoice("30", "За 30 дней")
        };
        filters.Controls.AddRange([_historyStatus, _historyCategory, _historySource, _historyDate]);
        _historyStatus.SelectedIndexChanged += async (_, _) => await RunActionAsync(RefreshHistoryAsync);
        _historyCategory.SelectedIndexChanged += async (_, _) => await RunActionAsync(RefreshHistoryAsync);
        _historySource.SelectedIndexChanged += async (_, _) => await RunActionAsync(RefreshHistoryAsync);
        _historyDate.SelectedIndexChanged += async (_, _) => await RunActionAsync(RefreshHistoryAsync);
        layout.Controls.Add(filters, 0, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 5, 0, 0), WrapContents = false };
        var revert = MakeAsyncButton("Отменить перемещение", RevertSelectedAsync);
        revert.Name = "revert";
        var reanalyze = MakeAsyncButton("Повторный анализ", () => ReanalyzeSelectedAsync(_historyGrid));
        var export = MakeAsyncButton("Экспорт CSV", ExportHistoryAsync);
        actions.Controls.AddRange([revert, reanalyze, export]);
        layout.Controls.Add(actions, 0, 1);

        ConfigureGrid(_historyGrid);
        AddTextColumn(_historyGrid, "Name", "Файл", 230);
        AddTextColumn(_historyGrid, "Category", "Категория", 200);
        AddTextColumn(_historyGrid, "Format", "Формат", 120);
        AddTextColumn(_historyGrid, "Provider", "Анализатор", 110);
        AddTextColumn(_historyGrid, "Status", "Статус", 130);
        AddTextColumn(_historyGrid, "Reason", "Причина / итог", 300);
        AddTextColumn(_historyGrid, "Created", "Создан", 180);
        _historyGrid.SelectionChanged += (_, _) => UpdateReviewButtons();
        layout.Controls.Add(_historyGrid, 0, 2);
        return page;
    }

    private Panel BuildSettingsTab()
    {
        var page = CreateTab("Настройки");
        var layout = NewLayout(2, 6);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 75));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        page.Controls.Add(layout);

        var foldersTitle = SectionTitle("Входящие папки");
        layout.Controls.Add(foldersTitle, 0, 0);
        layout.SetColumnSpan(foldersTitle, 2);
        _folderList.Dock = DockStyle.Fill;
        _folderList.IntegralHeight = false;
        _folderList.HorizontalScrollbar = true;
        layout.Controls.Add(_folderList, 0, 1);

        var folderButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
        folderButtons.Controls.Add(MakeButton("Добавить папку", AddWatchFolder));
        folderButtons.Controls.Add(MakeButton("Удалить выбранную", RemoveWatchFolder));
        folderButtons.Controls.Add(MakeButton("Исключения…", ConfigureWatchExclusions));
        layout.Controls.Add(folderButtons, 0, 2);

        var watching = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(5, 9, 0, 0) };
        _recursive.Text = "Включая вложенные папки";
        _recursive.AutoSize = true;
        _recursive.CheckedChanged += async (_, _) =>
        {
            if (!_initializing) await RunActionAsync(async () => await _runtime.SetRecursiveWatchingAsync(_recursive.Checked));
        };
        _startWithWindows.Text = "Запускать вместе с Windows";
        _startWithWindows.AutoSize = true;
        _startWithWindows.Margin = new Padding(18, 0, 0, 0);
        _startWithWindows.CheckedChanged += (_, _) =>
        {
            if (_initializing) return;
            try
            {
                _runtime.SetStartWithWindows(_startWithWindows.Checked);
                SetStatus(_startWithWindows.Checked ? "Автозапуск включён." : "Автозапуск выключен.");
            }
            catch (Exception exception) { SetStatus($"Настройку автозапуска не удалось применить: {exception.Message}"); }
        };
        watching.Controls.AddRange([_recursive, _startWithWindows]);
        layout.Controls.Add(watching, 1, 1);
        layout.SetRowSpan(watching, 2);

        var sortSettingsTitle = SectionTitle("Локальная безопасность и ИИ");
        layout.Controls.Add(sortSettingsTitle, 0, 3);
        layout.SetColumnSpan(sortSettingsTitle, 2);
        var settingsPanel = NewLayout(2, 5);
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settingsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        settingsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        settingsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        settingsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 102));
        settingsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        settingsPanel.Controls.Add(CreateLabel("Режим ИИ"), 0, 0);
        _providerMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _providerMode.Items.AddRange(["Off", "Auto", "OpenAI", "Gemini", "Claude", "Compare"]);
        settingsPanel.Controls.Add(_providerMode, 1, 0);
        settingsPanel.Controls.Add(CreateLabel("Максимальный размер"), 0, 1);
        _maxFileSize.Minimum = 1;
        _maxFileSize.Maximum = 2048;
        _maxFileSize.Width = 100;
        var sizePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        sizePanel.Controls.Add(_maxFileSize);
        sizePanel.Controls.Add(new Label { Text = "МБ", AutoSize = true, Padding = new Padding(3, 7, 0, 0) });
        settingsPanel.Controls.Add(sizePanel, 1, 1);

        settingsPanel.Controls.Add(CreateLabel("Лимит запросов к ИИ / месяц"), 0, 2);
        _monthlyAiRequestLimit.Minimum = 1;
        _monthlyAiRequestLimit.Maximum = 100_000;
        _monthlyAiRequestLimit.Width = 130;
        _monthlyAiUsage.AutoSize = true;
        _monthlyAiUsage.Padding = new Padding(8, 7, 0, 0);
        _monthlyAiUsage.ForeColor = Color.FromArgb(83, 97, 115);
        var quotaPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        quotaPanel.Controls.Add(_monthlyAiRequestLimit);
        quotaPanel.Controls.Add(_monthlyAiUsage);
        settingsPanel.Controls.Add(quotaPanel, 1, 2);

        var models = NewLayout(3, 3);
        models.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        models.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        models.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        models.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        models.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        models.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        AddProviderRow(models, 0, "OpenAI", _openAiModel, _openAiHealth);
        AddProviderRow(models, 1, "Gemini", _geminiModel, _geminiHealth);
        AddProviderRow(models, 2, "Claude", _claudeModel, _claudeHealth);
        settingsPanel.Controls.Add(models, 0, 3);
        settingsPanel.SetColumnSpan(models, 2);

        var notes = new Label
        {
            Text = "Ключи задаются переменными среды и не сохраняются. В облако уходит ограниченный текст; вероятные секреты блокируются.",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(83, 97, 115),
            Padding = new Padding(0, 8, 0, 0)
        };
        var cloudPolicyArea = NewLayout(1, 2);
        cloudPolicyArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        cloudPolicyArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        cloudPolicyArea.Controls.Add(notes, 0, 0);
        cloudPolicyArea.Controls.Add(MakeAsyncButton("Форматы и приватные папки…", ShowCloudPolicyAsync), 0, 1);
        settingsPanel.Controls.Add(cloudPolicyArea, 0, 4);
        settingsPanel.SetColumnSpan(cloudPolicyArea, 2);
        layout.Controls.Add(settingsPanel, 0, 4);
        layout.SetColumnSpan(settingsPanel, 2);

        var save = MakeAsyncButton("Сохранить настройки анализа", SaveAnalysisSettingsAsync);
        layout.Controls.Add(save, 1, 5);
        return page;
    }

    private Panel BuildApiTab()
    {
        var page = CreateTab("API и MCP");
        var layout = NewLayout(1, 6);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 145));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(layout);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 7, 0, 0), WrapContents = false };
        _apiToggle.Text = "Запустить локальный API";
        _apiToggle.AutoSize = true;
        _apiToggle.Click += async (_, _) => await RunActionAsync(ToggleApiAsync);
        _apiStatus.Text = "API выключен";
        _apiStatus.AutoSize = true;
        _apiStatus.Padding = new Padding(12, 8, 0, 0);
        toolbar.Controls.AddRange([_apiToggle, _apiStatus]);
        layout.Controls.Add(toolbar, 0, 0);

        layout.Controls.Add(BuildTokenRow("Read-токен", _readToken, () => CopyToken(_apiTokens.ReadToken)), 0, 1);
        layout.Controls.Add(BuildTokenRow("Write-токен", _writeToken, () => CopyToken(_apiTokens.WriteToken)), 0, 2);

        var addressPanel = NewLayout(2, 2);
        addressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        addressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addressPanel.Controls.Add(CreateLabel("Адрес API"), 0, 0);
        _apiAddress.Dock = DockStyle.Fill;
        _apiAddress.TextAlign = ContentAlignment.MiddleLeft;
        addressPanel.Controls.Add(_apiAddress, 1, 0);
        addressPanel.Controls.Add(CreateLabel("MCP по HTTP"), 0, 1);
        addressPanel.Controls.Add(new Label { Text = $"{_apiServer.BaseAddress}/mcp (Streamable HTTP)", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 1, 1);
        layout.Controls.Add(addressPanel, 0, 3);

        var privacy = new Label
        {
            Text = "HTTP API слушает только 127.0.0.1. GET-запросы требуют read-токен; загрузка и изменение файлов требуют write-токен. Ссылка загрузки одноразовая и действует 15 минут, ссылка скачивания — 5 минут. Для доступа через туннель задайте FILE_SORTER_PUBLIC_BASE_URL и используйте HTTPS.",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(83, 97, 115),
            Padding = new Padding(0, 10, 0, 0)
        };
        layout.Controls.Add(privacy, 0, 4);

        var mcp = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9.5f),
            Text = "Локальный MCP stdio для Antigravity и совместимых клиентов:\r\n" +
                   "{\r\n  \"mcpServers\": {\r\n    \"optium-plus-file-manager\": {\r\n      \"command\": \"C:\\\\путь\\\\к\\\\OptiumPlusFileManager.exe\",\r\n      \"args\": [\"--mcp-stdio\"]\r\n    }\r\n  }\r\n}\r\n\r\n" +
                   "Замените путь на собранный exe. У stdio отдельный процесс, поэтому MCP не зависит от HTTP-токена."
        };
        layout.Controls.Add(mcp, 0, 5);
        _readToken.UseSystemPasswordChar = true;
        _writeToken.UseSystemPasswordChar = true;
        _readToken.Text = _apiTokens.ReadToken;
        _writeToken.Text = _apiTokens.WriteToken;
        return page;
    }

    private async void HandleShown(object? sender, EventArgs e)
    {
        if (_startMinimized)
            WindowState = FormWindowState.Minimized;

        SetRuntimeReady(false);
        SetStatus("Открываю приложение и подготавливаю локальную базу…");
        _startupTask = Task.Run(() => _runtime.InitializeAsync(_startupCancellation.Token), CancellationToken.None);
        try
        {
            await _startupTask;
            if (IsDisposed || _startupCancellation.IsCancellationRequested) return;
            SetRuntimeReady(true);
            _refreshTimer.Start();
        }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            SetStatus($"Запуск остановился: {exception.Message}");
            MessageBox.Show(this,
                $"Приложение открылось, но не завершило инициализацию.\n\n{exception.GetType().Name}: {exception.Message}",
                "Optium+ File Manager — ошибка запуска", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            await _apiServer.StartAsync();
            UpdateApiState();
        }
        catch (Exception exception)
        {
            _apiStatus.Text = "API не запущен";
            SetStatus($"Локальный API не запустился: {exception.Message}");
        }
        await RefreshAllAsync();
    }

    private async Task RefreshAllAsync(bool refreshSortedFolders = true)
    {
        if (_refreshing || IsDisposed) return;
        _refreshing = true;
        try
        {
            var stats = await _runtime.GetStatsAsync();
            _metrics["queue"].Text = stats.PendingCount.ToString();
            _metrics["today"].Text = stats.ProcessedToday.ToString();
            _metrics["review"].Text = stats.ReviewCount.ToString();
            _metrics["errors"].Text = $"{stats.ErrorCount} / {stats.QuarantineCount}";
            var usedAiRequests = await _runtime.GetAiRequestsUsedThisMonthAsync();
            _monthlyAiUsage.Text = $"{usedAiRequests:N0} использовано";

            if (refreshSortedFolders || DateTime.UtcNow - _lastSortedTreeRefreshUtc >= TimeSpan.FromSeconds(30))
                RefreshSortedTree();
            await RefreshReviewAsync();
            await RefreshHistoryAsync();
            UpdateProviderHealth();
        }
        catch (Exception exception) { SetStatus($"Не удалось обновить экран: {exception.Message}"); }
        finally { _refreshing = false; }
    }

    private async Task RunGlobalSearchAsync()
    {
        _searchQuery.Text = _globalSearch.Text;
        SelectPage("search");
        await RunFileSearchAsync();
    }

    private async Task RunFileSearchAsync()
    {
        var query = _searchQuery.Text.Trim();
        if (query.Length == 0)
        {
            _searchStatus.Text = "Введите описание файла для поиска.";
            _searchQuery.Focus();
            return;
        }

        _activeSearch?.Cancel();
        _activeSearch?.Dispose();
        var searchCancellation = new CancellationTokenSource();
        _activeSearch = searchCancellation;
        _searchButton.Enabled = false;
        _cancelSearchButton.Enabled = true;
        _searchStatus.ForeColor = Color.FromArgb(78, 88, 103);
        _searchStatus.Text = "Ищу по локальному индексу содержимого…";
        SetStatus("Ищу файл в результатах сортировки…");
        try
        {
            var result = await _runtime.SearchFilesAsync(query, searchCancellation.Token);
            if (!ReferenceEquals(_activeSearch, searchCancellation)) return;
            _searchItems = result.Matches;
            _searchGrid.SuspendLayout();
            _searchGrid.Rows.Clear();
            foreach (var hit in result.Matches)
            {
                var index = _searchGrid.Rows.Add(hit.FileName, hit.FileType, hit.CategoryPath,
                    $"{hit.Score:P0}", hit.Reason);
                _searchGrid.Rows[index].Tag = hit.FilePath;
            }
            _searchGrid.ResumeLayout();
            _searchStatus.Text = result.Message;
            _searchStatus.ForeColor = result.Matches.Count == 0
                ? Color.FromArgb(154, 104, 25)
                : Color.FromArgb(41, 112, 85);
            SetStatus(result.Message);
        }
        catch (OperationCanceledException) when (searchCancellation.IsCancellationRequested)
        {
            if (ReferenceEquals(_activeSearch, searchCancellation))
            {
                _searchStatus.Text = "Поиск остановлен.";
                SetStatus("Поиск остановлен пользователем.");
            }
        }
        catch (Exception exception)
        {
            _searchStatus.Text = $"Не удалось выполнить поиск: {exception.Message}";
            _searchStatus.ForeColor = Color.FromArgb(159, 57, 57);
            SetStatus("Поиск завершился с ошибкой.");
        }
        finally
        {
            if (ReferenceEquals(_activeSearch, searchCancellation))
            {
                _activeSearch = null;
                _searchButton.Enabled = true;
                _cancelSearchButton.Enabled = false;
            }
            searchCancellation.Dispose();
        }
    }

    private void OpenSelectedSearchFile()
    {
        if (_searchGrid.SelectedRows.Count == 0 || _searchGrid.SelectedRows[0].Tag is not string path)
        {
            _searchStatus.Text = "Сначала выберите найденный файл.";
            return;
        }
        if (!File.Exists(path) || !FileSafety.IsWithin(path, _runtime.Paths.Sorted) || FileSafety.HasReparsePoint(path))
        {
            _searchStatus.Text = "Файл уже перемещён или больше не находится в папке результатов. Выполните поиск ещё раз.";
            return;
        }
        ShowFileLocation(path);
    }

    private Control BuildSortedFolderShortcuts()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = false,
            Padding = new Padding(2, 3, 2, 3),
            Margin = Padding.Empty
        };

        var programming = CategoryCatalog.Categories.First(category => category.Id == "programming");
        var documents = CategoryCatalog.Categories.First(category => category.Id == "documents");
        var images = CategoryCatalog.Categories.First(category => category.Id == "images");
        var archives = CategoryCatalog.Categories.First(category => category.Id == "archives");
        var csharpPath = CategoryCatalog.GetPath("programming", "csharp");
        var pythonPath = CategoryCatalog.GetPath("programming", "python");
        var sqlPath = CategoryCatalog.GetPath("programming", "sql");
        var shortcuts = new[]
        {
            new SortedFolderShortcut("C#", Path.Combine(_runtime.Paths.Sorted, csharpPath.Category, csharpPath.Subcategory!), "C#", Color.FromArgb(113, 91, 209)),
            new SortedFolderShortcut("Python", Path.Combine(_runtime.Paths.Sorted, pythonPath.Category, pythonPath.Subcategory!), "Py", Color.FromArgb(49, 120, 198)),
            new SortedFolderShortcut("SQL", Path.Combine(_runtime.Paths.Sorted, sqlPath.Category, sqlPath.Subcategory!), "SQL", Color.FromArgb(38, 152, 136)),
            new SortedFolderShortcut("Документы", Path.Combine(_runtime.Paths.Sorted, documents.DirectoryName), "DOC", Color.FromArgb(218, 152, 48)),
            new SortedFolderShortcut("Изображения", Path.Combine(_runtime.Paths.Sorted, images.DirectoryName), "IMG", Color.FromArgb(163, 94, 219)),
            new SortedFolderShortcut("Архивы", Path.Combine(_runtime.Paths.Sorted, archives.DirectoryName), "ZIP", Color.FromArgb(205, 108, 50)),
            new SortedFolderShortcut("Все файлы", _runtime.Paths.Sorted, "ALL", Color.FromArgb(70, 112, 184))
        };
        var shortcutButtons = new List<Button>(shortcuts.Length);

        foreach (var shortcut in shortcuts)
        {
            var icon = CreateFolderIcon(50, shortcut.Color, shortcut.Badge);
            _sortedShortcutImages.Add(icon);
            var button = new Button
            {
                Width = 170,
                Height = 76,
                Tag = shortcut,
                Text = $"{shortcut.DisplayName}\r\nПапка",
                TextAlign = ContentAlignment.MiddleLeft,
                Image = icon,
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                Padding = new Padding(6, 3, 3, 3),
                Margin = new Padding(3, 0, 3, 0),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderSize = 1;
            button.Click += (_, _) => SelectSortedShortcut(shortcut);
            _sortedShortcutButtons[Path.GetFullPath(shortcut.Path)] = button;
            shortcutButtons.Add(button);
            panel.Controls.Add(button);
        }

        panel.Resize += (_, _) => FitShortcutCards();
        FitShortcutCards();
        return panel;

        void FitShortcutCards()
        {
            var columnCount = Math.Clamp((panel.ClientSize.Width - panel.Padding.Horizontal + 6) / 176, 1, 4);
            var tileWidth = Math.Clamp((panel.ClientSize.Width - panel.Padding.Horizontal - columnCount * 6) / columnCount,
                155, 220);
            foreach (var button in shortcutButtons) button.Width = tileWidth;
        }
    }

    private void SelectSortedShortcut(SortedFolderShortcut shortcut)
    {
        var path = Path.GetFullPath(shortcut.Path);
        if (!FileSafety.IsWithin(path, _runtime.Paths.Sorted) || FileSafety.HasReparsePoint(path))
        {
            _sortedTreeStatus.Text = "Путь ярлыка находится вне безопасной папки результатов.";
            return;
        }

        if (path.Equals(Path.GetFullPath(_runtime.Paths.Sorted), StringComparison.OrdinalIgnoreCase))
        {
            OpenFolder(_runtime.Paths.Sorted);
            return;
        }

        _sortedTreeNodesByPath.TryGetValue(path, out var node);
        if (node is null)
        {
            _sortedTreeStatus.Text = "Папка появится в списке после обновления.";
            return;
        }

        node.Expand();
        node.EnsureVisible();
        _sortedTree.SelectedNode = node;
        _sortedTree.Focus();
        _sortedTreeStatus.Text = $"Быстрый переход: {shortcut.DisplayName}. В папке файлов: {CountChildFiles(node)}.";
    }

    private void UpdateSortedShortcutCounts()
    {
        foreach (var pair in _sortedShortcutButtons)
        {
            if (pair.Value.Tag is not SortedFolderShortcut shortcut) continue;
            if (Path.GetFullPath(shortcut.Path).Equals(Path.GetFullPath(_runtime.Paths.Sorted), StringComparison.OrdinalIgnoreCase))
            {
                var total = _sortedTree.Nodes.Cast<TreeNode>().Sum(CountChildFiles);
                pair.Value.Text = $"{shortcut.DisplayName}\r\n{FormatFileCount(total)}";
                continue;
            }

            _sortedTreeNodesByPath.TryGetValue(pair.Key, out var node);
            var count = node is null ? 0 : CountChildFiles(node);
            pair.Value.Text = $"{shortcut.DisplayName}\r\n{FormatFileCount(count)}";
        }
    }

    private static string FormatFileCount(int count)
    {
        var remainder100 = count % 100;
        if (remainder100 is >= 11 and <= 14) return $"{count} файлов";
        return (count % 10) switch
        {
            1 => $"{count} файл",
            2 or 3 or 4 => $"{count} файла",
            _ => $"{count} файлов"
        };
    }

    private static int CountChildFiles(TreeNode node) => node.Nodes.Cast<TreeNode>()
        .Sum(child => child.Name == "sorted-file" ? 1 : CountChildFiles(child));

    private ImageList CreateSortedTreeImageList()
    {
        var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(26, 26), TransparentColor = Color.Transparent };
        var icons = new (string Key, string Badge, Color Color)[]
        {
            ("folder", "DIR", Color.FromArgb(70, 112, 184)),
            ("code", "<> ", Color.FromArgb(49, 120, 198)),
            ("document", "DOC", Color.FromArgb(218, 152, 48)),
            ("image", "IMG", Color.FromArgb(163, 94, 219)),
            ("archive", "ZIP", Color.FromArgb(205, 108, 50)),
            ("audio", "AUD", Color.FromArgb(38, 152, 136)),
            ("video", "VID", Color.FromArgb(213, 77, 102)),
            ("application", "APP", Color.FromArgb(105, 116, 133)),
            ("file", "FILE", Color.FromArgb(105, 116, 133))
        };
        foreach (var icon in icons)
        {
            var bitmap = CreateFolderIcon(26, icon.Color, icon.Badge.Trim());
            _sortedTreeImageSources.Add(bitmap);
            images.Images.Add(icon.Key, bitmap);
        }
        return images;
    }

    private static Bitmap CreateFolderIcon(int size, Color color, string badge)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var shape = new GraphicsPath();
        var points = new[]
        {
            new PointF(size * 0.12f, size * 0.31f), new PointF(size * 0.39f, size * 0.31f),
            new PointF(size * 0.49f, size * 0.41f), new PointF(size * 0.88f, size * 0.41f),
            new PointF(size * 0.84f, size * 0.82f), new PointF(size * 0.10f, size * 0.82f)
        };
        shape.AddPolygon(points);
        using var fill = new SolidBrush(color);
        using var edge = new Pen(ControlPaint.Dark(color), Math.Max(1f, size / 28f));
        graphics.FillPath(fill, shape);
        graphics.DrawPath(edge, shape);
        if (!string.IsNullOrWhiteSpace(badge))
        {
            using var font = new Font("Segoe UI Semibold", Math.Max(5.5f, size * 0.19f), FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.White);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(badge, font, brush, new RectangleF(size * 0.08f, size * 0.48f, size * 0.78f, size * 0.27f), format);
        }
        return bitmap;
    }

    private static string GetCategoryIconKey(string categoryId) => categoryId.ToLowerInvariant() switch
    {
        "programming" or "csharp" or "python" or "java" or "javascript" or "sql" or "c" or "cpp" or "html" or "css" or "other-code" => "code",
        "documents" or "study" or "work" or "finance" => "document",
        "images" or "photo" or "screenshot" or "scan" or "diagram" or "illustration" => "image",
        "archives" => "archive",
        "audio" => "audio",
        "video" => "video",
        "applications" => "application",
        _ => "folder"
    };

    private static string GetFileIconKey(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".cs" or ".py" or ".java" or ".js" or ".ts" or ".sql" or ".html" or ".css" or ".c" or ".cpp" or ".h") return "code";
        if (extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".heic" or ".avif") return "image";
        if (extension is ".doc" or ".docx" or ".pdf" or ".txt" or ".rtf" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".csv") return "document";
        if (extension is ".zip" or ".rar" or ".7z" or ".tar" or ".gz") return "archive";
        if (extension is ".mp3" or ".wav" or ".ogg" or ".flac" or ".m4a") return "audio";
        if (extension is ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv") return "video";
        if (extension is ".exe" or ".msi" or ".appx" or ".msix") return "application";
        return "file";
    }

    private static TreeNode CreateSortedTreeNode(string text, string path, string imageKey) => new(text)
    {
        Tag = path,
        ImageKey = imageKey,
        SelectedImageKey = imageKey,
        ToolTipText = path
    };

    private void ConfigureSortedTree()
    {
        _sortedTree.Dock = DockStyle.Fill;
        _sortedTree.BorderStyle = BorderStyle.FixedSingle;
        _sortedTree.HideSelection = false;
        _sortedTree.FullRowSelect = true;
        _sortedTree.ShowLines = true;
        _sortedTree.ShowPlusMinus = true;
        _sortedTree.ShowRootLines = false;
        _sortedTree.ItemHeight = 36;
        _sortedTree.Indent = 26;
        _sortedTree.HotTracking = true;
        _sortedTree.ShowNodeToolTips = true;
        _sortedTreeImages = CreateSortedTreeImageList();
        _sortedTree.ImageList = _sortedTreeImages;
        _sortedTree.NodeMouseDoubleClick += (_, args) => OpenSortedPath(args.Node?.Tag as string);
        _sortedTree.AfterSelect += (_, args) =>
        {
            if (args.Node?.Tag is not string path) return;
            var kind = Directory.Exists(path) ? "Папка" : "Файл";
            _sortedTreeStatus.Text = $"{kind}: {path}";
        };
    }

    private void RefreshSortedTree()
    {
        if (IsDisposed || !_sortedTree.IsHandleCreated && !IsHandleCreated) return;
        var expandedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TreeNode root in _sortedTree.Nodes) CaptureExpandedPaths(root, expandedPaths);
        var selectedPath = _sortedTree.SelectedNode?.Tag as string;
        var updating = false;
        try
        {
            EnsureSortedCategoryFoldersExist();
            _sortedTree.BeginUpdate();
            updating = true;
            _sortedTree.Nodes.Clear();
            _sortedTreeNodesByPath.Clear();
            var knownFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var category in CategoryCatalog.Categories)
            {
                var path = Path.Combine(_runtime.Paths.Sorted, category.DirectoryName);
                knownFolders.Add(Path.GetFullPath(path));
                var categoryNode = category.Subcategories.Count > 0
                    ? CreateSortedCategoryNode(path, category)
                    : CreateSortedFolderNode(path, category.DisplayName, 0, GetCategoryIconKey(category.Id));
                _sortedTree.Nodes.Add(categoryNode);
                if (category.Id == "programming") categoryNode.Expand();
            }

            if (Directory.Exists(_runtime.Paths.Sorted) && !FileSafety.HasReparsePoint(_runtime.Paths.Sorted))
            {
                foreach (var directory in Directory.EnumerateDirectories(_runtime.Paths.Sorted, "*", SearchOption.TopDirectoryOnly)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var fullPath = Path.GetFullPath(directory);
                    if (knownFolders.Contains(fullPath) || FileSafety.HasReparsePoint(fullPath)) continue;
                    _sortedTree.Nodes.Add(CreateSortedFolderNode(fullPath, Path.GetFileName(fullPath), 0));
                }
            }

            _sortedTreeNodesByPath.Clear();
            foreach (TreeNode root in _sortedTree.Nodes) IndexSortedTreeNodes(root);
            foreach (TreeNode root in _sortedTree.Nodes) RestoreExpandedPaths(root, expandedPaths);
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                if (_sortedTreeNodesByPath.TryGetValue(Path.GetFullPath(selectedPath), out var selectedNode))
                    _sortedTree.SelectedNode = selectedNode;
            }

            UpdateSortedShortcutCounts();
            var fileCount = _sortedTree.Nodes.Cast<TreeNode>().Sum(CountChildFiles);
            _sortedTreeStatus.Text = $"Категорий: {_sortedTree.Nodes.Count} · файлов: {fileCount}. Ярлыки сверху открывают нужную папку в списке.";
            _lastSortedTreeRefreshUtc = DateTime.UtcNow;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _sortedTreeStatus.Text = $"Не удалось обновить список папок: {exception.Message}";
        }
        finally
        {
            if (updating) _sortedTree.EndUpdate();
        }
    }

    private void EnsureSortedCategoryFoldersExist()
    {
        var root = Path.GetFullPath(_runtime.Paths.Sorted);
        if (FileSafety.HasReparsePoint(root))
            throw new UnauthorizedAccessException("Папка результатов проходит через символическую ссылку.");
        Directory.CreateDirectory(root);

        foreach (var category in CategoryCatalog.Categories)
        {
            var categoryPath = Path.GetFullPath(Path.Combine(root, category.DirectoryName));
            if (!FileSafety.IsWithin(categoryPath, root) || FileSafety.HasReparsePoint(categoryPath))
                throw new UnauthorizedAccessException("Путь категории выходит из папки результатов или связан с символической ссылкой.");
            Directory.CreateDirectory(categoryPath);

            foreach (var subcategory in category.Subcategories)
            {
                var subcategoryPath = Path.GetFullPath(Path.Combine(categoryPath, subcategory.DirectoryName));
                if (!FileSafety.IsWithin(subcategoryPath, root) || FileSafety.HasReparsePoint(subcategoryPath))
                    throw new UnauthorizedAccessException("Путь подкатегории выходит из папки результатов или связан с символической ссылкой.");
                Directory.CreateDirectory(subcategoryPath);
            }
        }
    }

    private static void CaptureExpandedPaths(TreeNode node, ISet<string> expandedPaths)
    {
        if (node.IsExpanded && node.Tag is string path) expandedPaths.Add(path);
        foreach (TreeNode child in node.Nodes) CaptureExpandedPaths(child, expandedPaths);
    }

    private static void RestoreExpandedPaths(TreeNode node, IReadOnlySet<string> expandedPaths)
    {
        if (node.Tag is string path && expandedPaths.Contains(path)) node.Expand();
        foreach (TreeNode child in node.Nodes) RestoreExpandedPaths(child, expandedPaths);
    }

    private void IndexSortedTreeNodes(TreeNode node)
    {
        if (node.Tag is string path)
            _sortedTreeNodesByPath.TryAdd(Path.GetFullPath(path), node);
        foreach (TreeNode child in node.Nodes) IndexSortedTreeNodes(child);
    }

    private TreeNode CreateSortedFolderNode(string path, string displayName, int depth, string iconKey = "folder")
    {
        var node = CreateSortedTreeNode(displayName, path, iconKey);
        if (!Directory.Exists(path) || FileSafety.HasReparsePoint(path)) return node;

        try
        {
            var files = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                .Where(file => !FileSafety.HasReparsePoint(file))
                .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length > 0) node.Text = $"{displayName} ({files.Length})";
            foreach (var file in files)
            {
                var fileNode = CreateSortedTreeNode(Path.GetFileName(file), file, GetFileIconKey(file));
                fileNode.Name = "sorted-file";
                node.Nodes.Add(fileNode);
            }

            if (depth >= 3) return node;
            foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly)
                         .Where(folder => !FileSafety.HasReparsePoint(folder))
                         .OrderBy(folder => Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase))
            {
                node.Nodes.Add(CreateSortedFolderNode(directory, SortedDirectoryDisplayName(directory), depth + 1));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            node.Nodes.Add(CreateSortedTreeNode($"Недоступно: {exception.Message}", path, "folder"));
        }

        return node;
    }

    private TreeNode CreateSortedCategoryNode(string path, CategoryDefinition category)
    {
        var node = CreateSortedTreeNode(category.DisplayName, path, GetCategoryIconKey(category.Id));
        var knownSubfolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            AddSortedFiles(node, path);
            foreach (var subcategory in category.Subcategories)
            {
                var subfolder = Path.Combine(path, subcategory.DirectoryName);
                knownSubfolders.Add(Path.GetFullPath(subfolder));
                node.Nodes.Add(CreateSortedFolderNode(subfolder, subcategory.DisplayName, 1, GetCategoryIconKey(subcategory.Id)));
            }

            if (!Directory.Exists(path) || FileSafety.HasReparsePoint(path)) return node;
            foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly)
                         .Where(folder => !knownSubfolders.Contains(Path.GetFullPath(folder)) && !FileSafety.HasReparsePoint(folder))
                         .OrderBy(folder => Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase))
            {
                node.Nodes.Add(CreateSortedFolderNode(directory, SortedDirectoryDisplayName(directory), 1));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            node.Nodes.Add(CreateSortedTreeNode($"Недоступно: {exception.Message}", path, "folder"));
        }

        return node;
    }

    private static void AddSortedFiles(TreeNode node, string path)
    {
        if (!Directory.Exists(path) || FileSafety.HasReparsePoint(path)) return;
        var files = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
            .Where(file => !FileSafety.HasReparsePoint(file))
            .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var file in files)
        {
            var fileNode = CreateSortedTreeNode(Path.GetFileName(file), file, GetFileIconKey(file));
            fileNode.Name = "sorted-file";
            node.Nodes.Add(fileNode);
        }
        if (files.Length > 0) node.Text = $"{node.Text} ({files.Length})";
    }

    private static string SortedDirectoryDisplayName(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var category in CategoryCatalog.Categories)
        {
            if (category.DirectoryName.Equals(name, StringComparison.OrdinalIgnoreCase)) return category.DisplayName;
            var subcategory = category.Subcategories.FirstOrDefault(item => item.DirectoryName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (subcategory is not null) return subcategory.DisplayName;
        }
        return name;
    }

    private void OpenSelectedSortedNode() => OpenSortedPath(_sortedTree.SelectedNode?.Tag as string);

    private void OpenSortedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !FileSafety.IsWithin(path, _runtime.Paths.Sorted) || FileSafety.HasReparsePoint(path))
        {
            _sortedTreeStatus.Text = "Выберите папку или файл внутри результатов сортировки.";
            return;
        }
        if (Directory.Exists(path))
        {
            OpenFolder(path);
        }
        else if (IsKnownCategoryDirectory(path))
        {
            try
            {
                Directory.CreateDirectory(path);
                OpenFolder(path);
                RefreshSortedTree();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                _sortedTreeStatus.Text = $"Не удалось открыть папку: {exception.Message}";
            }
        }
        else if (File.Exists(path)) ShowFileLocation(path);
        else _sortedTreeStatus.Text = "Выбранный путь больше не существует. Обновите список.";
    }

    private bool IsKnownCategoryDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        foreach (var category in CategoryCatalog.Categories)
        {
            var categoryPath = Path.GetFullPath(Path.Combine(_runtime.Paths.Sorted, category.DirectoryName));
            if (fullPath.Equals(categoryPath, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var subcategory in category.Subcategories)
            {
                var subcategoryPath = Path.GetFullPath(Path.Combine(categoryPath, subcategory.DirectoryName));
                if (fullPath.Equals(subcategoryPath, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private async Task RefreshReviewAsync()
    {
        _reviewItems = await _runtime.ListFilesAsync(nameof(FileStatus.NeedsReview), 500);
        FillGrid(_reviewGrid, _reviewItems, file =>
        [
            Path.GetFileName(file.OriginalName), file.DetectedFileType, CategoryName(file.CategoryId, file.SubcategoryId),
            $"{file.Confidence:P0}", file.Reason ?? file.Summary
        ]);
        UpdateReviewButtons();
    }

    private async Task RefreshHistoryAsync()
    {
        if (_historyStatus.Items.Count == 0) return;
        var selected = _historyStatus.SelectedItem?.ToString();
        var status = string.IsNullOrWhiteSpace(selected) || selected == "Все статусы" ? null : selected;
        var categoryId = (_historyCategory.SelectedValue as string)?.Trim();
        var sourceType = (_historySource.SelectedValue as string)?.Trim();
        var daysText = _historyDate.SelectedValue as string;
        var days = int.TryParse(daysText, out var parsedDays) ? parsedDays : 0;
        DateTime? createdAfterUtc = days > 0 ? DateTime.UtcNow.AddDays(-days) : null;
        _historyItems = await _runtime.ListFilesAsync(status, 500, categoryId: categoryId, sourceType: sourceType, createdAfterUtc: createdAfterUtc);
        FillGrid(_historyGrid, _historyItems, file =>
        [
            Path.GetFileName(file.OriginalName), CategoryName(file.CategoryId, file.SubcategoryId), file.DetectedFileType,
            file.ProviderId ?? "local", file.Status, file.Reason ?? file.Summary, file.CreatedAtUtc.ToLocalTime().ToString("g")
        ]);
        UpdateReviewButtons();
    }

    private void FillRecent(IReadOnlyList<FileRecord> files) => FillGrid(_recentGrid, files, file =>
    [
        Path.GetFileName(file.OriginalName), CategoryName(file.CategoryId, file.SubcategoryId), $"{file.Confidence:P0}",
        file.Status, file.CreatedAtUtc.ToLocalTime().ToString("g")
    ]);

    private static void FillGrid(DataGridView grid, IReadOnlyList<FileRecord> files, Func<FileRecord, string[]> values)
    {
        grid.SuspendLayout();
        grid.Rows.Clear();
        foreach (var file in files)
        {
            var rowIndex = grid.Rows.Add(values(file));
            grid.Rows[rowIndex].Tag = file.Id;
            if (file.Status == nameof(FileStatus.Failed) || file.Status == nameof(FileStatus.Quarantined))
                grid.Rows[rowIndex].DefaultCellStyle.ForeColor = Color.FromArgb(159, 57, 57);
            else if (file.Status == nameof(FileStatus.NeedsReview))
                grid.Rows[rowIndex].DefaultCellStyle.ForeColor = Color.FromArgb(154, 104, 25);
        }
        grid.ResumeLayout();
    }

    private void LoadSettings()
    {
        _initializing = true;
        _folderList.Items.Clear();
        foreach (var folder in _runtime.Settings.WatchFolders) _folderList.Items.Add(folder);
        _automatic.Checked = _runtime.Settings.AutomaticSorting;
        _openDestinationAfterSort.Checked = _runtime.Settings.OpenDestinationFolderAfterSort;
        _darkTheme.Checked = _runtime.Settings.DarkTheme;
        _recursive.Checked = _runtime.Settings.WatchSubdirectories;
        _startWithWindows.Checked = _runtime.Settings.StartWithWindows || AppStartupManager.IsEnabled();
        _providerMode.SelectedItem = _runtime.Settings.AiProviderMode;
        _maxFileSize.Value = Math.Clamp(_runtime.Settings.MaxFileSizeMb, (int)_maxFileSize.Minimum, (int)_maxFileSize.Maximum);
        _monthlyAiRequestLimit.Value = Math.Clamp(_runtime.Settings.MonthlyAiRequestLimit,
            (int)_monthlyAiRequestLimit.Minimum, (int)_monthlyAiRequestLimit.Maximum);
        _openAiModel.Text = _runtime.Settings.OpenAiModel;
        _geminiModel.Text = _runtime.Settings.GeminiModel;
        _claudeModel.Text = _runtime.Settings.ClaudeModel;
        _initializing = false;
    }

    private async Task SortNowAsync()
    {
        _sortNow.Enabled = false;
        SetStatus("Обрабатываю выбранные входящие папки...");
        try
        {
            var outcomes = await _runtime.SortNowAsync();
            var moved = outcomes.Count(outcome => outcome.WasMoved);
            var review = outcomes.Count(outcome => outcome.File.Status == nameof(FileStatus.NeedsReview));
            var failed = outcomes.Count(outcome => outcome.File.Status == nameof(FileStatus.Failed));
            SetStatus($"Завершено: перемещено {moved}, на проверку {review}, ошибок {failed}.");
            await RefreshAllAsync();
        }
        finally { _sortNow.Enabled = true; }
    }

    private async Task PickFilesAsync()
    {
        using var dialog = new OpenFileDialog { Title = "Добавить файлы", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var added = 0;
        foreach (var path in dialog.FileNames)
        {
            await _runtime.IngestFileAsync(path);
            added++;
        }
        SetStatus($"В шлюз добавлено файлов: {added}.");
        await RefreshAllAsync();
    }

    private async Task ApproveSelectedAsync()
    {
        var id = GetSelectedId(_reviewGrid);
        if (id is null || _reviewCategory.SelectedItem is not CategoryChoice choice) return;
        await _runtime.ApproveAsync(id, choice.CategoryId, choice.SubcategoryId, _rememberFileNameRule.Checked);
        SetStatus(_rememberFileNameRule.Checked
            ? "Категория подтверждена; правило по точному имени файла сохранено."
            : "Категория подтверждена, файл перемещён.");
        _rememberFileNameRule.Checked = false;
        await RefreshAllAsync();
    }

    private async Task SkipSelectedAsync()
    {
        var id = GetSelectedId(_reviewGrid);
        if (id is null) return;
        var result = await _runtime.SkipAsync(id);
        SetStatus($"Файл оставлен в папке проверки: {Path.GetFileName(result.CurrentPath)}.");
        await RefreshAllAsync();
    }

    private async Task ReanalyzeSelectedAsync(DataGridView grid)
    {
        var id = GetSelectedId(grid);
        if (id is null) return;
        var file = await _runtime.ReanalyzeAsync(id);
        SetStatus($"Повторный анализ завершён. Статус: {file.Status}.");
        await RefreshAllAsync();
    }

    private async Task RevertSelectedAsync()
    {
        var id = GetSelectedId(_historyGrid);
        if (id is null) return;
        await _runtime.RevertAsync(id);
        SetStatus("Перемещение отменено; файл возвращён в исходную папку.");
        await RefreshAllAsync();
    }

    private async Task SaveAnalysisSettingsAsync()
    {
        var mode = _providerMode.SelectedItem?.ToString() ?? "Off";
        await _runtime.SaveAnalysisSettingsAsync(mode, (int)_maxFileSize.Value, _openAiModel.Text, _geminiModel.Text,
            _claudeModel.Text, (int)_monthlyAiRequestLimit.Value);
        SetStatus("Параметры локального и облачного анализа сохранены.");
        UpdateProviderHealth();
    }

    private async Task ToggleApiAsync()
    {
        if (_apiServer.IsRunning)
        {
            await _apiServer.StopAsync();
            SetStatus("Локальный API остановлен.");
        }
        else
        {
            await _apiServer.StartAsync();
            SetStatus("Локальный API запущен на loopback.");
        }
        UpdateApiState();
    }

    private async Task AddWatchFolderAsync()
    {
        using var dialog = new FolderBrowserDialog { Description = "Выберите входящую папку", ShowNewFolderButton = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var folders = _runtime.Settings.WatchFolders.Append(Path.GetFullPath(dialog.SelectedPath));
        await _runtime.SetWatchFoldersAsync(folders);
        LoadSettings();
        SetStatus("Входящая папка добавлена.");
    }

    private async Task RemoveWatchFolderAsync()
    {
        if (_folderList.SelectedItem is not string selected) return;
        var folders = _runtime.Settings.WatchFolders.Where(folder => !folder.Equals(selected, StringComparison.OrdinalIgnoreCase)).ToArray();
        await _runtime.SetWatchFoldersAsync(folders);
        LoadSettings();
        SetStatus("Входящая папка удалена.");
    }

    private async Task ExportHistoryAsync()
    {
        using var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"optium-plus-file-manager-history-{DateTime.Now:yyyyMMdd}.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await using var writer = new StreamWriter(dialog.FileName, append: false, new UTF8Encoding(true));
        await writer.WriteLineAsync("Id,Name,Status,Category,Subcategory,Confidence,FileType,Provider,CreatedAtUtc,Reason");
        var selected = _historyStatus.SelectedItem?.ToString();
        var status = string.IsNullOrWhiteSpace(selected) || selected == "Все статусы" ? null : selected;
        var categoryId = (_historyCategory.SelectedValue as string)?.Trim();
        var sourceType = (_historySource.SelectedValue as string)?.Trim();
        var days = int.TryParse(_historyDate.SelectedValue as string, out var parsedDays) ? parsedDays : 0;
        DateTime? createdAfterUtc = days > 0 ? DateTime.UtcNow.AddDays(-days) : null;
        const int pageSize = 1000;
        var exportedRows = 0;
        for (var skip = 0; ; skip += pageSize)
        {
            var files = await _runtime.ListFilesAsync(status, pageSize, skip: skip, categoryId: categoryId,
                sourceType: sourceType, createdAfterUtc: createdAfterUtc);
            foreach (var file in files)
            {
                var fields = new[] { file.Id, file.OriginalName, file.Status, file.CategoryId, file.SubcategoryId,
                    file.Confidence.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), file.DetectedFileType,
                    file.ProviderId ?? "local", file.CreatedAtUtc.ToString("O"), file.Reason ?? file.Summary };
                await writer.WriteLineAsync(string.Join(',', fields.Select(field => CsvEscape(field))));
                exportedRows++;
            }
            if (files.Count < pageSize) break;
        }
        await writer.FlushAsync();
        SetStatus($"Экспортировано записей: {exportedRows}. Файл: {dialog.FileName}");
    }

    private static string CsvEscape(string value)
    {
        var leadingText = value.TrimStart(' ', '\t', '\r', '\n');
        if (leadingText.Length > 0 && leadingText[0] is '=' or '+' or '-' or '@')
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private void OpenSelectedReviewFile()
    {
        var id = GetSelectedId(_reviewGrid);
        var file = _reviewItems.FirstOrDefault(item => item.Id == id);
        if (file is null) return;
        ShowFileLocation(file.CurrentPath);
    }

    private void UpdateProviderHealth()
    {
        var providers = _runtime.GetProviderHealth().ToDictionary(provider => provider.ProviderId, StringComparer.OrdinalIgnoreCase);
        _openAiHealth.Text = providers.TryGetValue("openai", out var openAi) ? openAi.Status : "Недоступен";
        _geminiHealth.Text = providers.TryGetValue("gemini", out var gemini) ? gemini.Status : "Недоступен";
        _claudeHealth.Text = providers.TryGetValue("claude", out var claude) ? claude.Status : "Недоступен";
        _openAiHealth.ForeColor = openAi?.Configured == true ? Color.DarkGreen : Color.DimGray;
        _geminiHealth.ForeColor = gemini?.Configured == true ? Color.DarkGreen : Color.DimGray;
        _claudeHealth.ForeColor = claude?.Configured == true ? Color.DarkGreen : Color.DimGray;
    }

    private void UpdateApiState()
    {
        var running = _apiServer.IsRunning;
        _apiToggle.Text = running ? "Остановить локальный API" : "Запустить локальный API";
        _apiStatus.Text = running ? "API запущен" : "API выключен";
        _apiStatus.ForeColor = running ? Color.DarkGreen : Color.DimGray;
        _apiAddress.Text = running ? _apiServer.BaseAddress : "API выключен";
    }

    private void UpdateReviewButtons()
    {
        var approve = FindButton("approve");
        if (approve is not null) approve.Enabled = GetSelectedId(_reviewGrid) is not null;
        var skip = FindButton("skip");
        if (skip is not null) skip.Enabled = GetSelectedId(_reviewGrid) is not null;
        var revert = FindButton("revert");
        if (revert is not null)
        {
            var selected = _historyItems.FirstOrDefault(file => file.Id == GetSelectedId(_historyGrid));
            revert.Enabled = selected?.Status is nameof(FileStatus.Completed) or nameof(FileStatus.NeedsReview) or nameof(FileStatus.Quarantined);
        }
    }

    private void FindButtonByName(Control root, string name, ref Button? found)
    {
        foreach (Control child in root.Controls)
        {
            if (child is Button button && button.Name == name) { found = button; return; }
            FindButtonByName(child, name, ref found);
        }
    }

    private Button? FindButton(string name)
    {
        Button? found = null;
        FindButtonByName(this, name, ref found);
        return found;
    }

    private static string? GetSelectedId(DataGridView grid) =>
        grid.SelectedRows.Count == 0 ? null : grid.SelectedRows[0].Tag as string;

    private void AddWatchFolder() => _ = RunActionAsync(AddWatchFolderAsync);
    private void RemoveWatchFolder() => _ = RunActionAsync(RemoveWatchFolderAsync);
    private void ConfigureWatchExclusions() => _ = RunActionAsync(ShowWatchExclusionsAsync);

    private async Task ShowWatchExclusionsAsync()
    {
        using var dialog = new Form
        {
            Text = "Исключения наблюдаемых папок",
            StartPosition = FormStartPosition.CenterParent,
            MinimumSize = new Size(620, 430),
            Size = new Size(760, 520),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };
        var layout = NewLayout(1, 5);
        layout.Padding = new Padding(14);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        dialog.Controls.Add(layout);

        layout.Controls.Add(new Label { Text = "Исключённые пути — по одному на строку; исключаются файлы и вложенные папки.", Dock = DockStyle.Fill }, 0, 0);
        var paths = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Text = string.Join(Environment.NewLine, _runtime.Settings.ExcludedPaths)
        };
        layout.Controls.Add(paths, 0, 1);
        layout.Controls.Add(new Label { Text = "Маски имён — glob-шаблоны без пути, например *.tmp или secret-??.txt.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        var patterns = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Text = string.Join(Environment.NewLine, _runtime.Settings.ExcludedPatterns)
        };
        layout.Controls.Add(patterns, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var save = new Button { Text = "Сохранить", DialogResult = DialogResult.OK, Width = 110, Height = 32 };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Width = 110, Height = 32 };
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 4);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;

        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await _runtime.SaveWatchExclusionsAsync(paths.Lines, patterns.Lines);
        SetStatus("Исключения наблюдаемых папок сохранены.");
    }

    private async Task ShowCloudPolicyAsync()
    {
        var groups = new (string Caption, string[] Types)[]
        {
            ("Текст и программный код", ["text"]),
            ("PDF", ["pdf"]),
            ("Office Open XML", ["docx", "xlsx", "pptx"]),
            ("PNG, JPEG, WebP (визуальный анализ; исходное изображение)", ["png", "jpeg", "webp"]),
            ("Другие изображения (только извлечённый OCR и метаданные)", ["gif", "bmp", "tiff", "ico", "heif", "avif"]),
            ("Архивы (только список ZIP-содержимого)", ["zip"]),
            ("Аудио и видео (технические данные)", ["mp3", "wav", "ogg", "flac", "m4a", "mp4", "avi", "mkv"]),
            ("Исполняемые файлы (только метаданные)", ["executable"])
        };
        using var dialog = new Form
        {
            Text = "Политика облачного анализа",
            StartPosition = FormStartPosition.CenterParent,
            MinimumSize = new Size(640, 540),
            Size = new Size(760, 650),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };
        var layout = NewLayout(1, 5);
        layout.Padding = new Padding(14);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        dialog.Controls.Add(layout);
        layout.Controls.Add(new Label
        {
            Text = "Разрешённые типы могут отправлять извлечённый текст. Для PNG, JPEG и WebP визуальный анализ отправляет провайдеру само изображение.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var allowedTypes = new HashSet<string>(_runtime.Settings.AiAllowedFileTypes, StringComparer.OrdinalIgnoreCase);
        var checkboxes = new List<(CheckBox Checkbox, string[] Types)>();
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        foreach (var group in groups)
        {
            var checkbox = new CheckBox
            {
                Text = group.Caption,
                AutoSize = true,
                Checked = group.Types.Any(allowedTypes.Contains),
                Margin = new Padding(4, 5, 4, 5)
            };
            options.Controls.Add(checkbox);
            checkboxes.Add((checkbox, group.Types));
        }
        layout.Controls.Add(options, 0, 1);
        var allowImageAnalysis = new CheckBox
        {
            Text = "Разрешить передачу оригинальных PNG/JPEG/WebP (до 12 МБ) для визуального анализа",
            AutoSize = true,
            Checked = _runtime.Settings.AllowAiImageAnalysis,
            Margin = new Padding(4, 5, 4, 5)
        };
        layout.Controls.Add(allowImageAnalysis, 0, 2);
        layout.Controls.Add(new Label
        {
            Text = "Локальные приватные папки — по одной строке. Их файлы никогда не передаются облачному провайдеру.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 2);
        var privatePaths = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Text = string.Join(Environment.NewLine, _runtime.Settings.LocalOnlyPaths)
        };
        layout.Controls.Add(privatePaths, 0, 3);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var save = new Button { Text = "Сохранить", DialogResult = DialogResult.OK, Width = 110, Height = 32 };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Width = 110, Height = 32 };
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 4);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;

        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var selectedTypes = checkboxes.Where(item => item.Checkbox.Checked).SelectMany(item => item.Types).ToArray();
        await _runtime.SaveCloudPolicyAsync(selectedTypes, privatePaths.Lines, allowImageAnalysis.Checked);
        SetStatus("Политика облачного анализа сохранена.");
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { SetStatus(exception.Message); }
    }

    private void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        OpenPath(path);
    }

    private void HandleFileSorted(FileRecord file)
    {
        PostToUi(() =>
        {
            if (!_openDestinationAfterSort.Checked || file.Status != nameof(FileStatus.Completed)) return;
            var folder = Path.GetDirectoryName(file.CurrentPath);
            if (folder is null || !Directory.Exists(folder) || !FileSafety.IsWithin(folder, _runtime.Paths.Sorted) ||
                FileSafety.HasReparsePoint(folder) || !_openedSortedFolders.Add(Path.GetFullPath(folder)))
                return;
            OpenFolder(folder);
        });
    }

    private void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception exception) { SetStatus($"Не удалось открыть путь: {exception.Message}"); }
    }

    private void ShowFileLocation(string path)
    {
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add($"/select,{path}");
            Process.Start(start);
        }
        catch (Exception exception) { SetStatus($"Не удалось показать файл в папке: {exception.Message}"); }
    }

    private void HandleDragEnter(object? sender, DragEventArgs e) =>
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;

    private async void HandleDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files) return;
        await RunActionAsync(async () =>
        {
            var count = 0;
            foreach (var path in files)
            {
                if (!File.Exists(path)) continue;
                await _runtime.IngestFileAsync(path);
                count++;
            }
            SetStatus($"Перетащенных файлов добавлено: {count}.");
            await RefreshAllAsync();
        });
    }

    private void ConfigureTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть Optium+ File Manager", null, (_, _) => ShowFromTray());
        menu.Items.Add("Сортировать сейчас", null, async (_, _) => await RunActionAsync(SortNowAsync));
        menu.Items.Add("Выход", null, (_, _) => Close());
        _trayIcon.Icon = SystemIcons.Application;
        _trayIcon.Text = "Optium+ File Manager";
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private void HandleResize(object? sender, EventArgs e)
    {
        if (WindowState != FormWindowState.Minimized) return;
        Hide();
        ShowInTaskbar = false;
        _trayIcon.Visible = true;
        _trayIcon.ShowBalloonTip(1200, "Optium+ File Manager работает", "Нажмите значок в системном трее, чтобы открыть окно.", ToolTipIcon.Info);
    }

    private void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();
        _trayIcon.Visible = false;
    }

    private async void HandleFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        _allowClose = true;
        _startupCancellation.Cancel();
        _activeSearch?.Cancel();
        _refreshTimer.Stop();
        _trayIcon.Visible = false;
        await _apiServer.StopAsync();
        if (_startupTask is not null)
        {
            try { await _startupTask; }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }
        _runtime.FilesChanged -= QueueRefresh;
        _runtime.FileSorted -= HandleFileSorted;
        Close();
    }

    private void SetRuntimeReady(bool ready)
    {
        _pageHost.Enabled = ready;
        _globalSearch.Enabled = ready;
        _globalSearchButton.Enabled = ready;
        AllowDrop = ready;
        foreach (var button in _navigationButtons.Values)
            button.Enabled = ready;
    }

    private void QueueRefresh() => PostToUi(async () => await RefreshAllAsync());

    private void PostToUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (InvalidOperationException) { }
    }

    private void SetStatus(string message) => _status.Text = message;

    private void ApplyTheme(bool dark)
    {
        var background = dark ? Color.FromArgb(17, 25, 21) : Color.FromArgb(246, 248, 251);
        var surface = dark ? Color.FromArgb(27, 40, 33) : Color.White;
        var input = dark ? Color.FromArgb(19, 30, 24) : Color.White;
        var border = dark ? Color.FromArgb(57, 76, 63) : Color.FromArgb(218, 225, 233);
        var text = dark ? Color.FromArgb(226, 235, 228) : Color.FromArgb(28, 42, 63);
        var muted = dark ? Color.FromArgb(155, 174, 159) : Color.FromArgb(83, 97, 115);
        var accent = dark ? Color.FromArgb(91, 143, 106) : Color.FromArgb(45, 102, 177);
        BackColor = background;
        ForeColor = text;
        ApplyThemeToControl(this, dark, background, surface, input, border, text, muted);

        _sortNow.BackColor = accent;
        _sortNow.ForeColor = Color.White;
        _searchButton.BackColor = accent;
        _searchButton.ForeColor = Color.White;
        _globalSearchButton.BackColor = accent;
        _globalSearchButton.ForeColor = Color.White;
        _darkTheme.ForeColor = text;
        _status.ForeColor = muted;
        _sortedTreeStatus.ForeColor = muted;
        _pageHost.Invalidate();
        foreach (var button in _navigationButtons.Values) button.Invalidate();
    }

    private static void ApplyThemeToControl(Control control, bool dark, Color background, Color surface,
        Color input, Color border, Color text, Color muted)
    {
        if (control is Form or Panel or TableLayoutPanel or FlowLayoutPanel or SplitContainer)
            control.BackColor = control is Panel or TableLayoutPanel or FlowLayoutPanel or SplitContainer ? surface : background;

        if (control is Label label)
        {
            label.ForeColor = label.Font.Bold ? text : muted;
            label.BackColor = Color.Transparent;
        }
        else if (control is TextBoxBase textBox)
        {
            textBox.BackColor = input;
            textBox.ForeColor = text;
            textBox.BorderStyle = BorderStyle.FixedSingle;
        }
        else if (control is ComboBox comboBox)
        {
            comboBox.BackColor = input;
            comboBox.ForeColor = text;
        }
        else if (control is ListBox listBox)
        {
            listBox.BackColor = input;
            listBox.ForeColor = text;
        }
        else if (control is NumericUpDown numericUpDown)
        {
            numericUpDown.BackColor = input;
            numericUpDown.ForeColor = text;
        }
        else if (control is CheckBox checkBox)
        {
            checkBox.BackColor = surface;
            checkBox.ForeColor = text;
        }
        else if (control is Button button)
        {
            var accent = string.Equals(button.Tag?.ToString(), "accent", StringComparison.Ordinal);
            var navigation = button.Tag?.ToString() is "nav" or "nav-active";
            var navigationSelected = string.Equals(button.Tag?.ToString(), "nav-active", StringComparison.Ordinal);
            var shortcut = button.Tag is SortedFolderShortcut;
            var themeAccent = dark ? Color.FromArgb(91, 143, 106) : Color.FromArgb(45, 102, 177);
            button.BackColor = accent || navigationSelected ? themeAccent :
                shortcut ? (dark ? Color.FromArgb(35, 51, 41) : Color.White) :
                navigation ? (dark ? Color.FromArgb(22, 33, 27) : Color.White) :
                (dark ? Color.FromArgb(42, 58, 48) : Color.FromArgb(239, 243, 248));
            button.ForeColor = accent || navigationSelected ? Color.White : text;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = accent || navigationSelected ? themeAccent :
                shortcut && button.Tag is SortedFolderShortcut folderShortcut
                    ? (dark ? Color.FromArgb(65, 86, 70) : ControlPaint.Light(folderShortcut.Color)) : border;
            button.FlatAppearance.BorderSize = navigation ? 0 : button.FlatAppearance.BorderSize;
            button.FlatAppearance.MouseOverBackColor = navigationSelected || accent ? themeAccent : shortcut
                ? (dark ? Color.FromArgb(45, 65, 51) : Color.FromArgb(235, 242, 250))
                : navigation ? (dark ? Color.FromArgb(35, 51, 41) : Color.FromArgb(235, 242, 250))
                : (dark ? Color.FromArgb(57, 76, 63) : Color.FromArgb(229, 236, 245));
        }
        else if (control is TreeView tree)
        {
            tree.BackColor = input;
            tree.ForeColor = text;
            tree.LineColor = dark ? Color.FromArgb(108, 130, 114) : Color.FromArgb(148, 163, 184);
        }
        else if (control is DataGridView grid)
        {
            grid.BackgroundColor = surface;
            grid.GridColor = border;
            grid.DefaultCellStyle.BackColor = surface;
            grid.DefaultCellStyle.ForeColor = text;
            grid.DefaultCellStyle.SelectionBackColor = dark ? Color.FromArgb(53, 93, 64) : Color.FromArgb(220, 234, 251);
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.RowsDefaultCellStyle.BackColor = surface;
            grid.RowsDefaultCellStyle.ForeColor = text;
            grid.AlternatingRowsDefaultCellStyle.BackColor = dark ? Color.FromArgb(35, 51, 41) : Color.FromArgb(248, 250, 252);
            grid.ColumnHeadersDefaultCellStyle.BackColor = dark ? Color.FromArgb(42, 58, 48) : Color.FromArgb(241, 245, 249);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = text;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = text;
        }

        foreach (Control child in control.Controls)
            ApplyThemeToControl(child, dark, background, surface, input, border, text, muted);
    }

    private void CopyToken(string token)
    {
        Clipboard.SetText(token);
        SetStatus("Токен скопирован в буфер обмена.");
    }

    private static string CategoryName(string categoryId, string subcategoryId)
    {
        var choice = CategoryCatalog.AllChoices().FirstOrDefault(item => item.CategoryId == categoryId && item.SubcategoryId == subcategoryId);
        return choice.DisplayName ?? "Прочее";
    }

    private sealed record SortedFolderShortcut(string DisplayName, string Path, string Badge, Color Color);

    private static Panel CreateTab(string title) => new()
    {
        Name = title.Replace(' ', '_'),
        Padding = new Padding(14),
        BackColor = Color.White
    };

    private static TableLayoutPanel NewLayout(int columns, int rows) => new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = columns,
        RowCount = rows,
        Padding = new Padding(4),
        Margin = new Padding(0)
    };

    private static Label SectionTitle(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        Font = new Font("Segoe UI Semibold", 11, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(49, 62, 81)
    };

    private static Label CreateLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(64, 76, 94)
    };

    private Control CreateMetricCard(string key, string title, string value, Color accent)
    {
        var card = new Panel
        {
            Width = 240,
            Height = 94,
            BackColor = Color.White,
            Margin = new Padding(0, 0, 12, 0),
            Padding = new Padding(12),
            BorderStyle = BorderStyle.FixedSingle
        };
        var titleLabel = new Label { Text = title, AutoSize = true, ForeColor = Color.FromArgb(91, 103, 120), Location = new Point(13, 10) };
        var valueLabel = new Label { Text = value, AutoSize = true, Font = new Font("Segoe UI Semibold", 22, FontStyle.Bold), ForeColor = accent, Location = new Point(12, 34) };
        _metrics[key] = valueLabel;
        card.Controls.Add(titleLabel);
        card.Controls.Add(valueLabel);
        return card;
    }

    private static Button MakeButton(string text, Action action)
    {
        var button = CreateButton(text);
        button.Click += (_, _) => action();
        return button;
    }

    private Button MakeAsyncButton(string text, Func<Task> action)
    {
        var button = CreateButton(text);
        button.Click += async (_, _) => await RunActionAsync(action);
        return button;
    }

    private static Button CreateButton(string text)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 34,
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(8, 2, 8, 2),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(239, 243, 248),
            ForeColor = Color.FromArgb(44, 59, 79)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(218, 225, 233);
        return button;
    }

    private static void ConfigureGrid(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.ReadOnly = true;
        grid.MultiSelect = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoGenerateColumns = false;
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(57, 70, 89);
        grid.ColumnHeadersHeight = 34;
        grid.RowTemplate.Height = 31;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(220, 234, 251);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(34, 47, 65);
    }

    private static void AddTextColumn(DataGridView grid, string name, string header, int width)
    {
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = width,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            SortMode = DataGridViewColumnSortMode.Automatic
        });
    }

    private static Control BuildTokenRow(string label, TextBox token, Action copy)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
            AutoSize = false,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var labelControl = CreateLabel(label);
        labelControl.Margin = new Padding(0);
        row.Controls.Add(labelControl, 0, 0);
        token.Dock = DockStyle.Fill;
        token.Margin = new Padding(0, 4, 8, 4);
        token.ReadOnly = true;
        row.Controls.Add(token, 1, 0);
        var button = MakeButton("Копировать", copy);
        button.AutoSize = false;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0);
        row.Controls.Add(button, 2, 0);
        return row;
    }

    private void AddProviderRow(TableLayoutPanel layout, int row, string name, TextBox model, Label health)
    {
        layout.Controls.Add(CreateLabel(name), 0, row);
        model.Dock = DockStyle.Fill;
        model.Margin = new Padding(0, 2, 8, 2);
        layout.Controls.Add(model, 1, row);
        health.Dock = DockStyle.Fill;
        health.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(health, 2, row);
    }

    private sealed record CategoryChoice(string CategoryId, string SubcategoryId, string DisplayName)
    {
        public string Key => $"{CategoryId}/{SubcategoryId}";
    }

    private sealed record HistoryFilterChoice(string Value, string DisplayName);
}
