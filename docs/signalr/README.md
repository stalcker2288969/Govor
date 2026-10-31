# SignalR — команды и события

SignalR дополняет HTTP: команды меняют состояние, события уведомляют подключённые клиенты, HTTP восстанавливает снимок. Для подключения используйте официальный SignalR-клиент с Bearer access token, а не произвольный WebSocket JSON.

| Хаб | Контракт |
| --- | --- |
| `/hubs/chats` | [Сообщения, прочтение, реакции и сообщества](chathub.md) |
| `/hubs/friends` | [Заявки и их результаты](../friendshub-zayavki.md) |
| `/hubs/presence` | [Online/offline](presencehub.md) |
| `/hubs/profiles` | [Описание и аватар](profilehub.md) |

Все подключения требуют действующей access-сессии. Общий фильтр повторяет проверку перед командой. ChatsHub/ProfileHub используют Authorize, FriendsHub/PresenceHub дополнительно задают User/Admin. Не передавайте refresh как access.

## Общие правила клиента

1. Зарегистрировать обработчики до StartAsync; access token возвращать через AccessTokenProvider.
2. Проверить HubResult.status после InvokeAsync, отдельно от состояния соединения.
3. Объединять повторные события и HTTP по ID, учитывать версии реакций.
4. После reconnect перечитать доступное HTTP-состояние и права.
5. При logout отменить повторные попытки, остановить и освободить соединения.

Automatic reconnect не заменяет собственную обработку первоначального StartAsync. События не имеют долговременной очереди и могут прийти в другом порядке при параллельных изменениях. Успех команды не гарантирует, что каждый клиент получил событие. [Путь интеграции](../client-integration.md) · [Пример C#](../klient-signalr-na-c-dlya-maui.md) · [Формат HubResult](../code-docs/hubresult-less-than-t-greater-than.md).
