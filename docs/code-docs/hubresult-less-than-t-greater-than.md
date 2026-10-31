# Ответы, ошибки и сериализация

## HTTP

ResultExtensions преобразует Result<T, Error>: успешный T → 200 со значением; Unit → 204 без тела.

| ErrorType | HTTP |
| --- | --- |
| Validation | 400 |
| NotFound | 404 |
| Conflict | 409 |
| Unauthorized | 401 |
| Forbidden | 403 |
| ServerError | 500 |
| Прочие Failure | 400 |

Сервисная ошибка:

```json
{"status":404,"title":"Not Found","detail":"<описание>","errorCode":"<код>"}
```

errors добавляется при наличии. Автоматическая MVC-валидация, middleware авторизации и ручные BadRequest/StatusCode могут возвращать другую форму. Единого тела ошибки для всех маршрутов сейчас нет.

Ручной Forbid возвращает отказ без гарантированного JSON-тела. SRP/E2EE-контроллеры также возвращают строки и пустые ответы. Обрабатывайте статус и Content-Type до разбора тела.

## SignalR HubResult<T>

```json
{"status":200,"result":null,"errorMessage":null}
```

status — число внутри ответа команды, не HTTP-статус соединения. Значения: 200 Success, 201 Created, 204 NoContent, 400 BadRequest, 401 Unauthorized, 403 Forbidden, 404 NotFound, 409 Conflict, 422 UnprocessableEntity, 500 ServerError.

Static factories: Ok(result), Created(result), NoContent(), BadRequest(message, details), NotFound(message, details), Unauthorized(message), Conflict(message), UnprocessableEntity(message), Error(message).

Каждый хаб преобразует бизнес-ошибки; сверяйтесь с контрактом конкретной команды. HubExceptionFilter проверяет активную сессию перед вызовом и преобразует необработанные исключения в сериализуемый HubResult нужного типа. status 401 может завершить соединение.

## JSON-контракт

Стандартные ASP.NET настройки используют camelCase; enum передаются числами, Guid — строками, byte[] — base64, даты — ISO 8601. StringEnumConverter явно не настроен.

HTTP MessageResponse.id и SignalR UserMessageResponse.messageId отличаются. HTTP и SignalR вложения также имеют разные структуры. Клиент должен иметь явное преобразование; не подменяйте один DTO другим.


Источник: [Govor.API/Common/Extensions/ResultExtensions.cs](https://github.com/stalcker2288969/Govor/blob/a3401ceedf30c2eb6167f7ef6e4ae820883d2de2/Govor.API/Common/Extensions/ResultExtensions.cs).
