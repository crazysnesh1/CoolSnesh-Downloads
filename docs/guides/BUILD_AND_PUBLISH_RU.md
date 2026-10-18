# Сборка и публикация CoolSnesh Downloads

Требуется Windows 10/11 и .NET 10 SDK.

## Локальная проверка

Из корня репозитория выполни:

```powershell
dotnet restore .\CoolSnesh.Avalonia.sln
dotnet test .\CoolSnesh.Avalonia.sln -c Release
dotnet build .\CoolSnesh.Avalonia.csproj -c Release
```

## Автономный EXE для Windows x64

Команда включает .NET Runtime в публикацию, поэтому пользователю не потребуется отдельно устанавливать .NET:

```powershell
dotnet publish .\CoolSnesh.Avalonia.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Ожидаемый путь:

```text
bin\Release\net10.0-windows\win-x64\publish\CoolSnesh Downloads.exe
```

## GitHub Releases

Для официального релиза используй тег `vMAJOR.MINOR.PATCH`, например `v1.0.0`. Workflow `release.yml` выполняет тесты, публикует EXE, ZIP и SHA-256. Подробнее: [настройка GitHub Releases](GITHUB_RELEASES_SETUP_RU.md).
