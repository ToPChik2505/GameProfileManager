<p align="center">
  <img src="src/GameProfileManager/Assets/GpmBranding/PNG/gpm_dark_256.png" alt="Иконка GPM" width="96" height="96">
</p>

<h1 align="center">GPM — Game Profile Manager</h1>

<p align="center">Профили производительности игр и снимки графических настроек для разных дисплеев в Windows 11.</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.ru.md"><strong>Русский</strong></a>
</p>

<p align="center">
  <a href="https://github.com/ToPChik2505/GameProfileManager/releases">Скачать</a> ·
  <a href="https://github.com/ToPChik2505/GameProfileManager/issues">Сообщить о проблеме</a>
</p>

<p align="center">
  <a href="https://github.com/ToPChik2505/GameProfileManager/actions/workflows/installer.yml"><img src="https://github.com/ToPChik2505/GameProfileManager/actions/workflows/installer.yml/badge.svg" alt="Сборка Windows"></a>
</p>

## Возможности

- Ограничение FPS для отдельных игр через RTSS и поддерживаемые параметры профилей NVIDIA через NVIDIA DRS.
- Сохранение снимков выбранных конфигурационных файлов и значений реестра, назначение снимков дисплеям и необязательное автоматическое переключение по эффективному дисплею.
- Защитные копии и восстановление прерванных операций с несколькими файлами. GPM не закрывает игры и не сохраняет или не применяет конфигурации, пока игра запущена или конфликт файлов не разрешён.
- Импорт игр из Steam и Epic, добавление EXE вручную, интерфейс WinUI 3 с поддержкой геймпада.
- Онлайн-поиск конфигураций использует [MediaWiki API PCGamingWiki](https://www.pcgamingwiki.com/wiki/PCGamingWiki:API): разбор статей без авторизации или структурированные данные GameData с необязательным bot-паролем. Учётные данные хранятся в Диспетчере учётных данных Windows.

## Требования

- Windows 11 x64.
- Драйвер NVIDIA для параметров профилей NVIDIA; установленный и запущенный RTSS для функций RTSS.

Установщик доступен в [последнем выпуске на GitHub](https://github.com/Vladislav2505/GameProfileManager/releases).

## Быстрый старт

1. Добавьте игру и проверьте путь к её EXE.
2. Создайте профиль, назначьте его игре и примените.
3. Для смены графических настроек по дисплею добавьте конфигурационные файлы игры, сохраните снимок для каждого варианта, назначьте снимки дисплеям и включите автоматическое переключение.

GPM управляет только явно выбранными источниками настроек. Автопереключение использует правила определения эффективного дисплея и не определяет, на каком мониторе находится окно игры. Если файлы изменились вне GPM и не совпадают с сохранённым снимком, переключение останавливается и создаёт защитную копию.

## Данные и резервные копии

Профили и настройки хранятся в %LOCALAPPDATA%\GameProfileManager\profiles.db; существующий экспорт JSON сохранён. Снимки графики и журналы восстановления операций находятся в %LOCALAPPDATA%\GameProfileManager\game-configs и %LOCALAPPDATA%\GameProfileManager\transactions. Для графических данных предусмотрен отдельный ZIP-экспорт и импорт.

## Сборка из исходников

Установите [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0) и выполните команду из корня репозитория:

```powershell
dotnet publish .\src\GameProfileManager\GameProfileManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o .\artifacts\publish
```

Запускайте GameProfileManager.exe из полной папки публикации. У библиотеки SDL3 для геймпада отдельная лицензия: [SDL3-LICENSE.txt](src/GameProfileManager/Native/SDL3-LICENSE.txt). Общей лицензии проекта в репозитории пока нет.
