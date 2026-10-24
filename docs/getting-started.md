# Запуск и конфигурация

## Требования

.NET SDK 10 с корректной feature band, PostgreSQL, доступ к NuGet, файл `libs/SmartRes.dll`, каталог `Govor.API/configs` и Firebase service account в `Govor.API/secrets/firebase-adminsdk.json`.

В `global.json` записана некорректная версия SDK `10.0.0`; замените её в отдельном исправлении на установленную версию, например `10.0.400`. Во время диагностики SDK 10.0.400 всё же собрал отдельный проект API.

## Команды из корня репозитория

```powershell
dotnet restore Govor.API/Govor.API.csproj
dotnet build Govor.API/Govor.API.csproj --no-restore
dotnet ef database update --project Govor.Domain --startup-project Govor.API
dotnet run --project Govor.API/Govor.API.csproj
```

Команда миграции требует установленного инструмента dotnet-ef; она приведена как инструкция и не выполнялась на пользовательской БД. Перед обновлением существующей БД проверьте миграции и резервную копию.

## Конфигурация

| Ключ / ресурс | Назначение |
| --- | --- |
| ConnectionStrings:GovorDbContext | PostgreSQL connection string |
| JwtAccessOption:SecretKey | Ключ подписи JWT |
| JwtAccessOption:Minutes | Срок access token в минутах |
| JwtRefreshOption:RefreshTokenLifetimeDays | Срок refresh-сессии |
| MessageEditingOptions:Enabled | Применять ли срок редактирования |
| MessageEditingOptions:MaxEditTimeMinutes | Срок редактирования; значение класса по умолчанию 15 |
| configs/ban_usernames.json | UsernameModeration: BlockedExact, BlockedContains, Reserved |
| secrets/firebase-adminsdk.json | Обязательные учётные данные Firebase при старте |
| uploads/ | Файлы относительно ContentRootPath |

Не переносите реальные ключи, пароли и service account в документацию или Git. Не рассчитывайте только на переменные окружения: Program.cs повторно добавляет appsettings.json после стандартных провайдеров, поэтому JSON может перекрыть их значения. Это нужно исправить.

Firebase создаётся безусловно. Наличие NullPushProvider не даёт выключить Firebase конфигурацией в текущем DI.

## Развёртывание и проверка

Dockerfile устарел: использует .NET 8 и ссылается на удалённые Govor.Core/Govor.Data. Solution также ссылается на внешний проект SmartRes. До исправления используйте сборку конкретного API-проекта.

HTTPS redirection отключён; TLS и forwarded headers требуют отдельной настройки. SessionKeysController содержит RequireHttps, поэтому HTTP для его методов не подходит.

`GET /server/ping` — маршрут проверки доступности, но возвращает MVC OkResult из minimal API (предупреждение ASP0004); не считайте его полноценным health check БД и Firebase.

Swagger включён во всех окружениях. UseSwagger обслуживает документ через middleware; поздний MapSwagger.RequireAuthorization не является надёжным подтверждением защиты всех Swagger-маршрутов.

## Что проверено

Обновление 24.10.2026: серверные тесты обновлены до .NET 10 и находятся в `tests`. `dotnet test Govor.Server.slnx -c Release` запускает все четыре проекта. Release-сборка и 710 тестов проходят на SQLite и отдельной PostgreSQL с применением миграций. Инструкции и ограничения — в [tests/README.md](../tests/README.md). Production, реальная доставка Firebase и контейнер в рамках этой проверки не запускались.

---
Проверено по исходникам на 03.10.2026, commit `4603be9`. Описано текущее поведение; успешная сборка не означает проверку работающего сервера.

Источник: [Govor.API/Program.cs](https://github.com/Govor-team/Govor/blob/4603be9f714135e0af72d505d17f3d1709834bea/Govor.API/Program.cs).
