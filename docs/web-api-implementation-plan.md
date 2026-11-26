# План реалізації OsbbRequests.WebApi

`OsbbRequests.WebApi` — ASP.NET Core API в Docker із тими самими HTTP-контрактами та Cognito-пулом `dev`. Перший запуск варто зробити зі спільною логікою поточного бекенду. Перехід на PostgreSQL тоді можна перевіряти окремо від заміни Lambda на локальний вебсервер.

## Етапи реалізації

1. **Винести спільну логіку Admin API — виконано.** `OsbbRequests.Domain` містить спільні моделі, правила та DTO. `OsbbRequests.Services` містить типізовані операції, перевірки доступу, бізнес-валідацію, генерацію Excel та інтерфейси сховища, ідентичності й статистики. Маршрути `/admin/*` та HTTP-контракт Lambda реалізовані в `OsbbRequests.AdminApi`. На наступному етапі `OsbbRequests.WebApi` визначить власні маршрути ASP.NET Core й підключить спільні `ResidentService`, `RequestService`, `MessageService`, `PermissionService` і `StatisticsService`. [Сервіс заявок](../src/OsbbRequests.Services/RequestService.cs) · [Адаптер Lambda](../src/Serverless/OsbbRequests.AdminApi/ApiHandler.cs)

2. **Додати локальний Docker-запуск і Cognito — виконано.** `OsbbRequests.WebApi` запускається у Compose через `./scripts/osbb-web-api`. Він бере `UserPoolId` і `UserPoolClientId` із CloudFormation `dev`. `JwtBearer` перевіряє підпис через JWKS, issuer, строк дії, `client_id`, `token_use=access` і scope `aws.cognito.signin.user.admin`. `GET /admin/me` використовує ті самі правила першого входу, що й Lambda. [Локальний запуск](../README.md#локальний-web-api) · [Перевірка токенів Cognito](https://docs.aws.amazon.com/cognito/latest/developerguide/amazon-cognito-user-pools-using-tokens-verifying-a-jwt.html)

3. **Додати контролери з контрактом чинного API — виконано.** Web API містить адміністративні маршрути Lambda. Інтеграційні тести перевіряють авторизацію, фільтрацію, Excel та операції запису. [Контролери](../src/OsbbRequests.WebApi/Controllers)

4. **Реалізувати PostgreSQL-сховище — виконано.** `PostgresAdminStore` обслуговує читання й записи за спільними інтерфейсами. Локальний API працює з `osbb_dev`, яку один раз створює з імпортованої бази. `IsSuperAdmin` читається з імпортованого запису й залишається доступним лише для читання. [Сховище](../src/OsbbRequests.Postgres/PostgresAdminStore.cs)

5. **Узгодити джерело запису та доставку Telegram.** Локальні транзакції ставлять події `LOCAL_PENDING` у PostgreSQL. Чинний бот і воркер використовують DynamoDB. Перехід бота на PostgreSQL і доставка локальних подій потребують окремого етапу. Показники `RRU/WRU` у локальному API стосуються даних DynamoDB зі знімка. [Локальна PostgreSQL та імпорт](architecture.md#експериментальна-локальна-postgresql)

6. **Підключити локальний Angular-фронтенд.** У локальній конфігурації змінити `apiUrl` на `http://localhost:<порт>`, використовуючи ті самі Cognito ID. Поточний фронтенд уже дозволяє HTTP на `localhost`. Перевірити в браузері вхід чинним адміністратором, доступ за ролями, оновлення заявки, обробку конфлікту версій і завантаження Excel. [Перевірка URL у фронтенді](../src/Frontend/src/app/auth.service.ts)

## Результат

**Перший результат** — локальний Docker API, до якого можна підключити чинний фронтенд і увійти наявним обліковим записом `dev`.

**Повна заміна бекенду на PostgreSQL** готова після перевірки записів, аудиту та доставки сповіщень у наскрізному сценарії з ботом.
