# FriendsHub — заявки

Подключение: /hubs/friends, access token, активная сессия и роль User/Admin. Authorize задан на классе; HubExceptionFilter повторно проверяет сессию перед каждой командой.

## Команды

| Команда       | Аргумент          | Успех                   |
| ------------- | ----------------- | ----------------------- |
| SendRequest   | Guid targetUserId | status 201, result null |
| AcceptRequest | Guid friendshipId | status 200, result null |
| RejectRequest | Guid friendshipId | status 200, result null |

SendRequest запрещает заявку себе и повтор при Pending/Accepted/Blocked. После Rejected запись переиспользуется. Accept разрешён только адресату и только Pending; после сохранения дружбы создаётся личный чат. Reject разрешён адресату для Pending/Rejected.

## Все события

| Событие                   | Кому                        |
| ------------------------- | --------------------------- |
| FriendRequestReceived     | Адресату новой заявки       |
| YourFriendRequestReceived | Отправителю                 |
| FriendRequestAccepted     | Принимающему адресату       |
| YourFriendRequestAccepted | Отправителю исходной заявки |
| FriendRequestRejected     | Отклоняющему адресату       |
| YourFriendRequestRejected | Отправителю исходной заявки |

Каждое событие передаёт **FriendshipDto**, не строку Guid:

```json
{"id":"11111111-1111-1111-1111-111111111111","requesterId":"22222222-2222-2222-2222-222222222222","addresseeId":"33333333-3333-3333-3333-333333333333","status":0}
```

Status: Pending=0, Accepted=1, Rejected=2, Blocked=3.

## Ошибки и восстановление

Сервисные Failure в SendRequest преобразуются в status 500; Accept/Reject — в 400. Поэтому не обещайте 409/403/404 для всех соответствующих бизнес-ошибок.

Дружба сохраняется до создания чата/рассылки; сбой может оставить частично завершённый сценарий. На reconnect перечитывайте /api/friends, /requests, /responses и /api/user/private-chats. Клиенту нужны дедупликация и обновление cache по id.


Источник: [Govor.API/Hubs/FriendsHub.cs](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Hubs/FriendsHub.cs).
