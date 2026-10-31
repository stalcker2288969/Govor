# Устройства, ключи и архивы — HTTP API

Все маршруты `/api/e2ee` требуют access token, роль User/Admin и активную сессию. `type`: `0` — PrivateChat, `1` — ChatGroup. Приватные ключи устройства, пароль и резервный код серверу не передаются. [Модель доверия и жизненный цикл](../e2ee.md).

## Маршруты

| Метод | Маршрут | Результат |
| --- | --- | --- |
| PUT | `/api/e2ee/device` | Регистрация/ротация ключа текущей сессии; `{ registered: true }` |
| GET | `/api/e2ee/devices` | Собственные устройства |
| PUT | `/api/e2ee/devices/certificate` | Сертификация исторического устройства; `{ certified: true }` |
| GET | `/api/e2ee/conversations/{type}/{conversationId}/keys` | `ConversationKeys` для доступного чата |
| GET | `/api/e2ee/conversations/{type}/{conversationId}/signers/{deviceSession}?identityKey=...` | Исторический подписант; `404`, если не найден |
| GET | `/api/e2ee/recovery` | `RecoveryState`: `{ vault: ... }`, без архива `vault: null` |
| PUT | `/api/e2ee/recovery` | Сохранение подписанного архива с ожидаемой ревизией |
| GET | `/api/e2ee/identities/{userId}/changes` | Журнал явной смены корневого ключа |

## Контракты

`DeviceKey`: `userId`, `sessionId`, `identityKey`, `encryptionKey`, `signature`, `accountIdentityKey`, `accountSignature`, `generation`, `previousFingerprint`. `fingerprint` вычисляется из публичных ключей. Идентификаторы обязаны совпасть с владельцем JWT и текущей сессией.

`ConversationKeys`: `conversationId`, `recipientType`, `members` (ID пользователей), `devices` (DeviceKey). Не путайте список участников с устройствами: у одного пользователя может быть несколько устройств. Лимит — 500 устройств на чат. Истёкшая, но не отозванная сертифицированная сессия может оставаться получателем офлайн-сообщений; для авторизации команд срок сессии всё равно проверяется.

`SaveRecoveryVault`: `expectedRevision`, `identityKey`, `ciphertext`, `signature`. `RecoveryVault`: `userId`, `revision`, `identityKey`, `ciphertext`, `signature`. Предел ciphertext архива — 4 000 000 символов, HTTP-запроса сохранения — 4 100 000 байт.

При конфликте ревизии перечитайте архив, расшифруйте и объедините ключи локально, затем подпишите следующую версию. Простая замена expectedRevision без объединения может потерять историю другого устройства.

## Ошибки и повторы

- `400`: неверный тип/контекст, ключи, подпись или данные архива.
- `403`: нет доступа к чату, чужая сессия/устройство или неподходящий ключ аккаунта.
- `404`: не найден исторический подписант.
- `409`: изменилось поколение/ревизия либо не у всех участников есть подходящие ключи.

Перед отправкой получите каталог устройств, зашифруйте сообщение всем получателям и подпишите. При `E2EE_DEVICES_CHANGED` перечитайте каталог и создайте согласованный конверт; не переходите на plaintext. Для личных и приватных чатов сервер требует E2EE; публичные группы допускают старый формат, хотя актуальный клиент шифрует и их.

Точный формат JOSE, подписи и кодирование заданы в [E2eeProtocol.cs](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.Application/E2ee/E2eeProtocol.cs) и [E2eeRecoveryProtocol.cs](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.Application/E2ee/E2eeRecoveryProtocol.cs). HTTP camelCase не означает, что можно переименовывать поля внутри подписанного JOSE-содержимого.
