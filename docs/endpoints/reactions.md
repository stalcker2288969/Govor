# Реакции, паки и настройки каналов

Контракт рабочей версии сервера от 03.10.2026. Все маршруты требуют Bearer **access** token и активную сессию. JSON использует camelCase, перечисления передаются числами. HTTP и SignalR вызывают один сервис с одинаковыми проверками доступа.

## Реакции сообщения

На одном сообщении пользователь может иметь одну реакцию: новая заменяет предыдущую. Изменять чужую реакцию нельзя; userId берётся из токена. Доступ имеют участники личного чата и незаблокированные участники группы/канала.

| Метод | Маршрут | Тело / результат |
| --- | --- | --- |
| GET | `/api/messages/{messageId}/reactions` | Снимок реакций |
| PUT | `/api/messages/{messageId}/reactions/me` | `{ "reactionId": "<GUID>" }`; снимок |
| DELETE | `/api/messages/{messageId}/reactions/me` | Без тела; снимок |

Успех — HTTP 200. Повтор PUT той же реакции и DELETE отсутствующей реакции идемпотентны: не повышают version и не отправляют событие. PUT требует активную реакцию активного пака. Ошибки: 400 — некорректный ID; 401 — нет активной access-сессии; 403 — нет доступа или запрещено настройками канала; 404 — сообщение/реакция недоступны.

Снимок и событие `MessageReactionsChanged` имеют одинаковый формат:

```json
{
  "messageId": "11111111-1111-1111-1111-111111111111",
  "recipientId": "22222222-2222-2222-2222-222222222222",
  "recipientType": 0,
  "version": 3,
  "actorId": "33333333-3333-3333-3333-333333333333",
  "actorReaction": null,
  "counts": [
    { "reactionId": "b3000000-0000-0000-0001-000000000001", "reactionCode": "👍", "count": 2 }
  ]
}
```

В GET actorId — текущий пользователь. В событии — пользователь, который изменил реакцию. actorReaction — его текущая MessageReactionResponse либо null после удаления. Не принимайте чужую actorReaction за свою. В counts передаётся полный снимок счётчиков, а не приращения. recipientType: 0 — личный чат (recipientId = PrivateChat.Id), 1 — группа/канал.

История сообщений содержит reactionsVersion и reactions с reactionId, reactionCode, userId и reactedAt. Старая неизвестная reactionCode может иметь reactionId=null; отображайте её как сохранённый код. Миграция связывает известные старые эмодзи с базовым паком.

## SignalR и синхронизация клиента

Подключение: `/hubs/chats`, обычный access token. Методы возвращают существующий `HubResult<T>` с status, result, errorMessage:

```csharp
var result = await connection.InvokeAsync<HubResult<MessageReactionsChangedResponse>>(
    "React", messageId, new SetReactionRequest { ReactionId = reactionId });
var removed = await connection.InvokeAsync<HubResult<MessageReactionsChangedResponse>>(
    "RemoveReaction", messageId);

connection.On<MessageReactionsChangedResponse>("MessageReactionsChanged", state =>
{
    // На UI-потоке: принять более новую версию и заменить counts целиком.
    // actorReaction обновляет собственный выбор только при actorId == currentUserId.
});
connection.On<ChannelReactionPolicyResponse>("ChannelReactionPolicyChanged", policy =>
{
    // Обновить разрешённые реакции канала по более новой version.
});
```

Событие после сохранения получают активные сессии обоих участников личного чата или незаблокированных участников группы, включая устройства автора изменения. Для этих событий push не отправляется. События SignalR не имеют долговременной очереди: после reconnect загрузите HTTP-снимки видимых сообщений и политику канала. Подпишитесь на события до загрузки истории и сравнивайте version отдельно для каждого сообщения, чтобы запоздавший ответ не затёр новое состояние. Ответ команды и событие могут дублировать одну версию.

## Каталог, установка и обмен

Базовый пак Emoji доступен каждому автоматически, не требует установки и не отключается. Его ID — `b3000000-0000-0000-0000-000000000001`; содержит 👍 👎 ❤️ 🔥 🥰 👏 😁 🤔 😢 🎉 🤯 🙏. Пользовательские паки публичны среди авторизованных пользователей. Установка добавляет пак в личный выбор; применять публичную реакцию можно и без установки.

| Метод | Маршрут | Назначение |
| --- | --- | --- |
| GET | `/api/reaction-packs?skip=0&take=50` | Активный каталог |
| GET | `/api/reaction-packs/mine?skip=0&take=50` | Базовый и установленные паки |
| GET | `/api/reaction-packs/{packId}` | Активный пак с активными реакциями |
| GET | `/api/reaction-packs/shared/{shareCode}` | Открыть пак по коду обмена |
| PUT | `/api/reaction-packs/{packId}/subscription` | Установить; HTTP 204 |
| DELETE | `/api/reaction-packs/{packId}/subscription` | Удалить из выбора; HTTP 204 |
| GET | `/api/reactions/{reactionId}` | Определение реакции, включая отключённую |
| GET | `/api/reaction-packs/limits` | Лимиты файлов и пака |

skip >= 0, take от 1 до 100. Возвращается список паков. Каждый пак: id, name, description, shareCode, sharePath, isDefault, isEnabled, createdByUserId, createdAt, reactions. sharePath — относительный API-маршрут; клиент может передавать shareCode в своей deep link. Отдельного механизма отправки приглашения/сообщения сервер не добавляет.

Элемент reactions: id, packId, code, name, kind, emoji, mediaFileId, mediaUrl, width, height, sizeBytes, durationMilliseconds, isEnabled, createdAt. kind: Emoji=0, Image=1, Gif=2. Для файлов используйте mediaUrl (`/api/media/download/{id}`) с Bearer token. Файлы реакций доступны всем авторизованным пользователям, включая исторические файлы отключённых реакций. ID файла не даёт доступ к обычным закрытым вложениям чата.

При отображении незнакомого reactionId получите `/api/reactions/{reactionId}` и кешируйте определение/изображение. Идентичность и файл реакции неизменяемы; для замены изображения администратор создаёт новый элемент.

## Администрирование паков

Требуется роль Admin; операции изменения дополнительно проверяют административное приглашение пользователя в БД, согласно существующей модели прав проекта. Будущий бот может использовать эти маршруты с access-сессией административного пользователя. Выдача отдельной bot-учётной записи/токена пока не реализована.

| Метод | Маршрут | Тело |
| --- | --- | --- |
| GET | `/api/admin/reaction-packs?skip=0&take=50` | Каталог, включая отключённые паки и элементы |
| GET | `/api/admin/reaction-packs/{packId}` | Пак, включая отключённые элементы |
| POST | `/api/admin/reaction-packs` | `{ "name": "Cats", "description": "..." }` |
| PUT | `/api/admin/reaction-packs/{packId}` | `{ "name": "Cats", "description": "...", "isEnabled": true }` |
| DELETE | `/api/admin/reaction-packs/{packId}` | Мягкое отключение пака; 204 |
| POST | `/api/admin/reaction-packs/{packId}/reactions/emoji` | `{ "name": "Cool", "emoji": "😎" }` |
| POST | `/api/admin/reaction-packs/{packId}/reactions/media` | multipart/form-data: name и file |
| DELETE | `/api/admin/reaction-packs/{packId}/reactions/{reactionId}` | Мягкое отключение элемента; 204 |

Создание/обновление возвращает HTTP 200 с DTO. name — 1–100 символов, description — до 500. До 100 элементов в паке, включая отключённые. Базовый пак и его элементы неизменяемы. Отключение исключает пак/элемент из выбора и запрещает новые реакции, но сохраняет ранее поставленные реакции и файлы; пользователь может снять свою старую реакцию. Отдельного события изменения каталога нет: обновляйте каталог при открытии выбора и после отказа установки реакции.

## Файлы

Формат определяется по содержимому: PNG, JPEG, WebP для статичных картинок; GIF для анимации. SVG и анимации других форматов отклоняются. Имя файла и заявленный MIME не определяют результат. Сервер декодирует, удаляет необязательные метаданные, уменьшает и заново кодирует файл. Картинки сохраняются как PNG, анимации как GIF.

| Ограничение | Значение |
| --- | --- |
| Исходный файл | До 2 МиБ, стороны до 2048 пикселей |
| Результат | Обе стороны не больше 128 пикселей |
| Картинка | До 256 КиБ |
| GIF | До 512 КиБ, до 60 кадров, до 5 секунд за цикл |

Для достижения лимита размера сервер может уменьшить стороны до 96, 64 или 48 пикселей. Задержка кадра GIF нормализуется минимум до 20 мс; лимит длительности проверяется после нормализации. Слишком сложный/повреждённый файл возвращает 400; тело запроса выше транспортного лимита может вернуть 413.

Обработка использует SixLabors.ImageSharp 3.1.12 с Six Labors Split License; пакет не требует лицензионного ключа для сборки. Условия использования доступны в [пакете NuGet](https://www.nuget.org/packages/SixLabors.ImageSharp/3.1.12). Переход на ImageSharp 4 требует отдельно настроить build-time license key.

## Настройки канала

Группы с IsChannel=true поддерживают ограничения. Новые и существующие каналы после миграции используют All. Управлять политикой может незаблокированный участник, записанный в GroupAdmins этого канала; глобальная роль Admin сама по себе не даёт этих прав.

| Метод | Маршрут | Результат |
| --- | --- | --- |
| GET | `/api/groups/{groupId}/reactions` | Политика для участников |
| PUT | `/api/groups/{groupId}/reactions` | `{ "mode": 2, "reactionIds": ["<GUID>"] }`; обновлённая политика |

mode: All=0 — все активные реакции; None=1 — новые реакции запрещены; Selected=2 — только выбранные ID (до 100). Для All/None reactionIds должен быть пустым. Пустой Selected запрещает все новые реакции. Выбираются только активные элементы активных паков. Результат: groupId, mode, version, reactionIds. После успешного PUT приходит ChannelReactionPolicyChanged. Обычная группа не поддерживает PUT политики (400). Изменение политики не удаляет историю; собственную реакцию разрешено снять при любом режиме.

## Обновление базы и проверка

Из корня серверного проекта:

```powershell
dotnet ef database update --project Govor.Domain --startup-project Govor.API
dotnet test tests/Govor.Server.Tests/Govor.Server.Tests.csproj -c Release
```

Миграция `20261003101700_ReactionPacksAndChannelPolicies` создаёт каталог, установки, разрешения канала, версии и базовый пак; старые реакции сохраняются. Она подготовлена, но к вашей рабочей БД автоматически не применялась. Локальные тесты используют SQLite и HTTP pipeline с JWT; SignalR-доставка проверяется через mock транспорта. CI также настроен на PostgreSQL. Реальные устройства/сокеты и PostgreSQL локально не проверены.
