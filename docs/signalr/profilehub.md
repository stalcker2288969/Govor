# ProfileHub — изменения профиля

Подключение: /hubs/profiles; требуется Authorize.

| Метод | Аргумент | Успех |
| --- | --- | --- |
| SetDescription | string description | HubResult<bool>, status=200, result=true |
| SetAvatar | Guid iconId | HubResult<bool>, status=200, result=true |

Описание не может быть null, переносы строк нормализуются, длина после нормализации ≤500; пустая строка допустима для очистки. Avatar требует непустой Guid и существующую запись медиа.

| Событие | Payload |
| --- | --- |
| DescriptionUpdated | { userId, description } |
| AvatarUpdated | { userId, iconId } |

Получатели: пользователь, друзья и потенциальные друзья согласно IFriendshipService. Группы имеют префикс user:.

## Ограничения

Result от записи проверяется хабом; сервисная ошибка возвращается в HubResult без успешного события. Ошибка последующей рассылки логируется и не отменяет сохранение.

SetAvatar проверяет существование, а сервис — загрузчика и OwnerType.Avatar. Чужой файл отклоняется. REST UploadAvatar не отправляет AvatarUpdated. Политика приватности не применяется к этой рассылке. После reconnect перечитайте профиль; события не гарантированно сохраняются.


Источник: [Govor.API/Hubs/ProfileHub.cs](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Hubs/ProfileHub.cs).

