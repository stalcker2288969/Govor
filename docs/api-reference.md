# Карта HTTP API

Карта составлена по действующим атрибутам контроллеров ревизии `a3401ce`. Закомментированные методы не включены. Тела запросов, ответы и права описаны в связанных разделах; точную runtime-схему DTO также показывает `/swagger/v1/swagger.json` установленного сервера. Swagger может быть ограничен оператором.

По умолчанию нужен Bearer access token и активная сессия. Без JWT доступны register/login/challenge/login, refresh, проверки server и InvitationWithKey (последний требует adminKey). Остальные методы не становятся публичными от наличия ссылки-приглашения. `purpose` в маршрутах пароля — только `change` или `reset`. Числовые типы чата: 0 — личный, 1 — группа.

Для методов без тела успех может быть 204; не пытайтесь всегда разбирать JSON. Форматы ошибок — [общий контракт](code-docs/hubresult-less-than-t-greater-than.md).

## Доступность

| Метод | Маршрут | Назначение |
| --- | --- | --- |
| GET | `/server/ping` | Процесс доступен |
| GET | `/server/ready` | БД доступна и миграции применены |

## InviteUserController

[Подробное описание](endpoints/admin-and-groups.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/AdminStuff/InviteUserController.cs)

| Метод | Маршрут |
| --- | --- |
| POST | `/api/admin/InviteUser/Invitation` |
| POST | `/api/admin/InviteUser/InvitationWithKey` |
| GET | `/api/admin/InviteUser/GetAllActiveInvitations` |
| GET | `/api/admin/InviteUser` |
| GET | `/api/admin/InviteUser/{id}` |

## ReactionPacksAdminController

[Подробное описание](endpoints/reactions.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/AdminStuff/ReactionPacksAdminController.cs)

| Метод | Маршрут |
| --- | --- |
| GET | `/api/admin/reaction-packs` |
| GET | `/api/admin/reaction-packs/{packId}` |
| POST | `/api/admin/reaction-packs` |
| PUT | `/api/admin/reaction-packs/{packId}` |
| DELETE | `/api/admin/reaction-packs/{packId}` |
| POST | `/api/admin/reaction-packs/{packId}/reactions/emoji` |
| POST | `/api/admin/reaction-packs/{packId}/reactions/media` |
| DELETE | `/api/admin/reaction-packs/{packId}/reactions/{reactionId}` |

## RequiredChannelController

[Подробное описание](endpoints/admin-and-groups.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/AdminStuff/RequiredChannelController.cs)

| Метод | Маршрут |
| --- | --- |
| GET | `/api/admin/required-channel` |
| PUT | `/api/admin/required-channel` |

## UsersController

[Подробное описание](endpoints/admin-and-groups.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/AdminStuff/UsersController.cs)

| Метод | Маршрут |
| --- | --- |
| GET | `/api/admin/Users/all` |
| GET | `/api/admin/Users/{id}` |
| POST | `/api/admin/Users/user/{id}/password` |

## AuthController

[Подробное описание](endpoints/authentication/authcontroller.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/Authentication/AuthController.cs)

| Метод | Маршрут |
| --- | --- |
| POST | `/api/Auth/register` |
| POST | `/api/Auth/login` |
| POST | `/api/Auth/password/challenge` |
| POST | `/api/Auth/password/login` |
| GET | `/api/Auth/password` |
| PUT | `/api/Auth/password/enrollment` |
| PUT | `/api/Auth/password/recovery` |
| POST | `/api/Auth/password/{purpose}/challenge` |
| POST | `/api/Auth/password/{purpose}/authorize` |
| POST | `/api/Auth/password/change` |
| POST | `/api/Auth/password/reset-encryption` |

## RefreshController

[Подробное описание](endpoints/authentication/refreshcontroller.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/Authentication/RefreshController.cs)

| Метод | Маршрут |
| --- | --- |
| POST | `/api/auth/token/refresh` |

## SessionKeysController

[Подробное описание](endpoints/session-keys.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/Authentication/SessionKeysController.cs)

| Метод | Маршрут |
| --- | --- |
| POST | `/api/session/keys` |
| GET | `/api/session/users/{userId}/keys` |
| POST | `/api/session/keys/rotate` |
| GET | `/api/session/keys/remaining` |
| POST | `/api/session/keys/{preKeyId}/used` |

## ChannelReactionsController

[Подробное описание](endpoints/reactions.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/ChannelReactionsController.cs)

| Метод | Маршрут |
| --- | --- |
| GET | `/api/groups/{groupId}/reactions` |
| PUT | `/api/groups/{groupId}/reactions` |

## ChatLoadController

[Подробное описание](endpoints/chatloadcontroller.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/ChatLoadController.cs)

| Метод | Маршрут |
| --- | --- |
| GET | `/api/groups/{groupId}/messages` |
| GET | `/api/user/{userId}/messages` |

## E2eeController

[Подробное описание](endpoints/e2ee.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/E2eeController.cs)

| Метод | Маршрут |
| --- | --- |
| PUT | `/api/e2ee/device` |
| GET | `/api/e2ee/recovery` |
| GET | `/api/e2ee/identities/{peer}/changes` |
| GET | `/api/e2ee/devices` |
| PUT | `/api/e2ee/devices/certificate` |
| GET | `/api/e2ee/conversations/{type}/{conversationId}/signers/{deviceSession}` |
| PUT | `/api/e2ee/recovery` |
| GET | `/api/e2ee/conversations/{type}/{conversationId}/keys` |

## FriendshipController

[Подробное описание](endpoints/friendshipcontroller.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/Friends/FriendshipController.cs)

| Метод | Маршрут |
| --- | --- |
| GET | `/api/friends/search` |
| GET | `/api/friends` |

## FriendsRequestQueryController

[Подробное описание](endpoints/friendsrequestquerycontroller.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/Friends/FriendsRequestQueryController.cs)

| Метод | Маршрут |
| --- | --- |
| GET | `/api/friends/requests` |
| GET | `/api/friends/responses` |

## GroupsController

[Подробное описание](groups.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/GroupsController.cs)

| Метод | Маршрут |
| --- | --- |
| POST | `/api/groups` |
| GET | `/api/groups/search` |
| GET | `/api/groups/mine` |
| GET | `/api/groups/{groupId}` |
| PUT | `/api/groups/{groupId}` |
| POST | `/api/groups/{groupId}/join` |
| POST | `/api/groups/{groupId}/avatar` |
| DELETE | `/api/groups/{groupId}/avatar` |
| DELETE | `/api/groups/{groupId}/members/me` |
| GET | `/api/groups/{groupId}/members` |
| PUT | `/api/groups/{groupId}/members/{userId}/administrator` |
| DELETE | `/api/groups/{groupId}/members/{userId}/administrator` |
| PUT | `/api/groups/{groupId}/members/{userId}/ban` |
| DELETE | `/api/groups/{groupId}/members/{userId}/ban` |
| POST | `/api/groups/{groupId}/ownership/{userId}` |
| POST | `/api/groups/{groupId}/invitations` |
| GET | `/api/groups/{groupId}/invitations` |
| DELETE | `/api/groups/{groupId}/invitations/{invitationId}` |
| DELETE | `/api/groups/{groupId}/messages/{messageId}` |

## InviteController

[Подробное описание](groups.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/InviteController.cs)

| Метод | Маршрут |
| --- | --- |
| GET | `/api/group-invites/{code}` |
| GET | `/invite/{code}` |
| POST | `/api/group-invites/{code}/join` |
| POST | `/invite/{code}/join` |

## MediaController

[Подробное описание](endpoints/mediacontroller.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/MediaController.cs)

| Метод | Маршрут |
| --- | --- |
| POST | `/api/media/upload` |
| GET | `/api/media/download/{id}` |

## MessageReactionsController

[Подробное описание](endpoints/reactions.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/MessageReactionsController.cs)

| Метод | Маршрут |
| --- | --- |
| GET | `/api/messages/{messageId}/reactions` |
| PUT | `/api/messages/{messageId}/reactions/me` |
| DELETE | `/api/messages/{messageId}/reactions/me` |

## MessageReadController

[Подробное описание](endpoints/message-read.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/MessageReadController.cs)

| Метод | Маршрут |
| --- | --- |
| POST | `/api/chats/{chatId}/read` |
| GET | `/api/chats/{chatId}/unread-count` |

## OnlinePingingController

[Подробное описание](endpoints/onlinepingingcontroller-ne-rabotaet.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/OnlinePingingController.cs)

| Метод | Маршрут |
| --- | --- |
| PATCH | `/api/online/ping` |
| GET | `/api/online/status/{userId}` |

## PrivateChatController

[Подробное описание](endpoints/private-chats.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/PrivateChatController.cs)

| Метод | Маршрут |
| --- | --- |
| GET | `/api/user/{friendId}/private-chat` |
| GET | `/api/user/private-chats` |

## ProfileController

[Подробное описание](endpoints/profile.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/ProfileController.cs)

| Метод | Маршрут |
| --- | --- |
| POST | `/api/profile/avatar` |
| GET | `/api/profile/download/me` |
| GET | `/api/profile/download/{id}` |

## PushTokensController

[Подробное описание](endpoints/push.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/PushTokensController.cs)

| Метод | Маршрут |
| --- | --- |
| POST | `/api/pushes/token/register` |
| DELETE | `/api/pushes/token` |

## ReactionPacksController

[Подробное описание](endpoints/reactions.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/ReactionPacksController.cs)

| Метод | Маршрут |
| --- | --- |
| GET | `/api/reaction-packs` |
| GET | `/api/reaction-packs/mine` |
| GET | `/api/reaction-packs/{packId}` |
| GET | `/api/reaction-packs/shared/{shareCode}` |
| GET | `/api/reactions/{reactionId}` |
| PUT | `/api/reaction-packs/{packId}/subscription` |
| DELETE | `/api/reaction-packs/{packId}/subscription` |
| GET | `/api/reaction-packs/limits` |

## SessionController

[Подробное описание](endpoints/sessioncontroller.md) · [Исходник](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/SessionController.cs)

| Метод | Маршрут |
| --- | --- |
| GET | `/api/Session/all` |
| DELETE | `/api/Session/close/{sessionId}` |
| DELETE | `/api/Session/close` |
| DELETE | `/api/Session/close/all` |

## SignalR

Команды сообщений находятся в [ChatsHub](signalr/chathub.md), заявки — в [FriendsHub](friendshub-zayavki.md). Подключения: `/hubs/chats`, `/hubs/friends`, `/hubs/presence`, `/hubs/profiles`. Пустой GroupsHub не опубликован как рабочий endpoint.
