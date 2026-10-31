# Администрирование сервера

Обычные административные маршруты требуют роль Admin и активную access-сессию. Исключение — InvitationWithKey, проверяющий отдельный runtime-секрет вместо JWT. Администратор сервера и админ группы — разные права: роль в группе не открывает маршруты `/api/admin`.

| Метод | Маршрут | Назначение |
| --- | --- | --- |
| POST | `/api/admin/InviteUser/Invitation` | Приглашение регистрации: endDate, maxParticipants, isAdmin, description |
| POST | `/api/admin/InviteUser/InvitationWithKey` | Без JWT, с секретом adminKey; первоначальное приглашение |
| GET | `/api/admin/InviteUser` | Список приглашений |
| GET | `/api/admin/InviteUser/GetAllActiveInvitations` | Активные приглашения |
| GET | `/api/admin/InviteUser/{id}` | Приглашение по ID |
| GET | `/api/admin/Users/all` | До 50 пользователей |
| GET | `/api/admin/Users/{id}` | Пользователь по ID |
| POST | `/api/admin/Users/user/{id}/password` | `{ "password": "..." }`; 204, отзыв сессий и push-токенов |
| GET / PUT | `/api/admin/required-channel` | Канал для всех новых пользователей, право выхода |

UserResponse не содержит passwordHash. Старый GET смены пароля с паролем в URL удалён. JWT роль Admin формируется по Invitation.IsAdmin пользователя. Настройки обязательного канала и изменения паков реакций дополнительно проверяют это право в БД.

Паки: [административный API реакций](reactions.md). Обязательный канал, владелец/админы, приглашения и модерация: [группы и каналы](../groups.md).

Создание административных приглашений выдаёт возможность зарегистрировать серверного администратора, поэтому этот API предназначен только для доверенных администраторов сервера. Административный FriendshipsController и пустой GroupsHub остаются неиспользуемыми заготовками.

## Первый администратор и InvitationWithKey

Оператор задаёт `InvitationAdmin__Key` в runtime и перезапускает API. Метод `POST /api/admin/InviteUser/InvitationWithKey` помечен AllowAnonymous: это альтернативная авторизация по секрету, а не открытая выдача ролей. Он принимает `adminKey`, `endDate`, `maxParticipants`, `isAdmin`, `description`. Без настроенного ключа возвращает 503, с неверным — 401, при успешной генерации — 200 со строкой кода. Для административного приглашения задайте isAdmin=true, небольшой срок и maxParticipants=1.

```powershell
# Секрет задайте отдельно; не сохраняйте его в репозитории.
$invitation = @{
    adminKey = $env:InvitationAdmin__Key
    endDate = [DateTime]::UtcNow.AddHours(1).ToString("o")
    maxParticipants = 1
    isAdmin = $true
    description = "Initial administrator"
} | ConvertTo-Json
$code = Invoke-RestMethod -Method Post -Uri "https://YOUR_API/api/admin/InviteUser/InvitationWithKey" -ContentType "application/json" -Body $invitation
```

Зарегистрируйтесь с inviteLink=$code через обычный `/api/auth/register` (SRP-параметры вычисляет клиент). Роль берётся из приглашения. После создания администратора уберите runtime-ключ и перезапустите API, если этот способ больше не нужен. Этот endpoint не использует password-auth rate limiter; ограничьте доступ к нему на инфраструктуре. Никогда не встраивайте adminKey в мобильное приложение.

## Сброс пароля пользователю

POST `/api/admin/Users/user/{id}/password` меняет доступ и отзывает сессии/FCM-токены. Сброс очищает SRP/парольную копию восстановления, но сохраняет основной зашифрованный архив. Администратор не получает ключи истории. Пользователь после входа задаёт новый пароль и восстанавливает архив резервным кодом либо с доверенного устройства. [Полный сценарий](../password-recovery.md).
