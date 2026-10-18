# Исправление сборки — Fix 2

## GoogleDriveDownloadResolver.cs
- Переименован локальный парсер query-строки `SystemWebQueryString` в `QueryStringParser`, чтобы не возникало путаницы с пространством имён `System.Web`.
- Вызовы на разборе `uri.Query` и `builder.Query` используют `QueryStringParser.Parse(...)`. Парсер реализован в этом же файле, дополнительные NuGet-пакеты не нужны.

## InstalledVersionService.cs
- Для nullable-версии, прочитанной из реестра, добавлен fallback `version ?? ""`, чтобы убрать предупреждение CS8604 при создании `InstalledInfo`.

После распаковки убедитесь, что VS Code открыт именно на этой папке проекта, затем выполните:

```powershell
dotnet clean .\CoolSnesh.Avalonia.csproj
dotnet build .\CoolSnesh.Avalonia.csproj -c Release
```

Полную сборку в среде подготовки выполнить не удалось: .NET SDK здесь отсутствует.
