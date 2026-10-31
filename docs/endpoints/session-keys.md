# Старый API prekeys

Эти маршруты сохранены для совместимости и отличаются от действующего [API E2EE устройств](e2ee.md). Они сами по себе не реализуют Signal Protocol, ratchet или архив истории.

Требуются HTTPS, access token и роль User/Admin.

| Метод | Маршрут | Назначение |
| --- | --- | --- |
| POST | `/api/session/keys` | Привязать ключи текущей сессии |
| GET | `/api/session/users/{userId}/keys` | Публичные ключи друга |
| POST | `/api/session/keys/rotate` | Пополнить одноразовые ключи |
| GET | `/api/session/keys/remaining` | `{ remaining }` |
| POST | `/api/session/keys/{preKeyId}/used` | Пометить собственный ключ использованным |

`byte[]` передаются в JSON как Base64. `UploadKeysRequest`: `identityKey`, `signedPreKey`, `signedPreKeySignature`, `oneTimePreKeys`. За upload или rotate допускается до 100 новых prekeys. Rotate получает `newOneTimePreKeys`.

Сервис проверяет владельца сессии; повторная привязка запрещена. Reader возвращает публичные DTO со `signedPreKey` и неиспользованными `oneTimePreKeys`, без внутренних данных UserSession. Конструктор rotator исправлен; прежняя ошибка null больше не описывает текущий сервер.

`used` относится к сессии владельца, а не к атомарной выдаче ключа произвольному собеседнику. Не стройте новый E2EE-клиент на предположении, что это законченный протокол одноразового обмена. За reverse proxy учитывайте [RequireHttps и forwarded headers](../configuration.md).
