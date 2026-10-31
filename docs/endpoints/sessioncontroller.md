# Сессии

Все методы требуют Bearer access token с ролью User или Admin.

| Метод | Маршрут | Успех |
| --- | --- | --- |
| GET | /api/Session/all | 200, SessionDto[] |
| DELETE | /api/Session/close/{sessionId} | 204, без тела |
| DELETE | /api/Session/close | 204, закрыть sid текущего токена |
| DELETE | /api/Session/close/all | 204, закрыть все сессии пользователя |

```json
[{
  "id":"11111111-1111-1111-1111-111111111111",
  "deviceInfo":"Android test device",
  "createdAt":"2026-10-03T00:00:00Z",
  "expiresAt":"2026-10-10T00:00:00Z",
  "isRevoked":false
}]
```

Список фильтруется по UserId и !IsRevoked, но не по ExpiresAt; истекшие сессии могут присутствовать. CreatedAt сохраняет дату первоначального открытия сессии.

Закрытие чужой, неизвестной или уже отозванной сессии возвращает 404 с errorCode UserSession.NotFoundOrUnauthorized. Guid.Empty возвращает 400. CloseAll без активных сессий тоже успешен.

Отзыв деактивирует связанные push-токены. Успех Unit преобразуется в 204, а не в 200.

## Действие отзыва

Bearer middleware проверяет sid, владельца, срок и отзыв по БД. SignalR-фильтр повторяет проверку перед каждой командой и прерывает соединение при отказе. ChatsHub исключает отозванные сессии из маршрутизации. Контроллер не гарантирует мгновенное физическое закрытие всех соединений других хабов; клиент останавливает reconnect и закрывает соединения при logout.

Отзыв исключает устройство из новых E2EE-получателей, но не удаляет уже имеющиеся на нём ключи, кэш и прочитанную историю.


Источник: [Govor.API/Controllers/SessionController.cs](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/SessionController.cs).
