# GitHub Actions и релизы CoolSnesh Downloads

## Что настроено

- `.github/workflows/build.yml` запускается при push в `main`, pull request и вручную; запускает тесты и создаёт временный Actions Artifact.
- `.github/workflows/release.yml` запускается при отправке тега `vMAJOR.MINOR.PATCH` и позволяет вручную пересобрать уже существующий тег через `workflow_dispatch`.
- Версия для Release берётся из тега, поэтому `v1.0.0` создаёт продукт с версией `1.0.0`.
- Перед упаковкой workflow нормализует имя EXE к `CoolSnesh Downloads.exe`, удаляет дублирующие apphost-имена и рассчитывает контрольные суммы SHA-256.
- Публичный Release содержит EXE, SHA-256 EXE, ZIP и SHA-256 ZIP.

## Первая публикация

1. Создайте пустой публичный репозиторий `CoolSnesh-Downloads` без README/`.gitignore`/лицензии.
2. Загрузите исходники из корня этой папки и дождитесь зелёной галочки `Build CoolSnesh Downloads`.
3. Создайте тег первой версии:

```powershell
git tag -a v1.0.0 -m "CoolSnesh Downloads v1.0.0"
git push origin v1.0.0
```

4. Дождитесь успешного `Publish CoolSnesh Downloads Release` и проверьте вкладку Releases.

## Следующие релизы

После изменения исходников/версии отправьте коммит в `main`, дождитесь успешного build workflow, затем создайте новый уникальный тег (например `v1.0.1`) и отправьте его. Не используйте опубликованный тег повторно для обычного выпуска.

Для повторной публикации существующего релиза используйте Actions → `Publish CoolSnesh Downloads Release` → `Run workflow`, выберите `main` и введите имеющийся тег. Старые названия EXE удаляются, канонический файл перезаписывается.

## Assets

- `CoolSnesh Downloads.exe`
- `CoolSneshDownloads-win-x64.exe.sha256`
- `CoolSneshDownloads-win-x64.zip`
- `CoolSneshDownloads-win-x64.zip.sha256`

Самообновление в приложении использует прямой EXE и checksum-файл. Не переименовывайте опубликованный Asset вручную: источник сборки сам нормализует имя.
