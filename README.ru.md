<p align="center">
  <img src="src/GameProfileManager/Assets/GpmBranding/PNG/gpm_dark_256.png" alt="Иконка GPM" width="96" height="96">
</p>

<h1 align="center">GPM — Game Profile Manager</h1>

<p align="center">Индивидуальные лимиты FPS и параметры NVIDIA через RTSS и NVIDIA DRS в Windows 11.</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.ru.md"><strong>Русский</strong></a>
</p>

<p align="center">
  <a href="https://github.com/Vladislav2505/GameProfileManager/releases">Скачать</a> ·
  <a href="https://github.com/Vladislav2505/GameProfileManager/issues">Сообщить о проблеме</a>
</p>

<p align="center">
  <a href="https://github.com/Vladislav2505/GameProfileManager/actions/workflows/installer.yml"><img src="https://github.com/Vladislav2505/GameProfileManager/actions/workflows/installer.yml/badge.svg" alt="Сборка Windows"></a>
</p>

GPM управляет профилями отдельных игровых исполняемых файлов. Перед применением профиля программа сохраняет исходные настройки RTSS и NVIDIA, повторно проверяет результат и позволяет восстановить прежние значения.

## Возможности

- Лимит FPS через RTSS и параметры G-SYNC/фиксированной частоты и V-Sync в профиле NVIDIA для игры.
- Один профиль для игры или разные профили для выбранных мониторов.
- Импорт игр из Steam и Epic Games либо добавление исполняемого файла вручную.
- Проверка применённых параметров, восстановление исходных значений и экспорт резервной копии данных GPM.
- Интерфейс WinUI 3 с управлением геймпадом, системным треем и светлой/тёмной темами.

## Требования и установка

- Windows 11 x64, видеокарта NVIDIA и совместимый драйвер.
- Установленный и запущенный [RivaTuner Statistics Server (RTSS)](https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/) для применения профилей.
- Подтверждение прав администратора при запуске текущей версии GPM.

Скачайте установщик в разделе [GitHub Releases](https://github.com/Vladislav2505/GameProfileManager/releases). Необходимые компоненты .NET и Windows App SDK включены. Если GPM не находит RTSS, укажите папку его установки в **«Настройках»**.

## Быстрый старт

1. Добавьте игру в разделе **«Мои игры»** и проверьте выбранный исполняемый файл.
2. Создайте или измените профиль и назначьте его игре.
3. Нажмите **«Применить профиль к игре»** и проверьте результат. Раздел **«Диагностика»** позволяет читать настройки без изменений.

Назначение профиля сохраняет выбор в GPM; **применение** записывает настройки в RTSS и NVIDIA DRS. Автоматическое применение включается отдельно. Повторное чтение подтверждает сохранённые параметры, но не фактический FPS или работу VRR в игре. Чтобы изменения драйвера вступили в силу, иногда нужно перезапустить игру.

Данные GPM находятся в `%LOCALAPPDATA%\GameProfileManager\profiles.db`; данные версии 1.x сохраняются. Перед обновлением полностью завершите GPM, включая значок в трее. В **«Настройках»** можно экспортировать резервную копию JSON.

## Ограничения

GPM поддерживает NVIDIA и RTSS, но не запускает игры и не меняет параметры дисплеев Windows. Программа не определяет, на каком мониторе находится окно игры. RTSS сопоставляет профили по имени исполняемого файла, поэтому GPM блокирует неоднозначную запись для игр с одинаковым именем файла. Раздел **«Графические конфигурации»** — предварительный; снимки файлов настроек игр пока не реализованы.

## Сборка из исходников

На Windows 11 установите [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0) и выполните из корня репозитория:

```powershell
dotnet build .\GameProfileManager.slnx -c Release
dotnet publish .\src\GameProfileManager\GameProfileManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o .\artifacts\publish
```

Запускайте `GameProfileManager.exe` из полной папки `artifacts\publish`: одного EXE недостаточно. Для сборки установщика установите [Inno Setup](https://jrsoftware.org/isdl.php) и выполните `.\scripts\build-installer.ps1`.

## Поддержка и лицензия

Сообщайте об ошибках в [Issues](https://github.com/Vladislav2505/GameProfileManager/issues): укажите версии GPM, Windows, драйвера NVIDIA и RTSS, а также шаги воспроизведения. При изменении исходников следуйте [AGENTS.md](AGENTS.md).

Общей лицензии проекта в репозитории пока нет. Открытый доступ к исходникам сам по себе не предоставляет права на их изменение и распространение. Для включённой библиотеки SDL3 действует [отдельная лицензия](src/GameProfileManager/Native/SDL3-LICENSE.txt).
