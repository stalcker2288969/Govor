# Друзья и поиск

Bearer token обязателен; отдельная роль в атрибуте не задана.

| Метод | Маршрут | Результат |
| --- | --- | --- |
| GET | /api/friends | UserDto[] друзей |
| GET | /api/friends/search?query=Арт | UserDto[] результатов поиска |

```json
[{
  "id":"11111111-1111-1111-1111-111111111111",
  "username":"Артём",
  "description":"",
  "wasOnline":"2026-10-03T00:00:00Z",
  "iconId":"00000000-0000-0000-0000-000000000000",
  "isOnline":false
}]
```

Пустой/пробельный query возвращает 400 строкой "Query cannot be empty". Успех — 200; серверные ошибки — 500 с объектом error. Не смешивайте UserDto и UserProfileDto: у второго нет wasOnline.

Изменение заявок выполняется через FriendsHub. HTTP-контроллер не содержит команды отправки, принятия и отклонения.

Статус isOnline зависит от состояния процесса и подключения к presence. Он не является гарантией доставки сообщений или фоновой доступности устройства.


Источник: [Govor.API/Controllers/Friends/FriendshipController.cs](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/Friends/FriendshipController.cs).
