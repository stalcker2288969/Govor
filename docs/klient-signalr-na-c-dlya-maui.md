# Клиент SignalR на C# для MAUI

Эта страница заменяет устаревший Java-пример: он принимал FriendshipDto как String и не соответствовал серверу. Ниже минимальный пример подключения C#; это пример интеграции, а не проверенный end-to-end тест.

Используйте Microsoft.AspNetCore.SignalR.Client и Govor.Contracts либо эквивалентные DTO с теми же полями.

```csharp
using Govor.Contracts.DTOs;
using Govor.Contracts.Responses.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

var connection = new HubConnectionBuilder()
    .WithUrl($"{baseUrl.TrimEnd('/')}/hubs/friends", options =>
    {
        options.AccessTokenProvider = async () =>
            await jwtProvider.GetAccessTokenAsync();
    })
    .WithAutomaticReconnect()
    .Build();

using var received = connection.On<FriendshipDto>(
    "FriendRequestReceived", dto =>
    {
        MainThread.BeginInvokeOnMainThread(() => ApplyFriendship(dto));
    });

using var accepted = connection.On<FriendshipDto>(
    "YourFriendRequestAccepted", dto =>
    {
        MainThread.BeginInvokeOnMainThread(() => ApplyFriendship(dto));
    });

await connection.StartAsync();

var response = await connection.InvokeAsync<HubResult<object>>(
    "SendRequest", targetUserId);

if (response.Status != HubResultStatus.Created)
    ShowError(response.ErrorMessage);
```

baseUrl, jwtProvider, ApplyFriendship, ShowError и targetUserId предоставляет приложение. Зарегистрируйте остальные четыре события аналогично с FriendshipDto. AcceptRequest/RejectRequest получают friendshipId.

## Жизненный цикл MAUI

1. Подпишитесь на события до подключения.
2. Первоначальный StartAsync требует собственной обработки повторов; automatic reconnect относится к потерянному соединению.
3. После Reconnected перечитайте HTTP-списки, чтобы восстановить пропущенные события.
4. Изменяйте ObservableCollection на MainThread.
5. Отменяйте повторные попытки при logout и DisposeAsync; не запускайте бесконечный StartAsync из Closed после намеренного StopAsync.
6. AccessTokenProvider должен получать актуальный access token; refresh должен быть сериализован.

Для ChatsHub даже сообщение только с вложением содержит E2EE-конверт. Объединяйте события и HTTP по messageId/id. В собственном клиенте отдельно обработайте временные ошибки refresh, остановку reconnect при logout и восстановление состояния после reconnect. [Полная последовательность интеграции](client-integration.md).


Источник: [Govor.API/Hubs/FriendsHub.cs](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Hubs/FriendsHub.cs).
