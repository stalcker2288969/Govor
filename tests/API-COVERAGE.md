# Покрытие Govor.API

Итоговая проверка 24.10.2026: Release-сборка и 710 тестовых случаев проходят
на SQLite и PostgreSQL 17.4 без ошибок и пропусков. На этапе расширения API
добавлены 172 случая: API.Tests вырос с 103 до 271, Server.Tests — с 132 до 136.
Application.Tests содержит 295, Domain.Tests — 8 случаев.

## Измерение

| Область | Строки | Ветви |
| --- | ---: | ---: |
| Исходники API до расширения | 56,14% (1093/1947) | 55,86% (248/444) |
| Исходники API после расширения, SQLite | 98,10% (1910/1947) | 92,34% (410/444) |
| Полная сборка API после расширения, включая генератор | 88,47% (1910/2159) | 63,08% (410/650) |

Исходники API включают Program.cs, конфигурацию DI, контроллеры, хабы, фильтр,
mapping и обработчики уведомлений. Все исполняемые строки контроллеров покрыты;
не все пути условий проверены.

Единственное исключение из порога исходников — файл
`obj/Release/net10.0/Microsoft.AspNetCore.OpenApi.SourceGenerators/Microsoft.AspNetCore.OpenApi.SourceGenerators.XmlCommentGenerator/OpenApiXmlCommentSupport.generated.cs`.
Он генерируется SDK, содержит 212 строк и 206 путей ветвления и не вызывается
текущим Swagger pipeline. Swagger проверяется отдельным HTTP-тестом.
Исключение явно запрашивается параметром `-ExcludeGeneratedSources`; без него
скрипт измеряет полную сборку. JSON в обоих случаях сохраняет EntireAssembly,
а при исключении — также ExcludedFiles. Рабочий код ради процентов не исключался.

## Добавленные проверки

- Административные пользователи и приглашения: DTO, поля, пароль, неизвестный
  пользователь, фильтрация активных приглашений, ключ администратора, ошибки.
- Ключи сессий: текущий sid, лимит 100 ключей, невалидная модель, проверка дружбы,
  Base64, отсутствие SignedPreKey, исключение использованных ключей.
- Личные чаты: проверка дружбы перед созданием, обе ориентации участников,
  запреты и ошибки сервисов.
- Наборы реакций: пользовательские и административные операции, пагинация,
  подписки, видимость выключенных наборов, emoji и медиа, лимиты и cancellation.
- Группы: списки, участники, приглашения, отмена приглашения, обязательный канал,
  удаление аватара и сообщения; уведомления отправляются после успеха.
- ChatsHub: жизненный цикл, Send/Edit/Remove/Read/ReadChat, реакции, параметры
  актёра и времени, вложения, ответы, ошибки и отсутствие лишних уведомлений.
- ProfileHub и PresenceHub: подписки групп, онлайн на первом соединении,
  офлайн на последнем, нормализация описания, аватар, адресаты и сбои рассылки.
- HubExceptionFilter: активные и отсутствующие сессии, типизированные и обычные
  возвращаемые значения, соответствие исключений статусам.
- Уведомления и push: получатели, уже прочитанные сообщения, отсутствующий профиль,
  постоянные и временные ошибки поиска чата, отмена с сохранением lease для повтора.
- DI и worker: PostgreSQL provider и retry, detailed errors, отключённый Firebase,
  LocalStorage, освобождение scope и остановка при отмене.
- HTTP: административные роли, реальные mappings реакций, регистрация E2EE,
  неизменяемость ключей, каталог участников, ping, readiness и Swagger contract.

Проверки SQL-поведения находятся в Integration, тесты с mocks — в Unit.
HTTP-проверки приложения находятся в Govor.Server.Tests/Api. Удалены 32 пустых
каталога старой структуры; это были остатки переноса, а не незавершённые тесты.

## Запуск и CI

```powershell
dotnet build Govor.Server.slnx -c Release --no-restore
dotnet test Govor.Server.slnx -c Release --no-build --no-restore --logger trx --results-directory artifacts/coverage --collect 'XPlat Code Coverage;Format=json,cobertura'
./tests/Measure-ApplicationCoverage.ps1 -ResultsDirectory artifacts/coverage
./tests/Measure-ApplicationCoverage.ps1 -ResultsDirectory artifacts/coverage -AssemblyName Govor.API -ExcludeGeneratedSources -MinimumLineCoverage 98 -MinimumBranchCoverage 92
```

Для PostgreSQL задайте TEST_POSTGRES_CONNECTION согласно [README.md](README.md).
CI проверяет оба порога на SQLite и PostgreSQL. Объединяются попадания всех четырёх
проектов, проценты отдельных проектов не складываются. Каждый новый запуск должен
использовать свежую папку отчётов. Проверен и отрицательный сценарий: требование
100% корректно отклоняется скриптом.

Итоговые локальные TRX и JSON: `artifacts/test-results/api-final-sqlite` и
`artifacts/test-results/api-final-postgres`. Скрипт создаёт api-coverage.json и
application-coverage.json. Артефакты игнорируются Git; CI сохраняет свои отчёты.

## Границы проверки

Остаются 37 строк и 34 пути ветвления исходников API. Большинство строк находятся
в Program.cs: production startup guards, создание реального Firebase с credentials,
режим migrate-only и отдельные ошибки readiness. Остаются также защитный выход
ProfileHub при пустом наборе адресатов (текущий пользователь добавляется всегда),
часть обработки отмены worker и несколько комбинаций условий. Точные строки есть
в JSON-отчёте. Это высокое, но не стопроцентное покрытие.

Compiler/nullable и NuGet warnings остаются. Реальные Firebase credentials,
доставка push на устройство, нагрузка и production-развёртывание не проверялись.
