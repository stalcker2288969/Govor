# Личные чаты

Требуется роль User/Admin.

| Метод | Маршрут | Успех |
| --- | --- | --- |
| GET | /api/user/{friendId}/private-chat | 200, Guid чата в JSON |
| GET | /api/user/private-chats | 200, PrivateChatDto[] |

Первый метод проверяет дружбу, возвращает существующий чат либо создаёт новый. Это побочный эффект GET; рекомендуемый будущий контракт — POST, но маршрут пока не изменён.

```json
[{
  "chatId":"11111111-1111-1111-1111-111111111111",
  "friendId":"22222222-2222-2222-2222-222222222222"
}]
```

При принятии заявки FriendRequestCommandService также создаёт личный чат. ChatsHub использует группы сессий; ручная подписка клиента на чат не требуется.

Используйте chatId в Send.recipientId и в /api/user/{userId}/messages. Несмотря на имя параметра userId, загрузчик ожидает ID PrivateChat.

Отсутствие дружбы/доступа возвращает 403 без обещания тела ProblemDetails. Некорректный аргумент — 400. При последовательном вызове существующий чат возвращается повторно. Сервис сначала ищет пару, затем создаёт запись; полную гарантию идемпотентности при конкурентном создании этот маршрут не обещает.


Источник: [Govor.API/Controllers/PrivateChatController.cs](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/PrivateChatController.cs).

