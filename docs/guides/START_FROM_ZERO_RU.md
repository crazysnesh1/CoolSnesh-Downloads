# Чистая публикация CoolSnesh Downloads v1.0.0

Этот архив уже выставляет версию проекта `1.0.0` и включает `.github/workflows/build.yml` и `.github/workflows/release.yml`.

## Перед загрузкой

1. Требуется Windows 10/11 и .NET 10 SDK.
2. Выполните `dotnet restore .\CoolSnesh.Avalonia.sln`, `dotnet test .\CoolSnesh.Avalonia.sln -c Release`, `dotnet build .\CoolSnesh.Avalonia.csproj -c Release`.
3. Проверьте `git status --short`: в исходники не должны попасть пароли, локальные конфиги, журналы, `bin`, `obj`, установщики или ISO. `.gitignore` уже исключает типовые временные файлы.

## Для нового пустого GitHub-репозитория

Создайте публичный репозиторий `CoolSnesh-Downloads` без README, `.gitignore` и лицензии. Затем из корня распакованного проекта выполните:

```powershell
git init -b main
git remote add origin https://github.com/crazysnesh1/CoolSnesh-Downloads.git
git add -A
git status --short
git commit -m "CoolSnesh Downloads v1.0.0 - initial source release"
git push -u origin main
```

После успешной зелёной сборки в GitHub Actions создайте первую версию:

```powershell
git tag -a v1.0.0 -m "CoolSnesh Downloads v1.0.0"
git push origin v1.0.0
```

Release workflow выполнит тесты, соберёт автономный Windows x64 EXE и опубликует прямой EXE, ZIP и их SHA-256. Для дальнейших версий используйте новые уникальные теги, например `v1.0.1`.

## Важно

В этом архиве находятся исходники и workflows, но не готовый скомпилированный EXE: в среде подготовки не было .NET SDK. Готовый EXE создаст GitHub Actions на Windows runner.
