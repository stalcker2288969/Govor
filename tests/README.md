# Серверные тесты Govor

Все действующие серверные тесты находятся в этой папке и входят в оба решения:
`Govor.Server.slnx` и `Govor.sln`. Требуется .NET SDK 10 из `global.json`.

| Проект | Назначение |
| --- | --- |
| `Govor.API.Tests/Unit` | Контроллеры, преобразование Result в HTTP, SignalR helpers; зависимости подменены mocks |
| `Govor.API.Tests/Integration` | HubExceptionFilter с реальными сессиями, запросы групп, доставка уведомлений и отмена фонового push |
| `Govor.Application.Tests/Unit` | JWT, пароли, username moderation, claims, online connections, storage, нормализация текста |
| `Govor.Application.Tests/Integration` | Сервисы регистрации, дружбы, профилей, сессий, истории сообщений и доступа к медиа с реальной SQL-базой |
| `Govor.Domain.Tests/Persistence` | Уникальные индексы, внешние ключи и каскадное удаление |
| `Govor.Domain.Tests/Migrations` | Очистка старых данных групп при миграции |
| `Govor.Server.Tests` | HTTP/JWT, SignalR, E2EE, группы, реакции, прочтения, push-очередь и Firebase transport |
| `Shared/TestDatabase.cs` | Изолированная БД и минимальные данные; исходник подключён к тестовым проектам через Directory.Build.props |

## Полный запуск

Из корня репозитория:

```powershell
dotnet restore Govor.Server.slnx
dotnet build Govor.Server.slnx --no-restore -c Release
dotnet test Govor.Server.slnx --no-build --no-restore -c Release --logger trx --results-directory artifacts/test-results
```

По умолчанию каждый тест использует собственную SQLite in-memory БД с внешними
ключами. Тесты не запускают production-сервер, не используют его БД, Firebase credentials
или файловое хранилище. HTTP-тесты поднимают приложение внутри WebApplicationFactory;
Firebase и storage заменены тестовыми реализациями.

Для проверки PostgreSQL укажите отдельную тестовую БД:

```powershell
$env:TEST_POSTGRES_CONNECTION = 'Host=localhost;Port=5432;Database=govor_tests;Username=govor;Password=tests-only'
dotnet test Govor.Server.slnx --no-build --no-restore -c Release --logger trx --results-directory artifacts/test-results/postgres
Remove-Item Env:TEST_POSTGRES_CONNECTION
```

Имя БД обязано заканчиваться на `_tests`; пользователь должен иметь право создавать
и удалять схемы. Каждый экземпляр TestDatabase создаёт уникальную схему, применяет
полную цепочку миграций и удаляет схему при Dispose. Пул соединений отключён:
уникальный SearchPath иначе создаёт отдельный пул для каждого теста и исчерпывает
лимит PostgreSQL. Для детерминированного сравнения времени учитывается точность
PostgreSQL в одну микросекунду.

На Windows криптографические тесты требуют рабочего профиля Windows CNG.
Ограниченная sandbox-учётная запись может завершать импорт PKCS#8 ошибкой
`CryptographicException`; проверку нужно запускать под обычным пользователем.

## Покрытие

```powershell
dotnet test Govor.Server.slnx --no-build --no-restore -c Release --collect 'XPlat Code Coverage;Format=json,cobertura' --results-directory artifacts/coverage
./tests/Measure-ApplicationCoverage.ps1 -ResultsDirectory artifacts/coverage
./tests/Measure-ApplicationCoverage.ps1 -ResultsDirectory artifacts/coverage -AssemblyName Govor.API -ExcludeGeneratedSources -MinimumLineCoverage 98 -MinimumBranchCoverage 92
```

Каждый тестовый проект создаёт свой `coverage.cobertura.xml`. Эти файлы нельзя
суммировать как независимые проценты: они покрывают пересекающиеся серверные сборки.

Объединённое покрытие `Govor.Application` в итоговом прогоне 24.10.2026:
**98,88% строк (2727/2758)** и **93,53% ветвей (1199/1282)**. До расширения
покрытие строк составляло 82,8%. Скрипт объединяет JSON-отчёты всех четырёх
проектов: строка или ветвь считается покрытой, если выполнена хотя бы в одном
проекте. Сборка Application включена целиком, без исключения неудобного кода.
CI проверяет минимальные пороги 98% строк и 92% ветвей.

Это не полное покрытие: остаются 31 строка и 83 пути ветвления. Подробности,
реализованные сценарии и ограничения — в [APPLICATION-COVERAGE.md](APPLICATION-COVERAGE.md).

Покрытие исходников `Govor.API`: **98,10% строк (1910/1947)** и **92,34% ветвей
(410/444)**. Все контроллеры выполнены по строкам; это не означает проверки всех
комбинаций условий. Для API из порога исключён только сгенерированный файл OpenAPI
из `obj`; с ним полная сборка имеет 88,47% строк и 63,08% ветвей. JSON сохраняет
полные числа и список исключений. Подробности — в [API-COVERAGE.md](API-COVERAGE.md).

## Обновление устаревших тестов

Старые NUnit-проекты .NET 8 переведены на .NET 10; общие версии test SDK,
стабильного NUnit и адаптера заданы в `Directory.Build.props`. Тесты больше не
ссылаются на удалённые Govor.Data/Repositories, старые пространства имён Services/Interfaces
и внешний исходный проект SmartRes; используется существующий `libs/SmartRes.dll`.

Проверки удалённых репозиториев и IObjectValidator заменены проверками текущих
сервисов и ограничений модели EF. Вместо ожидания исключений проверяются Result,
HTTP status и ProblemDetails. Закомментированные тесты удалённых API Presence/Chats
убраны; актуальные сценарии SignalR выполняются в Govor.Server.Tests.
LocalStorageServiceTests перенесены из API к приложению.

Тесты удалённого консольного клиента `Govor.ConsoleClient.Tests` не входят в
серверные решения. Их исходники оставлены в прежней папке вне `tests`.

CI запускает все четыре проекта в Release на SQLite и PostgreSQL, проверяет
пороги покрытия и сохраняет TRX и отчёты покрытия для обеих конфигураций.
Для Application и исходников API установлены пороги 98% строк / 92% ветвей.

## Результат проверки 24.10.2026

Release-сборка и 710 тестов проходят на SQLite и PostgreSQL 17.4; пропущенных
тестов нет. Проверены регистрация и роли, сессии и refresh/replay, доступ к чатам
и медиа, пагинация, миграции и ограничения данных, группы, реакции, E2EE, HTTP/SignalR,
push retries и сериализация Firebase через подменённый транспорт.

При расширении покрытия Application добавлена 191 проверка; в самом проекте
Application.Tests теперь 295 тестовых случаев. Реализованы чтение last seen,
чтение медиа по URL и блокировка/разблокировка друзей. Исправлены обнаруженные
ошибки сохранения криптографических ключей EF, проверки владельца сессии,
загрузки SignedPreKey, обработки пустого username и выхода за корень storage.

При расширении API добавлены ещё 172 проверки: API.Tests содержит 271 случай,
Server.Tests — 136. Пустые каталоги прежней структуры удалены; места для будущих
тестов заранее не создаются. Папка `Shared` в IDE также может быть виртуальной
ссылкой на общий исходник `tests/Shared/TestDatabase.cs`.

Найден и исправлен дефект загрузки вложений: Required/implicit Required для
EncryptedKey отклонял пустое поле, хотя E2EE-вложение должно приходить без ключа
расшифровки. Регрессионные unit- и HTTP-тесты покрывают отсутствующее и пустое поле,
а plaintext и переданный ключ по-прежнему отклоняются.

В исходной кодовой базе остаются nullable/compiler warnings и предупреждения
NuGet NU1902/NU1903 для SixLabors.ImageSharp 3.1.12. Переход на новые ImageSharp
требует отдельной проверки совместимости и лицензирования: официальные
[release notes](https://github.com/SixLabors/ImageSharp/releases) отмечают изменение
license enforcement вместе с security fixes. Успех тестов не снимает эти предупреждения
и не подтверждает доставку push на физическое устройство или production-развёртывание.
