# Game Profile Manager

Настольное приложение для Windows 11 x64 на C# / .NET 10 / WPF. Оно назначает играм общие профили FPS, G-SYNC и V-Sync, записывает параметры в RTSS и NVIDIA DRS и проверяет результат повторным чтением. Игры продолжают запускаться обычным способом через Steam, Epic Games Launcher или свой `.exe`.

Подробное описание функций, установки, резервного копирования и ограничений находится в [руководстве приложения](GameProfileManager/README.md). Результаты проверок на конкретном компьютере вынесены в [журнал проверок](docs/validation.md).

## Структура репозитория

| Каталог | Содержимое |
| --- | --- |
| `GameProfileManager/` | WPF-приложение, модели, хранение настроек и интеграции |
| `GameProfileManager.Tests/` | Тесты логики без записи в RTSS/NVIDIA |
| `GameProfileManager.IntegrationTests/` | Диагностика интеграций; без дополнительных флагов только чтение |
| `GameProfileManager.UiSmoke/` | Проверка интерфейса с временной конфигурацией и снимками экранов |
| `docs/` | Датированные результаты проверок |

DLL SDL3 и её лицензия находятся в `GameProfileManager/Native/`: DLL нужна для управления с контроллера и копируется рядом с приложением при сборке. Пользовательская база хранится в `%LOCALAPPDATA%\GameProfileManager\profiles.db` и в Git не входит.

## Сборка и проверка

Установите .NET SDK 10 и выполняйте команды из корня репозитория на Windows 11:

```powershell
dotnet build .\GameProfileManager\GameProfileManager.csproj -c Release
dotnet run --project .\GameProfileManager.Tests\GameProfileManager.Tests.csproj -c Release
dotnet run --project .\GameProfileManager.IntegrationTests\GameProfileManager.IntegrationTests.csproj -c Release

$smokeOutput = Join-Path .\artifacts ("ui-smoke-" + [guid]::NewGuid().ToString("N"))
dotnet run --project .\GameProfileManager.UiSmoke\GameProfileManager.UiSmoke.csproj -c Release -- $smokeOutput
```

UI smoke открывает окно приложения и создаёт временную базу и PNG в переданном каталоге. Каждый запуск используйте с новым каталогом. Флаги интеграционных тестов `--write-test`, `--nvidia-write-test` и `--sync-test` меняют временные профили RTSS/NVIDIA; для обычной проверки они не нужны.

## Автономная сборка

```powershell
dotnet publish .\GameProfileManager\GameProfileManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish\win-x64
```

Распространяйте **весь** каталог `publish\win-x64`: помимо EXE в нём находятся необходимые DLL и лицензия SDL3. Готовые сборки, архивы, результаты тестов и локальные данные исключены через `.gitignore`; этот репозиторий содержит исходники.
