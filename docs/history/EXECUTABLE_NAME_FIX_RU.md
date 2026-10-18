# Исправление имени исполняемого файла

## Что было

После сборки приложение выдавалось как `CoolSnesh.Avalonia.exe`, хотя пользовательское имя продукта — `CoolSnesh Downloads`.

## Что стало

- Добавлены метаданные продукта `CoolSnesh Downloads` (`AssemblyTitle`, `Product`, `Description`).
- После `Build` создаётся копия apphost под именем `CoolSnesh Downloads.exe` в `bin\Release\net10.0-windows\` (или соответствующей папке конфигурации).
- После `Publish` создаётся `CoolSnesh Downloads.exe` в папке публикации, включая сценарий `PublishSingleFile`; технический `CoolSnesh.Avalonia.exe` из каталога `publish` удаляется после успешного копирования брендированного файла.
- Внутреннее имя сборки и namespace не меняются: это сохраняет текущие Avalonia URI `avares://CoolSnesh.Avalonia/...` и не ломает ресурсы/настройки. Оригинальный apphost оставлен для совместимости с VS Code и `dotnet run`.

## Сборка и публикация

```powershell
dotnet restore .\CoolSnesh.Avalonia.sln
dotnet test .\CoolSnesh.Avalonia.sln -c Release
dotnet build .\CoolSnesh.Avalonia.csproj -c Release
dotnet publish .\CoolSnesh.Avalonia.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

В каталоге `publish` используй `CoolSnesh Downloads.exe`. Полную компиляцию на этой машине не выполняли, потому что .NET SDK отсутствует; проверь target после публикации в Windows.
