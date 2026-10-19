# CoolSnesh Downloads

**Каталог, загрузчик и обновлятор программ для Windows 10/11.**

CoolSnesh Downloads помогает находить, скачивать, устанавливать и обновлять приложения из одного окна. Проект ориентирован на простой тёмный интерфейс, проверку установленных версий и загрузку установщиков из официальных источников, где они доступны.

## 🖼️ Скриншот
[Главное окно CoolSnesh Downloads]([docs/images/main-window.png](https://github.com/crazysnesh1/CoolSnesh-Downloads/blob/main/docs/images/main-window.png))
<img width="1280" height="825" alt="image" src="https://github.com/user-attachments/assets/b3f61fec-3de8-438b-95d4-51ff0cc30be4" />
## ⚙️ Возможности

- Каталог приложений с поиском, категориями, иконками, описаниями и ссылками на сайты.
- Проверка установленных версий и отображение статуса: актуально, доступно обновление или не установлено.
- Скачивание и пакетное обновление выбранных приложений; тихая установка используется там, где известны поддерживаемые параметры.
- Поддержка HTTP, HTTPS, SOCKS4 и SOCKS5 прокси, включая проверку соединения.
- Автообновление самого CoolSnesh Downloads через GitHub Releases с проверкой SHA-256.
- Специальный сценарий обновления Flowseal Zapret с сохранением пользовательских списков, резервной копией и попыткой отката.
- Журналирование ошибок и сохранение пользовательских настроек.

## 📦 Категории каталога

В каталоге **39 приложений** в шести категориях:

| Категория | Количество | Примеры содержимого |
|---|---:|---|
| Для России | 2 | Flowseal Zapret, TG WS Proxy |
| VPN и Прокси | 4 | Proxifier, v2rayTun, Happ, Incy |
| Программы | 7 | Discord, Telegram Desktop, Notepad++, qBittorrent, 7-Zip, WinRAR, ArtCraft Launcher |
| Браузеры | 10 | Chrome, Firefox, Яндекс Браузер, Opera, Opera GX, Edge, Brave, Vivaldi, Tor Browser, LibreWolf |
| Windows | 11 | Windows 10/11 ISO, Ventoy, Rufus, Visual C++ Redistributable, .NET Framework, .NET Desktop Runtime, NVIDIA App |
| Игровые лаунчеры | 5 | Steam, Battle.net, Epic Games Launcher, EA app, Ubisoft Connect |

## 🛠️ Технологии

- **C#** и **.NET 10**
- **Avalonia UI 12** и **XAML** — интерфейс приложения
- **xUnit** — модульные тесты
- **GitHub Actions** — автоматические тесты, сборка и публикация релизов
- Windows Registry и DPAPI — обнаружение установок и защита сохранённого пароля прокси

## 🔨 Сборка из исходников

Требуется .NET 10 SDK и Windows. В корне репозитория выполни:

```powershell
dotnet restore .\CoolSnesh.Avalonia.sln
dotnet test .\CoolSnesh.Avalonia.sln -c Release
dotnet build .\CoolSnesh.Avalonia.csproj -c Release
```

Автономная сборка Windows x64 публикуется через GitHub Actions. Готовые версии доступны на странице [Releases](https://github.com/crazysnesh1/CoolSnesh-Downloads/releases/latest).
