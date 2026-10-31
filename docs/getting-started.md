# Быстрый старт

## Требования

Нужны .NET SDK 10, доступ к NuGet и отдельная PostgreSQL с созданной БД для разработки. `global.json` задаёт SDK `10.0.100` с `rollForward: latestFeature`; подходит установленный SDK `10.0.400`. В репозитории уже есть `libs/SmartRes.dll` и `Govor.API/configs/ban_usernames.json`.

Firebase для локального запуска не требуется: в `appsettings.json` он выключен. Секреты БД и приложения задаются отдельно.

## 1. Настроить окружение

PowerShell, из корня серверного репозитория. Подставьте реквизиты своей **локальной** БД. Переменные действуют в текущем терминале.

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__GovorDbContext = 'Host=127.0.0.1;Port=5432;Database=govor_dev;Username=postgres;Password=YOUR_LOCAL_PASSWORD'
$env:JwtAccessOption__SecretKey = 'local-development-signing-key-change-before-deployment'
$env:EncryptionOption__Secret = 'local-development-refresh-secret-change-before-deployment'
$env:Firebase__Enabled = 'false'
```

Эти строки предназначены для разработки. Для внешнего сервера используйте собственные случайные секреты и HTTPS. [Полный список параметров](configuration.md).

## 2. Собрать сервер

```powershell
dotnet --version
dotnet restore Govor.Server.slnx
dotnet build Govor.Server.slnx -c Release --no-restore
```

Решение включает API, прикладной и доменный слои, контракты и четыре действующих тестовых проекта. Старый консольный клиент в серверное решение не входит.

## 3. Применить миграции

```powershell
dotnet run --project Govor.API/Govor.API.csproj --no-launch-profile -- --migrate-only
```

Команда применяет ожидающие EF Core миграции и завершается без HTTP API и Firebase. Обычный запуск **не применяет** миграции автоматически. Отдельный `dotnet-ef` для этого способа не нужен. Для существующей БД сначала сделайте резервную копию и изучите [обновление сервера](operations.md).

## 4. Запустить и проверить

```powershell
dotnet run --project Govor.API/Govor.API.csproj --launch-profile http
```

API: `http://localhost:7155`, Swagger UI: `/swagger`. Для локального HTTPS используйте профиль `https` (`https://localhost:7156`) и доверенный сертификат разработки.

```powershell
Invoke-WebRequest http://localhost:7155/server/ping
Invoke-WebRequest http://localhost:7155/server/ready
```

`ping` проверяет процесс. `ready` возвращает `200`, если БД доступна и нет ожидающих миграций, иначе `503`. Они не проверяют Firebase, свободное место и доставку сообщений.

## 5. Первый аккаунт

Регистрация требует кода приглашения. Для первого администратора оператор задаёт секрет `InvitationAdmin__Key` и использует `POST /api/admin/InviteUser/InvitationWithKey`: этот метод не требует JWT, но проверяет секрет из тела. Создайте ограниченное по сроку/числу регистраций приглашение с `isAdmin=true`, затем зарегистрируйтесь по полученному коду. [Точные поля и пример](endpoints/admin-and-groups.md).

Ключ задаётся до запуска API; без него метод возвращает 503. После первоначальной настройки уберите ключ из runtime и перезапустите API, если этот способ больше не нужен. Для обычных приглашений действующий администратор использует защищённый `/Invitation`. Для проверки кода без настройки настоящего аккаунта подходят [изолированные тесты](testing.md).

Далее: [интеграция клиента](client-integration.md), [вход](endpoints/authentication/README.md), [диагностика](diagnostics.md).
