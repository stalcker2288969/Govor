# Govor

Сервер мессенджера на ASP.NET Core **.NET 10**: PostgreSQL/EF Core, JWT, SignalR, Firebase и локальное хранилище файлов. Клиент .NET MAUI разрабатывается в отдельном репозитории.

Реализованы регистрация по приглашению, SRP-вход и управление сессиями, друзья, личные чаты, группы/каналы, сообщения и вложения с E2EE, восстановление истории, реакции, прочтение и очередь push.

Начните с [документации](docs/README.md), [локального запуска](docs/getting-started.md) и [карты HTTP API](docs/api-reference.md). Публичный [GitBook](https://nas-3.gitbook.io/govor-api) содержит руководства и справочник; сведения о синхронизации — в [правилах актуализации](docs/server-fixes.md).

## Проверка сервера

```powershell
dotnet restore Govor.Server.slnx
dotnet test Govor.Server.slnx -c Release
```

Тесты по умолчанию используют изолированную SQLite. [Полное руководство](docs/testing.md) · [Устройство тестовых проектов](tests/README.md).

## Развёртывание

[Выбор схемы](docs/deployment.md) · [Timeweb VPS + GitHub Actions](docs/vps-deployment.md) · [Timeweb App Platform](docs/timeweb-deployment.md).

Секреты задаются в runtime, миграции применяются отдельным запуском `--migrate-only`, uploads требует постоянного хранилища. [Конфигурация](docs/configuration.md) · [Резервные копии](docs/operations.md).

Границы шифрования и доверия описаны в [E2EE](docs/e2ee.md); протокол не предоставляет FS/PCS и не является реализацией Signal/MLS.
