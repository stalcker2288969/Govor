# Тестирование

## Серверный набор

Из корня репозитория:

```powershell
dotnet restore Govor.Server.slnx
dotnet build Govor.Server.slnx --no-restore -c Release
dotnet test Govor.Server.slnx --no-build --no-restore -c Release --logger trx --results-directory artifacts/test-results
```

По умолчанию каждый тест создаёт изолированную SQLite in-memory БД. HTTP/SignalR тесты поднимают приложение внутри тестового хоста; Firebase и хранилище подменяются. Production-БД, реальные push credentials и пользовательские файлы не нужны.

| Проект | Область |
| --- | --- |
| `tests/Govor.Domain.Tests` | Ограничения БД и миграции |
| `tests/Govor.Application.Tests` | Прикладные сервисы, права, токены, хранение, валидация |
| `tests/Govor.API.Tests` | Контроллеры, хабы, фильтры и транспортные границы |
| `tests/Govor.Server.Tests` | HTTP/JWT, SignalR, E2EE, восстановление, push и группы |

На Windows криптографические тесты требуют обычного профиля Windows CNG. Ограниченная sandbox-учётная запись может дать ошибку импорта PKCS#8; это не следует устранять изменением криптографического протокола.

## PostgreSQL

Используйте отдельную БД с именем, заканчивающимся `_tests`, и пользователя с правом создания/удаления схем. **Не указывайте рабочую БД.**

```powershell
$env:TEST_POSTGRES_CONNECTION = 'Host=localhost;Port=5432;Database=govor_tests;Username=govor;Password=YOUR_TEST_PASSWORD'
dotnet test Govor.Server.slnx -c Release
Remove-Item Env:TEST_POSTGRES_CONNECTION
```

Каждый тест использует отдельную схему и применяет миграции; соединения без пула защищают лимит PostgreSQL. CI запускает SQLite и PostgreSQL; состав job задан в `.github/workflows/dotnet.yml`.

## Проверенная ревизия

30.10.2026 на `a3401ce` прошли **735 тестов** Release на SQLite: Domain — 8, Application — 296, API — 271, Server — 160; ошибок и пропусков нет. Запуск выполнялся до fast-forward слияния, которое сохранило тот же коммит. В этой проверке PostgreSQL, настоящий Firebase, контейнер и UI устройств не запускались.

Сборка выдала предупреждения компилятора и NuGet, включая известные уязвимости `SixLabors.ImageSharp 3.1.12`. Успешные тесты не означают отсутствие этих проблем.

## Покрытие

```powershell
dotnet test Govor.Server.slnx --no-build --no-restore -c Release --collect 'XPlat Code Coverage;Format=json,cobertura' --results-directory artifacts/coverage
./tests/Measure-ApplicationCoverage.ps1 -ResultsDirectory artifacts/coverage
./tests/Measure-ApplicationCoverage.ps1 -ResultsDirectory artifacts/coverage -AssemblyName Govor.API -ExcludeGeneratedSources -MinimumLineCoverage 98 -MinimumBranchCoverage 92
```

CI проверяет пороги 98% строк и 92% ветвей для Application и исходников API. Проценты из разных проектов нельзя складывать: они покрывают пересекающиеся сборки. Отчёты от 24.10.2026 в `tests/APPLICATION-COVERAGE.md` и `tests/API-COVERAGE.md` — исторические измерения; после добавления тестов восстановления они не пересчитывались в текущем запуске.

Перед релизом дополнительно проверьте HTTPS/WSS, доставку на физические устройства, восстановление архива на чистом устройстве, старые вложения после деплоя и восстановление резервной копии. [Порядок обновления](operations.md).
