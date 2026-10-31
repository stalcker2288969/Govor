# Профиль и аватар

Требуется роль User/Admin.

| Метод | Маршрут | Успех |
| --- | --- | --- |
| GET | /api/profile/download/me | 200, UserProfileDto текущего пользователя |
| GET | /api/profile/download/{id} | 200, UserProfileDto указанного пользователя |
| POST | /api/profile/avatar | 200, MediaUploadResult |

UserProfileDto: id, username, description (nullable), iconId (nullable), isOnline. Отсутствующий пользователь — 404 ProblemDetails.

Загрузка аватара: multipart/form-data с FromFile, Type, MimeType. В отличие от MediaController, отдельный лимит 20 MB не задан; действуют ограничения хоста. Контроллер сохраняет медиа с OwnerType.Avatar и устанавливает IconId.

Описание изменяется через ProfileHub.SetDescription. ProfileHub.SetAvatar принимает существующий iconId и рассылает AvatarUpdated.

## Ограничения

REST UploadAvatar не рассылает AvatarUpdated. Клиентам может потребоваться отдельное обновление данных. ProfileHub.SetAvatar и ProfileService проверяют, что файл загружен текущим пользователем с OwnerType.Avatar. Чужое вложение нельзя назначить аватаром.

Хаб проверяет Result от SetDescription и SetNewIcon: при сервисной ошибке возвращает ошибку HubResult и не рассылает успешное изменение.

Чтение чужого профиля не применяет PrivacyUserSettings. Наличие моделей приватности не означает действующую политику доступа.


Источник: [Govor.API/Controllers/ProfileController.cs](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Controllers/ProfileController.cs).

