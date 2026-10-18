# Исправление Google Drive parser и nullable warning

Этот архив содержит исходники с двумя исправлениями:

1. `Services/GoogleDriveDownloadResolver.cs`: используется локальный `QueryStringParser`, без несуществующего `System.WebQueryString`.
2. `Services/InstalledVersionService.cs`: для версии из записи деинсталлятора используется `version ?? ""`, чтобы не передавать null в параметр `InstalledInfo.Version`.

## Сборка в VS Code

Распакуйте архив в новую папку, откройте именно эту папку в VS Code и выполните:

```powershell
dotnet clean .\CoolSnesh.Avalonia.csproj
dotnet restore .\CoolSnesh.Avalonia.csproj
dotnet build .\CoolSnesh.Avalonia.csproj -c Release
```

Чтобы проверить нужный исходник в PowerShell:

```powershell
Select-String -Path .\Services\GoogleDriveDownloadResolver.cs -Pattern 'System\.WebQueryString|SystemWebQueryString|QueryStringParser'
Select-String -Path .\Services\InstalledVersionService.cs -Pattern 'new InstalledInfo\(true, version'
```

В `GoogleDriveDownloadResolver.cs` должны быть вызовы `QueryStringParser.Parse(...)` и объявление `QueryStringParser`; старого `System.WebQueryString` быть не должно.
