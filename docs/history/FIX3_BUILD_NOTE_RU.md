# Fix 3 — исправления компиляции

## GoogleDriveDownloadResolver.cs

В `TryExtractFileId` и `AddQuery` используется локальный `QueryStringParser.Parse(...)`. В исходнике проекта не должно остаться ссылки на несуществующий тип `System.WebQueryString`.

Если эта ошибка всё ещё возникает в вашей рабочей папке, проверьте строку 23 в `Services/GoogleDriveDownloadResolver.cs` и замените вызов на:

```csharp
var query = QueryStringParser.Parse(uri.Query);
```

Также проверьте вызов ниже в методе `AddQuery`:

```csharp
var merged = QueryStringParser.Parse(builder.Query);
```

## InstalledVersionService.cs

Исправлено nullable-предупреждение: имя программы из записи реестра теперь передаётся как `registry.Value.DisplayName ?? ""`. Предупреждение CS8604 не было причиной провала сборки, но устранено.

## Пересборка

В терминале VS Code из папки `CoolSnesh.Avalonia` выполните:

```powershell
dotnet clean .\CoolSnesh.Avalonia.csproj
dotnet build .\CoolSnesh.Avalonia.csproj -c Release
```

Перед сборкой убедитесь, что VS Code открыт именно на папке из этого архива, а не на старой распакованной версии.
