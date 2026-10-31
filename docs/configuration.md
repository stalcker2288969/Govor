# Конфигурация

Настройки загружаются стандартными провайдерами ASP.NET Core: `appsettings.json`, файл окружения, переменные окружения и аргументы командной строки. В переменных окружения вложенность обозначается двойным подчёркиванием: `JwtAccessOption:SecretKey` → `JwtAccessOption__SecretKey`.

Дополнительно загружается обязательный `configs/ban_usernames.json` из ContentRoot. Он предназначен для `UsernameModeration`; не размещайте в нём другие параметры, перекрывающие runtime-конфигурацию. Файлы `secrets/` исключены из публикации и предоставляются отдельно.

## Параметры

| Переменная окружения | Назначение / значение по умолчанию |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | Локально `Development`, на сервере `Production` |
| `ConnectionStrings__GovorDbContext` | Подключение PostgreSQL; в Git пустое |
| `JwtAccessOption__SecretKey` | Секрет HS256; вне Development минимум 32 байта UTF-8 |
| `JwtAccessOption__Minutes` | Access token: 10 минут |
| `JwtRefreshOption__RefreshTokenLifetimeDays` | Refresh-сессия: 30 дней; продлевается при refresh |
| `InvitationAdmin__Key` | Необязательный секрет создания приглашений без JWT; без значения endpoint отключён (503) |
| `EncryptionOption__Secret` | Секрет хеширования refresh-токенов; в Git пустой |
| `MessageEditingOptions__Enabled` | Ограничение времени редактирования: `true` |
| `MessageEditingOptions__MaxEditTimeMinutes` | Окно редактирования: 15 минут |
| `Firebase__Enabled` | Отправка push; в поставляемом JSON `false` |
| `Firebase__CredentialsJson` | JSON service account; приоритетнее пути |
| `Firebase__CredentialsPath` | Путь к service account; относительно ContentRoot |
| `GOOGLE_APPLICATION_CREDENTIALS` | Путь, если `Firebase:CredentialsPath` не задан |
| `Cors__AllowedOrigins__0` | Разрешённый browser-origin, например `https://client.example.org`; следующие — `__1`, `__2` |
| `ASPNETCORE_HTTP_PORTS` | HTTP-порт контейнера, например `8080` |

Без явного пути Firebase используется `secrets/firebase-adminsdk.json`. Неправильные credentials при включённом Firebase останавливают запуск. Если ключ `Firebase:Enabled` отсутствует вообще, код использует `true`; штатный JSON явно задаёт `false`.

Вне Development нужны строка подключения, секрет JWT и `EncryptionOption:Secret`. `--migrate-only` требует подключение, но пропускает проверку секретов JWT/refresh и создание Firebase. Не используйте Development для обхода production-проверок.

## Хранилище и сеть

`uploads/` хранится относительно каталога API. В контейнере вынесите его в постоянный bind mount. Потеря файлов означает потерю вложений даже при сохранённой БД. Согласованно копируйте БД и файлы; [эксплуатация](operations.md).

API не включает HTTPS redirection. Внешний TLS обычно завершает reverse proxy. Для браузерного клиента настройте точные CORS origins. CORS не заменяет авторизацию и не ограничивает нативный MAUI-клиент.

Ограничитель `password-auth` считает `RemoteIpAddress`. В `Program.cs` нет настроенного `UseForwardedHeaders`: за прокси адресом может оказаться сам прокси, а `[RequireHttps]` старого API ключей может не распознать внешний HTTPS. TLS у прокси не настраивает forwarded headers в приложении автоматически.

Swagger middleware включён во всех окружениях. Для закрытого API ограничьте `/swagger` на прокси: поздний `MapSwagger().RequireAuthorization()` не защищает все пути, уже обслуженные `UseSwagger()`.

Секреты, тома и журналы для [VPS](vps-deployment.md) и [App Platform](timeweb-deployment.md) настраиваются по-разному.
