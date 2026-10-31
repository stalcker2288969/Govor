# ChatsHub — сообщения

Подключение: `/hubs/chats`, Bearer access token и активная сессия. Команды возвращают `HubResult<T>` (status, result, errorMessage); события приходят независимо от ответа команды. Контракт сверяется с серверной ревизией, указанной в обзоре.

## Команды

| Имя            | Аргументы                            | Успешный result                 |
| -------------- | ------------------------------------ | ------------------------------- |
| Send           | MessageRequest                       | UserMessageResponse             |
| Read           | `{ messageId }`                      | MessageReadResponse             |
| ReadChat       | chatId, ReadChatRequest              | ChatReadResponse                |
| Edit           | `{ messageId, newEncryptedContent }` | MessageEditResponse             |
| Remove         | `{ messageId, requestType }`         | MessageRemovedResponse          |
| React          | messageId, `{ reactionId }`          | MessageReactionsChangedResponse |
| RemoveReaction | messageId                            | MessageReactionsChangedResponse |

```json
{
  "recipientId": "11111111-1111-1111-1111-111111111111",
  "recipientType": 0,
  "encryptedContent": "<содержимое>",
  "replyToMessageId": null,
  "mediaAttachments": []
}
```

RecipientType.User=0 означает личный чат: recipientId — PrivateChat.Id. Group=1 означает ChatGroup.Id. Вложение: `{ mediaId, encryptedKey }`; для E2EE encryptedKey пустой, секрет находится внутри ciphertext. encryptedContent — подписанный конверт с префиксом govor.e2ee.v1:, максимум 1 000 000 символов; до 100 уникальных вложений. Даже сообщение только с файлом содержит E2EE-конверт. Транспортный лимит входящего SignalR-сообщения — 1 100 000 байт, поэтому учитывайте JSON-overhead. SignalR не выполняет MVC DataAnnotations автоматически. DTO проверки дополняются сервисами доступа.

Send/Read/React и чтение истории проверяют участие в конкретном чате. В группах заблокированный участник не имеет доступа. Reply и вложения не позволяют ссылаться на чужие закрытые чаты. Edit проверяет автора, доступ и временной лимит редактирования. Remove проверяет автора/права администратора группы; HideForMe пока не реализован и возвращает ошибку, ForceRemove удаляет сообщение.

[ReadChat и три сценария прочтения](../endpoints/message-read.md): видимые messageIds, граница upToMessageId либо весь чат. [React/RemoveReaction, паки и настройки канала](../endpoints/reactions.md) используют тот же сервис, что HTTP API.

## События

| Имя события (точно)          | Payload                                                         |
| ---------------------------- | --------------------------------------------------------------- |
| ReceiveMessage               | UserMessageResponse                                             |
| MessageSent                  | UserMessageResponse, подтверждение устройствам отправителя      |
| MessageEdited                | MessageEditResponse                                             |
| MessageRemoved               | MessageRemovedResponse                                          |
| MessageReaded                | MessageReadResponse                                             |
| ChatRead                     | ChatReadResponse                                                |
| MessageReactionsChanged      | MessageReactionsChangedResponse                                 |
| ChannelReactionPolicyChanged | ChannelReactionPolicyResponse                                   |
| GroupProfileChanged          | GroupProfileChangedResponse: `{ groupId }`                      |
| GroupMemberChanged           | GroupMemberChangedResponse: `{ groupId, userId, status, role }` |

Опечатка MessageReaded сохранена для совместимости. При подключении сервер автоматически добавляет соединение в группу его сессии. События чата направляются активным сессиям участников; заблокированные участники групп исключаются. Не требуется вручную подписываться на SignalR-группу чата. Отзыв сессии прекращает её участие в маршрутизации и проверяется для команд; срок сессии повторно проверяется перед командой. Мгновенное закрытие соединения только по истечению access JWT не гарантируется конфигурацией; не полагайтесь на него как на механизм logout.

UserMessageResponse: messageId, senderId, recipientId, recipientType, encryptedContent, replyToMessageId, sentAt, isEdited, mediaAttachments. История через HTTP также содержит реакции и reactionsVersion. После получения сообщения загрузите снимок реакций при необходимости.

MessageEditResponse: messageId, editorId, recipientId, recipientType, newEncryptedContent, editedAt. MessageReadResponse: viewId, messageId, readerId, recipientId, whenWas, recipientType. MessageRemovedResponse: messageId, senderId, recipientId, requestType, recipientType.

## Доставка и восстановление

GroupProfileChanged сообщает об изменении имени, описания, приватности, аватара, владельца и настроек обязательного канала. Загрузите GET `/api/groups/{groupId}`: myRole и canLeave индивидуальны, поэтому событие содержит только groupId. GroupMemberChanged сообщает о вступлении/выходе, бане/разбане и роли участника. status: Active=0, Banned=1, Left=2; role: Member=0, Admin=1, Owner=2, null без активного членства. Перечитайте профиль и видимых участников; при потере доступа удалите сообщество из локального списка. Передача владения обновляет профиль и роли обоих пользователей.

Эти события доставляются после успешного HTTP сохранения. Заблокированный/вышедший пользователь получает только изменение собственного членства; последующие события сообщества ему не отправляются. Ошибочные команды не создают событий. Повтор успешной команды может повторить уведомление. [Группы и каналы](../groups.md) описывают все причины событий и клиентское восстановление. Порядок параллельных уведомлений не гарантирован; HTTP состояние определяет актуальные права. После reconnect обновите mine, профиль открытой группы и список участников.

Сохранение в БД и доставка события — разные этапы. После reconnect загрузите историю через HTTP; объединяйте ответ Send и ReceiveMessage по messageId. Для реакций принимайте более новую version и заменяйте counts целиком, затем восстанавливайте состояние видимых сообщений через GET.

SignalR-события не имеют долговременной очереди. Push сообщений использует серверную очередь и повторные попытки; для реакций, политик каналов и изменений сообществ push не отправляется. Реальный push требует корректной конфигурации Firebase и зарегистрированного FCM-токена активной сессии. Для E2EE messageId берётся из подписанного конверта. Повтор того же конверта с тем же автором, чатом, reply и набором вложений возвращает сохранённое сообщение; конфликт содержимого с тем же ID отклоняется. Повтор может повторить уведомления. Создание нового конверта/ID при каждой сетевой попытке всё ещё может породить дубликаты.
