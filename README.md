# Game Profile Manager

Настольное приложение для Windows 11 x64 на C# / .NET 10 / WPF. Оно назначает играм общие профили FPS, G-SYNC и V-Sync, записывает параметры в RTSS и NVIDIA DRS и проверяет результат повторным чтением. Игры продолжают запускаться обычным способом через Steam, Epic Games Launcher или свой `.exe`.

Подробное описание функций, установки, резервного копирования и ограничений находится в [руководстве приложения](src/GameProfileManager/README.md). Результаты проверок на конкретном компьютере вынесены в [журнал проверок](docs/validation.md).

## Структура репозитория

Откройте `GameProfileManager.slnx` для работы со всеми проектами в одном решении.

| Каталог | Содержимое |
| --- | --- |
| `src/GameProfileManager/` | WPF-приложение, модели, хранение настроек и интеграции |
| `tests/GameProfileManager.Tests/` | Тесты логики без записи в RTSS/NVIDIA |
| `tests/GameProfileManager.IntegrationTests/` | Диагностика интеграций; без дополнительных флагов только чтение |
| `tests/GameProfileManager.UITests/` | Проверка интерфейса с временной конфигурацией и снимками экранов |
| `docs/` | Датированные результаты проверок |

DLL SDL3 и её лицензия находятся в `src/GameProfileManager/Native/`: DLL нужна для управления с контроллера и копируется рядом с приложением при сборке. Пользовательская база хранится в `%LOCALAPPDATA%\GameProfileManager\profiles.db` и в Git не входит.

## Архитектура синхронизации

`Application/SyncService.cs` выполняет применение, проверку и восстановление; он зависит от интерфейсов RTSS/NVIDIA и хранилища. `Infrastructure/SyncServiceFactory.cs` связывает реальные драйверы и источник дисплеев. `Application/SyncOperationCoordinator.cs` последовательно запускает внешние операции над отделённым `AppStateSnapshot`; после завершения `MainViewModel` переносит статусы и резервные данные в привязанные WPF-модели на UI-потоке. Сохранение конфигурации из интерфейса во время операции откладывается до её завершения.

`Application/DisplayChangeMonitor.cs` подтверждает изменение конфигурации экрана двумя одинаковыми наблюдениями. Чтение дисплеев и состояния процесса RTSS выполняется вне Dispatcher. Повторное применение уже подтверждённых значений не записывает RTSS/NVIDIA заново, если существует исходный бэкап приложения и параметры EXE-профиля совпадают при чтении.

`Core/Models.cs` не зависит от WPF. `Presentation/GameEntryViewModel.cs` адаптирует игровую запись для привязок, а `Presentation/GameIconLoader.cs` читает и декодирует изображения вне Dispatcher, ограничивает параллельную работу и хранит не более 64 изображений в LRU-кэше. `Application/AttentionService.cs` формирует причины раздела «Требуют внимания» из локального состояния без записи во внешние приложения.

`SqliteStateStore.Load()` различает отсутствие строки состояния и ошибки SQLite/JSON. Ошибка чтения прерывает запуск с сообщением о пути к базе; приложение не сохраняет пустую конфигурацию поверх существующего файла. `SettingBackupOwnership` сопоставляет резервные значения по ID игры и допускает старые записи без ID только при однозначном совпадении пути.

Steam/Epic discovery читает каталоги в фоновом потоке; объединение найденных игр с наблюдаемой коллекцией и сохранение SQLite выполняются после возврата на UI-поток. Во время импорта команды редактирования временно блокируются.

## Сборка и проверка

Установите .NET SDK 10 и выполняйте команды из корня репозитория на Windows 11:

```powershell
dotnet build .\src\GameProfileManager\GameProfileManager.csproj -c Release
dotnet run --project .\tests\GameProfileManager.Tests\GameProfileManager.Tests.csproj -c Release
dotnet run --project .\tests\GameProfileManager.IntegrationTests\GameProfileManager.IntegrationTests.csproj -c Release

$smokeOutput = Join-Path .\artifacts ("ui-smoke-" + [guid]::NewGuid().ToString("N"))
dotnet run --project .\tests\GameProfileManager.UITests\GameProfileManager.UITests.csproj -c Release -- $smokeOutput
```

UI smoke открывает окно приложения и создаёт временную базу и PNG в переданном каталоге. Каждый запуск используйте с новым каталогом. Флаги интеграционных тестов `--write-test`, `--nvidia-write-test` и `--sync-test` меняют временные профили RTSS/NVIDIA; для обычной проверки они не нужны.

## Автономная сборка

```powershell
dotnet publish .\src\GameProfileManager\GameProfileManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish\win-x64
```

Распространяйте **весь** каталог `publish\win-x64`: помимо EXE в нём находятся необходимые DLL и лицензия SDL3. Готовые сборки, архивы, результаты тестов и локальные данные исключены через `.gitignore`; этот репозиторий содержит исходники.
