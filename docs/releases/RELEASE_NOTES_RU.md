# CoolSnesh Downloads — изменения релиза

## Иконки из предоставленного архива

- Добавлены 13 предоставленных пользователем SVG-иконок для Chrome, Firefox, Edge, Opera, Opera GX, LibreWolf, Tor, Telegram Desktop, qBittorrent, EA app, Ubisoft Connect, Windows 10 и Windows 11.
- SVG сохранены в `Assets/PackageIcons/SourceSvg`, а для runtime отрисовки в Avalonia подготовлены прозрачные PNG 512×512. PNG встроены в ресурсы приложения, поэтому не нужна сетевая загрузка этих иконок.
- Каталожные иконки показываются в размере 48×48 без фоновой рамки; для приложений без пользовательской иконки остаётся запасной вариант.

## Источники загрузки и версии

- Tor Browser: возле названия и во всплывающей подсказке отображается предупреждение, что для загрузки может понадобиться VPN или прокси в зависимости от доступности Tor Project из сети пользователя.
- LibreWolf: скачивание и определение версии работают через официальную страницу Windows `https://librewolf.net/installation/windows/`; ссылка на x64 setup EXE извлекается динамически.
- Ventoy: последняя Windows ZIP-версия и ссылка берутся из официального GitHub Releases API `https://api.github.com/repos/ventoy/Ventoy/releases/latest`.
- NVIDIA App: версия и ссылка извлекаются из официальной страницы NVIDIA.

## Исправление Flowseal и прокси

- Существующий корректный ручной путь Flowseal проверяется раньше автоматического поиска; для Zapret он должен содержать `service.bat` и `bin\winws.exe`. Если заданный путь больше не существует или не распознаётся как установка, применяется обычное автоопределение.
- Включение/выключение прокси работает через `ToggleSwitch` и сохраняется независимо от кнопки сохранения реквизитов. Кнопка проверки создаёт соединение по введённым данным даже при выключенном общем переключателе.
- В поле хоста можно вставить `ip:port:login:password` или строку с префиксом `http://`, `https://`, `socks4://`, `socks5://`; поля заполняются автоматически. Остаток строки после третьего разделителя сохраняется в качестве пароля.

## Категория Windows

Добавлены официальные источники для Visual C++ Redistributable x64/x86, .NET Framework 3.5/4.8/4.8.1, .NET Desktop Runtime x64 и NVIDIA App. .NET Framework 3.5 на Windows 11 26H1 и новее устанавливается отдельным пакетом; на более ранних версиях Windows 11 следует использовать «Компоненты Windows». См. [FAQ Microsoft](https://learn.microsoft.com/en-us/dotnet/framework/install/dotnet-35-windows-11-faq). Ссылки Visual C++ ведут на текущие поддерживаемые Microsoft версии: [x64](https://aka.ms/vc14/vc_redist.x64.exe) и [x86](https://aka.ms/vc14/vc_redist.x86.exe).

## Ограничения проверки

XML-файлы проекта, наличие PNG-ресурсов, ссылки в каталоге и архив проверяются автоматически при сборке пакета. Полная компиляция C# и реальные скачивания здесь не выполнялись, потому что в среде подготовки отсутствует .NET SDK. Перед использованием рекомендуется выполнить `dotnet test` и `dotnet build` на Windows и проверить LibreWolf, Ventoy, NVIDIA App и тест прокси непосредственно в приложении.

## Автономная публикация EXE

В корне архива есть файл [руководство по сборке](../guides/BUILD_AND_PUBLISH_RU.md) с командой публикации. Краткая команда:

```powershell
dotnet publish .\CoolSnesh.Avalonia.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```
