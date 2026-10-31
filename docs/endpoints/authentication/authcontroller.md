# Регистрация и вход

`Content-Type: application/json`. Регистрация и методы входа анонимные, но ограничены политикой `password-auth`. Смена пароля, миграция и восстановление описаны в [отдельном руководстве](../../password-recovery.md).

## POST /api/auth/register

Для нового клиента передайте `name`, `inviteLink`, `deviceInfo` и **все три** поля `srpSalt`, `srpIdentity`, `srpVerifier`, без `password`. Параметры вычисляются клиентом по `PasswordProtocol`: SRP-6a, группа 4096 бит, SHA-512; сериализация должна точно соответствовать реализации. Не подставляйте произвольные Base64-строки вместо verifier.

Совместимый старый формат принимает `password` (минимум 8 символов) без SRP-полей. Смешанный или неполный SRP-запрос отклоняется. Новая интеграция должна реализовывать доказательство пароля, а не строиться на старом формате.

Имя: 4–44 символа, кириллица и цифры, начинается с буквы. Модерация проверяет зарезервированные имена, запрещённые совпадения/подстроки и пять одинаковых символов подряд. `deviceInfo` описывает устройство, в БД до 256 символов.

`inviteLink` — код приглашения, не URL. Генератор использует GUID в формате `N` (32 символа без дефисов). При регистрации проверяются активность, срок и лимит приглашения. Резервирование места, аккаунт, административная роль и членство в обязательном канале сохраняются транзакционно.

Успех `200`:

```json
{"refreshToken":"<refresh>","accessToken":"<access>"}
```

Открытие сессии выполняется после регистрации. Если оно не удалось, аккаунт уже мог быть создан: сначала попробуйте вход, не повторяйте регистрацию бесконечно. Конфликт имени — `409`, отсутствующее приглашение — `404`, ошибка валидации/недоступное приглашение — `400`. Ошибки сервиса используют [ProblemDetails](../../code-docs/hubresult-less-than-t-greater-than.md).

## Контракт SRP-обмена

| Шаг | JSON-поля запроса | Поля успешного ответа |
| --- | --- | --- |
| `password/challenge` | name, publicEphemeral, deviceInfo | challengeId, salt, identity, publicEphemeral, credentialVersion, legacy |
| `password/login` | challengeId, proof | refreshToken, accessToken, serverProof, credentialVersion |

publicEphemeral/proof создаёт SRP-библиотека с параметрами PasswordProtocol. salt/identity/verifier регистрации создаются PasswordProtocol.Create на стороне клиента; salt и identity — случайные значения в hex. `legacy=true` требует явного сценария перехода старого аккаунта, не автоматической отправки открытого пароля.

Пример формы новой регистрации (значения SRP нужно вычислить):

```json
{
  "name": "Артём",
  "srpSalt": "<salt из PasswordProtocol.Create>",
  "srpIdentity": "<identity из PasswordProtocol.Create>",
  "srpVerifier": "<verifier из PasswordProtocol.Create>",
  "inviteLink": "<код приглашения>",
  "deviceInfo": "Android test device"
}
```

## Вход SRP

1. `POST /api/auth/password/challenge` начинает обмен и возвращает серверные параметры/challenge.
2. Клиент вычисляет доказательство и отправляет `POST /api/auth/password/login`.
3. Ответ содержит токены и серверное доказательство. Клиент проверяет доказательство до принятия результата.

Challenge действует две минуты и расходуется один раз. Неверное/истёкшее доказательство — `401`, некорректные протокольные данные — `400`, конфликт состояния — `409`, превышение лимита — `429`. Для повторной попытки начните новый обмен.

Точные DTO, правила кодирования и криптографические операции заданы в [PasswordProtocol.cs](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.Application/Authentication/PasswordProtocol.cs) и [PasswordAccountService.cs](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.Application/Authentication/PasswordAccountService.cs). Их следует переносить согласованно с клиентом, не реализовывать обмен по одному схематическому JSON-примеру.

## POST /api/auth/login — совместимость

```json
{"name":"Артём","password":"example-password","deviceInfo":"Android test device"}
```

Для bcrypt-аккаунта успех — та же пара токенов. Неизвестное имя — `404`, неверный пароль — `400`, SRP-аккаунт — `401 Auth.PasswordProofRequired`. Различающиеся ответы старого входа позволяют определить существование имени; не переносите это поведение в пользовательские тексты ошибок.
