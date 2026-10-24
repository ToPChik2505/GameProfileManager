using GameProfileManager.Application;
using GameProfileManager.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameProfileManager;

internal static partial class PageFactory
{
    private sealed record SnapshotChoice(Guid? SnapshotId, string Label);
    private sealed record SourceChoice(GameConfigSource Source, string Label);

    private static PageComposition Graphics(RouteRequest request)
    {
        var vm = Vm(request);
        var game = Game(request);
        if (game is null) return Shell("Графические файлы", "Сначала выберите игру.");

        var sources = new ListView { MaxHeight = 180, SelectionMode = ListViewSelectionMode.Single };
        var snapshots = new ListView { MaxHeight = 180, SelectionMode = ListViewSelectionMode.Single,
            DisplayMemberPath = nameof(GameConfigSnapshot.Name) };
        var snapshotDetails = new StackPanel { Spacing = 5 };
        var assignmentRows = new StackPanel { Spacing = 10 };
        var processRows = new StackPanel { Spacing = 5 };
        var safetyRows = new StackPanel { Spacing = 5 };
        Button? restore = null;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var statusDetail = Secondary("");
        var target = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var autoApply = Toggle("Автоматически применять графические файлы по эффективному дисплею",
            game.GameConfigSettings.AutoApply, value => vm.SetGameConfigAutoApply(game, value), () => game.GameConfigSettings.AutoApply);
        var setupHint = Secondary("Сначала найдите файлы на компьютере. GPM не меняет их, пока вы не сохраните и не примените снимок.");
        var emptySnapshots = Secondary("Снимков пока нет. Сохраните текущие настройки как первый вариант.");
        var searchProgressRing = new ProgressRing { Width = 20, Height = 20, IsActive = false, VerticalAlignment = VerticalAlignment.Center };
        var searchProgressText = Secondary("");
        var searchProgress = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Visibility = Visibility.Collapsed };
        searchProgress.Children.Add(searchProgressRing);
        searchProgress.Children.Add(searchProgressText);
        bool isSearchingForConfig = false;
        Button? localSearchButton = null;
        MenuFlyoutItem? onlineSearchItem = null;
        var sourceMenu = new MenuFlyout();
        var addSourceButton = new Button { Content = "Другие способы…", Flyout = sourceMenu, MinHeight = 40 };
        Action refreshSections = () => { };
        MenuFlyoutItem AddSourceMenuItem(string label, Action action, bool hidden = false)
        {
            var item = new MenuFlyoutItem { Text = label,
                Visibility = hidden ? Visibility.Collapsed : Visibility.Visible };
            item.Click += (_, _) => action();
            sourceMenu.Items.Add(item);
            return item;
        }

        void SetSearchProgress(string? message)
        {
            isSearchingForConfig = !string.IsNullOrWhiteSpace(message);
            searchProgressText.Text = message ?? "";
            searchProgressRing.IsActive = isSearchingForConfig;
            searchProgress.Visibility = isSearchingForConfig ? Visibility.Visible : Visibility.Collapsed;
            if (localSearchButton is not null) localSearchButton.IsEnabled = !isSearchingForConfig;
            addSourceButton.IsEnabled = !isSearchingForConfig;
            if (onlineSearchItem is not null) onlineSearchItem.IsEnabled = !isSearchingForConfig;
        }

        void RefreshSources()
        {
            var entries = game.GameConfigSettings.Sources.Select(source => new SourceChoice(source,
                source.Kind == GameConfigSourceKind.File ? source.TargetPath : source.TargetPath + "\\" + source.RegistryValueName)).ToArray();
            sources.ItemsSource = entries;
            sources.DisplayMemberPath = "Label";
            sources.Visibility = entries.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void RefreshSnapshots()
        {
            var entries = vm.GameConfigSnapshots(game.Id);
            snapshots.ItemsSource = entries;
            snapshots.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            emptySnapshots.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshSnapshotDetails();
        }

        void RefreshSnapshotDetails()
        {
            snapshotDetails.Children.Clear();
            if (snapshots.SelectedItem is not GameConfigSnapshot selected)
            {
                snapshotDetails.Visibility = Visibility.Collapsed;
                return;
            }
            snapshotDetails.Visibility = Visibility.Visible;
            foreach (var item in selected.Items)
            {
                string sourceName = game.GameConfigSettings.Sources.FirstOrDefault(source => source.Id == item.SourceId) is { } source
                    ? source.Kind == GameConfigSourceKind.File ? source.TargetPath : source.TargetPath + "\\" + source.RegistryValueName
                    : item.SourceId.ToString("N");
                snapshotDetails.Children.Add(Secondary($"{sourceName} · {item.Size:N0} байт · SHA-256 {item.Sha256}"));
            }
        }

        void RefreshAssignments()
        {
            assignmentRows.Children.Clear();
            var assignmentIds = vm.GameConfigAssignments(game.Id).Select(item => item.DisplayId);
            var displayIds = vm.Displays.Select(item => item.Id).Concat(assignmentIds)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string displayId in displayIds)
            {
                var choices = new List<SnapshotChoice> { new(null, "Не назначено — не менять файлы") };
                choices.AddRange(vm.GameConfigSnapshots(game.Id).Select(snapshot => new SnapshotChoice(snapshot.Id, snapshot.Name)));
                var row = new StackPanel { Spacing = 5 };
                row.Children.Add(Strong(vm.GameConfigDisplayLabel(displayId)));
                var combo = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(SnapshotChoice.Label),
                    MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
                var assignment = vm.GameConfigAssignments(game.Id).FirstOrDefault(item =>
                    item.DisplayId.Equals(displayId, StringComparison.OrdinalIgnoreCase));
                combo.SelectedItem = choices.FirstOrDefault(choice => choice.SnapshotId == assignment?.SnapshotId) ?? choices[0];
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is SnapshotChoice choice)
                    {
                        try { vm.SetGameConfigAssignment(game, displayId, choice.SnapshotId); }
                        catch (Exception ex) { vm.Notice = ex.Message; }
                    }
                };
                row.Children.Add(combo);
                assignmentRows.Children.Add(row);
            }
            if (displayIds.Length == 0) assignmentRows.Children.Add(Secondary("Подключённые дисплеи не обнаружены. Назначения появятся после их обнаружения."));
        }

        void RefreshProcesses()
        {
            processRows.Children.Clear();
            foreach (string path in game.GameConfigSettings.AdditionalProcessPaths.ToArray())
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(ActionButton("Убрать", () => { vm.RemoveGameConfigProcessPath(game, path); RefreshProcesses(); }));
                processRows.Children.Add(row);
            }
        }

        void RefreshStatus()
        {
            status.Text = MainViewModel.GameConfigStatusLabel(game.GameConfigSettings.Status);
            bool hasProblem = game.GameConfigSettings.Status is GameConfigApplyStatus.Error or
                GameConfigApplyStatus.Conflict or GameConfigApplyStatus.RecoveryRequired;
            statusDetail.Text = hasProblem ? game.GameConfigSettings.StatusDetail : "";
            statusDetail.Visibility = hasProblem && !string.IsNullOrWhiteSpace(game.GameConfigSettings.StatusDetail)
                ? Visibility.Visible : Visibility.Collapsed;
            var desired = vm.ResolveGameConfigTarget(game);
            target.Text = desired.SnapshotId is { } id
                ? $"Цель: {vm.GameConfigSnapshots(game.Id).FirstOrDefault(item => item.Id == id)?.Name ?? "не найден"} → {vm.GameConfigDisplayLabel(desired.DisplayId)}"
                : "Цель не назначена";
            if (restore is not null)
            {
                restore.IsEnabled = vm.HasOriginalGameConfigBackups(game.Id);
                restore.Visibility = restore.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            }
            safetyRows.Children.Clear();
            foreach (var copy in vm.GameConfigSafetyCopies(game.Id).Take(8))
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var details = Secondary($"{copy.CreatedAt.ToLocalTime():g} · {copy.Reason} · SHA-256 {copy.Sha256}");
                Grid.SetColumn(details, 0);
                row.Children.Add(details);
                var preview = ActionButton("Просмотреть", async () =>
                {
                    try
                    {
                        var content = new TextBox
                        {
                            Text = vm.GameConfigSafetyCopyPreview(copy), IsReadOnly = true,
                            AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
                            MinWidth = 380, MinHeight = 240, MaxHeight = 520,
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
                        };
                        var dialog = new ContentDialog
                        {
                            XamlRoot = request.Owner.Content.XamlRoot, Title = "Защитная копия",
                            Content = new ScrollViewer { Content = content, MaxHeight = 560 },
                            CloseButtonText = "Закрыть", DefaultButton = ContentDialogButton.Close
                        };
                        await request.Owner.ShowDialogAsync(dialog);
                    }
                    catch (Exception ex) { vm.Notice = "Не удалось прочитать защитную копию: " + ex.Message; }
                });
                Grid.SetColumn(preview, 1);
                row.Children.Add(preview);
                safetyRows.Children.Add(row);
            }
        }

        async Task CreateSnapshot(string name)
        {
            await vm.CreateGameConfigSnapshotAsync(game, name);
            RefreshSnapshots(); RefreshAssignments(); RefreshStatus(); refreshSections();
        }

        async Task AddCandidates(IReadOnlyList<GameConfigCandidate> found, string title)
        {
            if (found.Count == 0)
            {
                vm.Notice = title.Contains("PCGamingWiki", StringComparison.OrdinalIgnoreCase)
                    ? vm.PcgamingWikiCredentialsConfigured
                        ? $"PCGamingWiki не вернул Windows-путь через Cargo GameData или раздел конфигурации статьи для «{game.Name}»."
                        : $"PCGamingWiki не нашёл пути для «{game.Name}». Точный поиск настраивается в «Настройки → Точный поиск PCGamingWiki»."
                    : "Локальный поиск не нашёл конфигурации. Убедитесь, что игра уже создала настройки, или выберите файл вручную.";
                return;
            }
            var content = new StackPanel { Spacing = 8, MaxWidth = 680 };
            var checks = new List<(GameConfigCandidate Candidate, CheckBox Check)>();
            content.Children.Add(Secondary("Проверьте имя и источник. Для реестра будут добавлены только отмеченные точные значения."));
            foreach (var candidate in found.OrderBy(item => item.Priority)
                         .ThenBy(item => item.SourceKind == GameConfigSourceKind.Registry ? item.RegistryValueName : Path.GetFileName(item.Path),
                             StringComparer.OrdinalIgnoreCase))
            {
                var details = new StackPanel { Spacing = 2, MaxWidth = 620 };
                string candidateName = candidate.SourceKind == GameConfigSourceKind.Registry
                    ? candidate.RegistryValueName : Path.GetFileName(candidate.Path);
                string candidateLocation = candidate.SourceKind == GameConfigSourceKind.Registry
                    ? candidate.Path + "\\" + candidate.RegistryValueName : candidate.Path;
                details.Children.Add(new TextBlock { Text = candidateName, FontSize = 15,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                if (candidate.SourceKind == GameConfigSourceKind.Registry)
                    details.Children.Add(new TextBlock { Text = candidate.Description, FontSize = 12, Opacity = 0.78,
                        TextWrapping = TextWrapping.Wrap });
                else if (!string.IsNullOrWhiteSpace(candidate.Description))
                    details.Children.Add(new TextBlock { Text = candidate.Description, FontSize = 12, Opacity = 0.78,
                        TextWrapping = TextWrapping.Wrap });
                details.Children.Add(new TextBlock { Text = candidateLocation, FontSize = 12, Opacity = 0.78, TextWrapping = TextWrapping.Wrap });
                var check = new CheckBox
                {
                    Content = details, IsChecked = candidate.IsRecommended,
                    HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 660
                };
                checks.Add((candidate, check));
                content.Children.Add(check);
            }
            var dialog = new ContentDialog
            {
                XamlRoot = request.Owner.Content.XamlRoot, Title = title,
                Content = new ScrollViewer { Content = content, MaxHeight = 440 },
                PrimaryButtonText = "Подтвердить выбранные", CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Close
            };
            if (await request.Owner.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
            try
            {
                vm.AddGameConfigCandidates(game, checks.Where(item => item.Check.IsChecked == true)
                    .Select(item => item.Candidate));
                RefreshSources(); RefreshStatus(); refreshSections();
            }
            catch (Exception ex) { vm.Notice = "Источники не добавлены: " + ex.Message; }
        }

        async Task AddDiscoveredCandidates()
        {
            if (isSearchingForConfig) return;
            SetSearchProgress("Ищем файлы настроек на компьютере…");
            try
            {
                await Task.Delay(100);
                var found = await vm.FindGameConfigCandidatesAsync(game);
                SetSearchProgress(null);
                await AddCandidates(found, "Найденные локальные кандидаты");
            }
            catch (Exception ex) { vm.Notice = "Локальный поиск не завершён: " + ex.Message; }
            finally { SetSearchProgress(null); }
        }

        async Task AddOnlineDiscoveredCandidates()
        {
            if (isSearchingForConfig) return;
            if (!await Confirm(request.Owner, "Искать пути в PCGamingWiki",
                $"Найти конфигурационные пути для «{game.Name}». Файлы не изменятся без вашего выбора и подтверждения.",
                "Искать онлайн")) return;
            if (isSearchingForConfig) return;
            SetSearchProgress("Ищем пути конфигурации в PCGamingWiki…");
            try
            {
                await Task.Delay(100);
                var found = await vm.FindOnlineGameConfigCandidatesAsync(game);
                SetSearchProgress(null);
                await AddCandidates(found, "Кандидаты PCGamingWiki");
            }
            catch (Exception ex) { vm.Notice = "Онлайн-поиск не завершён: " + ex.Message; }
            finally { SetSearchProgress(null); }
        }

        async Task AddManualFiles()
        {
            string? initial = Path.GetDirectoryName(game.ExePath);
            IReadOnlyList<string> paths = await (vm.PickLocalFilesAsync?.Invoke(
                "Выберите файлы графических настроек", "*", Directory.Exists(initial) ? initial : game.InstallDirectory)
                ?? Task.FromResult<IReadOnlyList<string>>([]));
            if (paths.Count == 0) return;
            var binaries = paths.Where(path => GameConfigPathSafety.IsLikelyBinaryExtension(Path.GetExtension(path))).ToArray();
            bool allowBinary = false;
            if (binaries.Length > 0)
            {
                allowBinary = await Confirm(request.Owner, "Бинарные файлы графики",
                    "В выбранном наборе есть файлы неизвестного бинарного формата. Их содержимое будет сохраняться побайтово без проверки безопасности формата. Продолжить для всех выбранных файлов?\n\n" +
                    string.Join("\n", binaries), "Добавить с предупреждением");
                if (!allowBinary) paths = paths.Except(binaries, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            try
            {
                vm.AddGameConfigFileSources(game, paths.Select(path => (path, "manual", allowBinary)));
                RefreshSources(); RefreshStatus(); refreshSections();
            }
            catch (Exception ex) { vm.Notice = "Файлы не добавлены: " + ex.Message; }
        }

        async Task AddRegistryValue()
        {
            var key = new TextBox { PlaceholderText = @"HKCU\Software\Vendor\Game" };
            var value = new TextBox { PlaceholderText = "Имя точного значения" };
            var body = Column(
                Message("Только выбранное значение HKCU", "Будет изменяться одно явно указанное значение в HKEY_CURRENT_USER\\Software. HKLM/HKCR/HKU и импорт .reg запрещены."),
                key, value);
            var dialog = new ContentDialog
            {
                XamlRoot = request.Owner.Content.XamlRoot, Title = "Добавить значение реестра",
                Content = body, PrimaryButtonText = "Добавить точное значение", CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Close
            };
            if (await request.Owner.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
            try { vm.AddGameConfigRegistrySource(game, key.Text, value.Text); RefreshSources(); RefreshStatus(); refreshSections(); }
            catch (Exception ex) { vm.Notice = "Значение не добавлено: " + ex.Message; }
        }

        Button? removeSource = null;
        removeSource = ActionButton("Удалить выбранный источник", () =>
        {
            if (sources.SelectedItem is not SourceChoice selected) return;
            try { vm.RemoveGameConfigSource(game, selected.Source.Id); sources.SelectedItem = null; if (removeSource is not null) removeSource.IsEnabled = false; RefreshSources(); RefreshStatus(); refreshSections(); }
            catch (Exception ex) { vm.Notice = "Источник не удалён: " + ex.Message; }
        }, enabled: false, destructive: true);
        Button? editSource = null;
        editSource = ActionButton("Изменить путь выбранного источника", async () =>
        {
            if (sources.SelectedItem is not SourceChoice selected) return;
            if (selected.Source.Kind == GameConfigSourceKind.File)
            {
                string? currentDirectory = Path.GetDirectoryName(selected.Source.TargetPath);
                string? initial = Directory.Exists(currentDirectory) ? currentDirectory : game.InstallDirectory;
                if (vm.PickLocalFileAsync is null) return;
                string? path = await vm.PickLocalFileAsync("Выберите новый файл настроек", "*", initial);
                if (path is null) return;
                bool binary = GameConfigPathSafety.IsLikelyBinaryExtension(Path.GetExtension(path));
                bool allowBinary = !binary || await Confirm(request.Owner, "Бинарная конфигурация",
                    "Формат файла не распознаётся как текстовый. GPM сохранит его побайтно без анализа формата.", "Разрешить файл");
                if (!allowBinary) return;
                if (!await Confirm(request.Owner, "Изменить путь источника",
                    "Исходные копии старого пути останутся доступны. Автоматическое применение будет выключено; новый путь начнёт изменяться только после отдельного применения снимка.", "Изменить путь")) return;
                try { vm.EditGameConfigFileSource(game, selected.Source.Id, path, allowBinary); RefreshSources(); RefreshStatus(); refreshSections(); }
                catch (Exception ex) { vm.Notice = "Путь не изменён: " + ex.Message; }
                return;
            }

            var key = new TextBox { Text = selected.Source.TargetPath, PlaceholderText = @"HKCU\Software\Vendor\Game" };
            var value = new TextBox { Text = selected.Source.RegistryValueName, PlaceholderText = "Имя точного значения" };
            var dialog = new ContentDialog
            {
                XamlRoot = request.Owner.Content.XamlRoot, Title = "Изменить значение HKCU",
                Content = Column(key, value), PrimaryButtonText = "Продолжить", CloseButtonText = "Отмена"
            };
            if (await request.Owner.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
            if (!await Confirm(request.Owner, "Изменить путь источника",
                "Исходная копия старого значения останется доступна. Автоматическое применение выключится; новое значение изменится только после отдельного применения снимка.", "Изменить источник")) return;
            try { vm.EditGameConfigRegistrySource(game, selected.Source.Id, key.Text, value.Text); RefreshSources(); RefreshStatus(); refreshSections(); }
            catch (Exception ex) { vm.Notice = "Путь не изменён: " + ex.Message; }
        }, enabled: false);
        sources.SelectionChanged += (_, _) =>
        {
            bool hasSelection = sources.SelectedItem is SourceChoice;
            removeSource.IsEnabled = editSource.IsEnabled = hasSelection;
            refreshSections();
        };

        var snapshotsButton = ActionButton("Сохранить текущие настройки как снимок", async () =>
        {
            var name = new TextBox { PlaceholderText = "Например, 4K TV" };
            var dialog = new ContentDialog
            {
                XamlRoot = request.Owner.Content.XamlRoot, Title = "Новый снимок графики", Content = name,
                PrimaryButtonText = "Сохранить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Primary
            };
            if (await request.Owner.ShowDialogAsync(dialog) == ContentDialogResult.Primary)
                await CreateSnapshot(name.Text);
        }, accent: true);

        async Task RenameSelectedSnapshot()
        {
            if (snapshots.SelectedItem is not GameConfigSnapshot selected) return;
            var name = new TextBox { Text = selected.Name };
            var dialog = new ContentDialog { XamlRoot = request.Owner.Content.XamlRoot, Title = "Переименовать снимок",
                Content = name, PrimaryButtonText = "Сохранить", CloseButtonText = "Отмена" };
            if (await request.Owner.ShowDialogAsync(dialog) == ContentDialogResult.Primary)
            {
                try { vm.RenameGameConfigSnapshot(game, selected.Id, name.Text); RefreshSnapshots(); RefreshAssignments(); refreshSections(); }
                catch (Exception ex) { vm.Notice = ex.Message; }
            }
        }

        async Task SaveSnapshotRevision()
        {
            if (snapshots.SelectedItem is GameConfigSnapshot selected)
                await CreateSnapshot(selected.Name + " · ревизия " + DateTime.Now.ToString("yyyy-MM-dd HH-mm"));
        }

        async Task DeleteSelectedSnapshot()
        {
            if (snapshots.SelectedItem is not GameConfigSnapshot selected) return;
            if (!await Confirm(request.Owner, "Удалить снимок", "Удалить выбранный снимок? Назначенный снимок удалять нельзя.", "Удалить")) return;
            try { vm.DeleteGameConfigSnapshot(game, selected.Id); RefreshSnapshots(); RefreshAssignments(); RefreshStatus(); refreshSections(); }
            catch (Exception ex) { vm.Notice = ex.Message; }
        }

        var snapshotMenu = new MenuFlyout();
        var renameSnapshotItem = new MenuFlyoutItem { Text = "Переименовать" };
        renameSnapshotItem.Click += async (_, _) => await RenameSelectedSnapshot();
        var revisionItem = new MenuFlyoutItem { Text = "Сохранить новую ревизию" };
        revisionItem.Click += async (_, _) => await SaveSnapshotRevision();
        var deleteSnapshotItem = new MenuFlyoutItem { Text = "Удалить снимок" };
        deleteSnapshotItem.Click += async (_, _) => await DeleteSelectedSnapshot();
        snapshotMenu.Items.Add(renameSnapshotItem);
        snapshotMenu.Items.Add(revisionItem);
        snapshotMenu.Items.Add(deleteSnapshotItem);
        var snapshotActionsButton = new Button { Content = "Действия со снимком…", Flyout = snapshotMenu,
            MinHeight = 40, IsEnabled = false };

        var apply = ActionButton("Применить выбранный снимок сейчас", async () =>
        {
            if (snapshots.SelectedItem is not GameConfigSnapshot selected) return;
            if (!await Confirm(request.Owner, "Применить графическую конфигурацию",
                "GPM создаст защитные копии и заменит выбранные файлы, пока игра закрыта. Если рабочие файлы изменились после прошлого применения, это действие явно разрешит их перезапись.", "Защитить и применить")) return;
            await vm.ApplyGameConfigSnapshotAsync(game, selected.Id, overrideConflict: true);
            RefreshStatus();
            if (restore is not null) restore.IsEnabled = vm.HasOriginalGameConfigBackups(game.Id);
            refreshSections();
        }, accent: true, enabled: false);

        restore = ActionButton("Восстановить исходные файлы", async () =>
        {
            if (!await Confirm(request.Owner, "Восстановить исходные графические настройки",
                "Будут восстановлены значения, существовавшие до первой записи GPM. Текущие файлы сохранятся в защитной копии. При конфликте это подтверждение разрешит восстановление поверх отличающихся данных.", "Сохранить копию и восстановить")) return;
            await vm.RestoreOriginalGameConfigsAsync(game, overrideConflict: true);
            RefreshStatus();
        }, destructive: true, enabled: vm.HasOriginalGameConfigBackups(game.Id));

        snapshots.SelectionChanged += (_, _) =>
        {
            bool selected = snapshots.SelectedItem is GameConfigSnapshot;
            snapshotActionsButton.IsEnabled = apply.IsEnabled = selected;
            RefreshSnapshotDetails();
            refreshSections();
        };

        var recoveryStatus = Secondary("");
        var recovery = ActionButton("Восстановить незавершённую операцию", async () =>
        {
            var journal = vm.IncompleteGameConfigTransactions.FirstOrDefault();
            if (journal is null) return;
            if (!await Confirm(request.Owner, "Восстановить операцию",
                "GPM проверит журнал и вернёт только те файлы, которые всё ещё совпадают с состоянием транзакции. Неожиданно изменённые файлы останутся нетронутыми.", "Восстановить")) return;
            await vm.RecoverGameConfigTransactionAsync(journal);
            RefreshStatus();
        }, destructive: true);

        var eventContent = Column();
        var eventCard = Card(eventContent);
        void RefreshEventCard()
        {
            eventContent.Children.Clear();
            int incomplete = vm.IncompleteGameConfigTransactions.Count;
            var events = game.GameConfigSettings.Events.OrderByDescending(item => item.At).Take(3).ToArray();
            int orphanCount = vm.OrphanGameConfigSnapshotCount;
            if (incomplete > 0)
            {
                eventContent.Children.Add(Strong("Нужно восстановить операцию"));
                eventContent.Children.Add(recoveryStatus);
                eventContent.Children.Add(recovery);
            }
            if (events.Length > 0)
            {
                eventContent.Children.Add(Strong("Последние события"));
                foreach (var item in events)
                    eventContent.Children.Add(Secondary($"{item.At.ToLocalTime():g} · {MainViewModel.GameConfigStatusLabel(item.Status)} · {item.Message}"));
            }
            if (orphanCount > 0)
                eventContent.Children.Add(Secondary($"Найдены неиспользуемые или промежуточные снимки: {orphanCount}. Они сохранены без изменений."));
            eventCard.Visibility = incomplete > 0 || events.Length > 0 || orphanCount > 0
                ? Visibility.Visible : Visibility.Collapsed;
        }

        void RefreshRecovery()
        {
            int incomplete = vm.IncompleteGameConfigTransactions.Count;
            recovery.IsEnabled = incomplete > 0;
            recovery.Visibility = incomplete > 0 ? Visibility.Visible : Visibility.Collapsed;
            recoveryStatus.Text = incomplete == 1 ? "Одна операция не завершилась." : $"Не завершено операций: {incomplete}.";
            RefreshEventCard();
        }

        async Task AddProcess()
        {
            if (vm.PickLocalFileAsync is null) return;
            string? path = await vm.PickLocalFileAsync("Выберите дополнительный процесс игры", "*.exe", game.InstallDirectory);
            if (path is null) return;
            try { vm.AddGameConfigProcessPath(game, path); RefreshProcesses(); refreshSections(); }
            catch (Exception ex) { vm.Notice = ex.Message; }
        }

        onlineSearchItem = AddSourceMenuItem("Искать в PCGamingWiki…", () => _ = AddOnlineDiscoveredCandidates());
        AddSourceMenuItem("Выбрать файлы вручную…", () => _ = AddManualFiles());
        AddSourceMenuItem("Добавить значение реестра HKCU…", () => _ = AddRegistryValue(), hidden: true);
        AddSourceMenuItem("Настроить точный поиск PCGamingWiki…", () => request.Owner.Navigate("settings-pcgamingwiki"),
            hidden: vm.PcgamingWikiCredentialsConfigured);
        AddSourceMenuItem("Добавить связанный процесс…", () => _ = AddProcess(), hidden: true);
        var localSearchAction = ActionButton("Найти файлы на компьютере", () => _ = AddDiscoveredCandidates(), true);
        localSearchButton = localSearchAction;
        var sourceActions = Buttons(localSearchAction, addSourceButton);

        var sourceSelectionActions = Buttons(editSource, removeSource);
        var sourceCard = Card(Column(Strong("Файлы игры"), setupHint, searchProgress, sources, sourceActions, sourceSelectionActions));
        var stateCard = Card(Column(Strong("Применение"), status, statusDetail, target, autoApply));
        var snapshotsCard = Card(Column(Strong("Снимки графики"), emptySnapshots, snapshots, snapshotDetails,
            snapshotsButton, snapshotActionsButton, apply));
        var assignmentsCard = Card(Column(Strong("Назначения по дисплеям"),
            Secondary("Для каждого экрана выберите снимок. Пустое назначение оставляет файлы без изменений."), assignmentRows));
        var originalsCard = Card(Column(Strong("Исходные файлы и защитные копии"), restore, safetyRows));
        var processesCard = Card(Column(Strong("Связанные процессы"),
            Secondary("GPM дождётся завершения этих процессов перед заменой файлов."), processRows));
        var items = new List<UIElement>
        {
            GameHeader(request, game), sourceCard, stateCard, snapshotsCard, assignmentsCard,
            originalsCard, processesCard, eventCard
        };
        var composition = Shell("Графические файлы", "Сохраните варианты настроек и назначьте их экранам.", items.ToArray());
        if (composition.Content is ScrollViewer { Content: StackPanel layout }) layout.Spacing = 12;

        void RefreshSectionsVisibility()
        {
            bool hasSources = game.GameConfigSettings.Sources.Count > 0;
            bool hasSnapshots = vm.GameConfigSnapshots(game.Id).Count > 0;
            bool hasSafetyCopies = vm.GameConfigSafetyCopies(game.Id).Count > 0;
            bool selectedSource = sources.SelectedItem is SourceChoice;
            bool selectedSnapshot = snapshots.SelectedItem is GameConfigSnapshot;
            setupHint.Visibility = hasSources ? Visibility.Collapsed : Visibility.Visible;
            sourceSelectionActions.Visibility = selectedSource ? Visibility.Visible : Visibility.Collapsed;
            stateCard.Visibility = hasSources ? Visibility.Visible : Visibility.Collapsed;
            snapshotsCard.Visibility = hasSources ? Visibility.Visible : Visibility.Collapsed;
            assignmentsCard.Visibility = hasSources && hasSnapshots ? Visibility.Visible : Visibility.Collapsed;
            originalsCard.Visibility = vm.HasOriginalGameConfigBackups(game.Id) || hasSafetyCopies
                ? Visibility.Visible : Visibility.Collapsed;
            processesCard.Visibility = game.GameConfigSettings.AdditionalProcessPaths.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            snapshotActionsButton.Visibility = selectedSnapshot ? Visibility.Visible : Visibility.Collapsed;
            apply.Visibility = selectedSnapshot ? Visibility.Visible : Visibility.Collapsed;
        }

        refreshSections = RefreshSectionsVisibility;
        RefreshSources(); RefreshSnapshots(); RefreshAssignments(); RefreshProcesses(); RefreshStatus(); RefreshRecovery();
        RefreshSectionsVisibility();
        void RefreshPage()
        {
            Guid? sourceId = (sources.SelectedItem as SourceChoice)?.Source.Id;
            Guid? snapshotId = (snapshots.SelectedItem as GameConfigSnapshot)?.Id;
            RefreshSources();
            sources.SelectedItem = sources.Items.Cast<SourceChoice>().FirstOrDefault(item => item.Source.Id == sourceId);
            if (removeSource is not null) removeSource.IsEnabled = sources.SelectedItem is SourceChoice;
            if (editSource is not null) editSource.IsEnabled = sources.SelectedItem is SourceChoice;
            RefreshSnapshots();
            snapshots.SelectedItem = snapshots.Items.Cast<GameConfigSnapshot>().FirstOrDefault(item => item.Id == snapshotId);
            RefreshAssignments(); RefreshProcesses(); RefreshStatus(); RefreshRecovery(); RefreshSectionsVisibility();
        }
        System.ComponentModel.PropertyChangedEventHandler stateChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.GameConfigStateVersion)) RefreshPage();
        };
        vm.PropertyChanged += stateChanged;
        if (composition.Content is FrameworkElement pageRoot)
            pageRoot.Unloaded += (_, _) => vm.PropertyChanged -= stateChanged;
        return composition;
    }
}
