# Обновление иконки CoolSnesh Downloads

- `Assets/App.ico` — многоразмерная иконка Windows: 16, 24, 32, 40, 48, 64, 128 и 256 px.
- `Assets/App.png` — версия 512×512 для логотипа в шапке приложения.
- `CoolSnesh.Avalonia.csproj` уже использует `Assets\App.ico` как `ApplicationIcon` и включает ICO/PNG в ресурсы Avalonia.
- `MainWindow.axaml.cs` уже назначает `Assets/App.ico` иконкой окна. Я заменил сами файлы ресурсов, поэтому дополнительных правок логики не требуется.
- Значок EXE будет применяться к ярлыкам, созданным после публикации новой версии. Чтобы обновилась иконка уже существующего ярлыка, Windows иногда нужно обновить кэш значков или создать ярлык заново.

## Сборка автономного EXE для Windows x64

```powershell
dotnet restore .\CoolSnesh.Avalonia.sln
dotnet test .\CoolSnesh.Avalonia.sln -c Release
dotnet publish .\CoolSnesh.Avalonia.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Результат: `bin\Release\net10.0-windows\win-x64\publish\CoolSnesh Downloads.exe`.
