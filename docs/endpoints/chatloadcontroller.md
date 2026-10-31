# История сообщений

Требуется роль User/Admin.

| Метод | Маршрут | Идентификатор |
| --- | --- | --- |
| GET | /api/user/{userId}/messages | В userId передаётся **ID PrivateChat** |
| GET | /api/groups/{groupId}/messages | ID ChatGroup |

Query: StartMessageId (Guid?, необязателен), Before=20, After=2. Before и After неотрицательные, сумма ≤100.

Без StartMessageId возвращаются последние Before сообщений, упорядоченные по SentAt по возрастанию; After не используется. С якорем возвращаются Before старых, якорное сообщение и After новых: максимум 101 запись. Неизвестный якорь — [].

Успех 200, MessageResponse[]:

```json
[{
  "id":"11111111-1111-1111-1111-111111111111",
  "senderId":"22222222-2222-2222-2222-222222222222",
  "recipientId":"33333333-3333-3333-3333-333333333333",
  "recipientType":0,
  "encryptedContent":"<строка>",
  "sentAt":"2026-10-03T00:00:00Z",
  "isEdited":false,
  "editedAt":null,
  "replyToMessageId":null,
  "mediaAttachments":[],
  "reactions":[],
  "reactionsVersion":0,
  "messageViews":[]
}]
```

У HTTP-ответа поле id; у SignalR UserMessageResponse — messageId. Вложения HTTP имеют id, messageId, mediaFileId.

## Доступ и пагинация

Оба загрузчика проверяют доступ к конкретному чату: неучастник/заблокированный участник получает 403. Реакции, прочтения и вложения загружаются явно. Порядок стабилен по паре (SentAt, Id), включая одинаковое время отправки.

Якорь включается в результат; при объединении страниц удаляйте дубли по id. Пустой массив может означать пустую историю или неизвестный якорь. encryptedContent возвращается как сохранённый конверт и расшифровывается клиентом.


Источник: [Govor.Application/Messages/MessagesLoader.cs](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.Application/Messages/MessagesLoader.cs).
