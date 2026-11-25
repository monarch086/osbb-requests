# План реалізації OsbbRequests.WebApi

`OsbbRequests.WebApi` — ASP.NET Core API в Docker із тими самими HTTP-контрактами та Cognito-пулом `dev`. Перший запуск варто зробити зі спільною логікою поточного бекенду. Перехід на PostgreSQL тоді можна перевіряти окремо від заміни Lambda на локальний вебсервер.

## Етапи реалізації

1. **Винести спільну логіку Admin API — виконано.** `OsbbRequests.Domain` містить спільні моделі, правила та DTO. `OsbbRequests.Services` містить типізовані операції, перевірки доступу, бізнес-валідацію, генерацію Excel та інтерфейси сховища, ідентичності й статистики. Маршрути `/admin/*` та HTTP-контракт Lambda реалізовані в `OsbbRequests.AdminApi`. На наступному етапі `OsbbRequests.WebApi` визначить власні маршрути ASP.NET Core для тих самих операцій `AdminService`. [Спільний сервіс](../src/OsbbRequests.Services/AdminService.cs) · [Адаптер Lambda](../src/OsbbRequests.AdminApi/ApiHandler.cs)

2. **Додати локальний Docker-запуск і Cognito.** Додати сервіс `web-api` до `compose.data.yml` із доступом через `localhost`. Використати чинні `UserPoolId` та `UserPoolClientId` із CloudFormation `dev`. `JwtBearer` має перевіряти підпис через JWKS, issuer, строк дії, `client_id`, `token_use=access` і поточний scope `aws.cognito.signin.user.admin`. Зберегти поведінку `/admin/me`, включно з перевіркою підтвердженого email для нового адміністратора. Cognito підтверджує особу, а доступ до операцій визначають серверні дозволи ОСББ. [Перевірка токенів Cognito](https://docs.aws.amazon.com/cognito/latest/developerguide/amazon-cognito-user-pools-using-tokens-verifying-a-jwt.html) · [ASP.NET Core JWT Bearer](https://learn.microsoft.com/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0)

3. **Перевірити сумісність із чинним API на тому самому сховищі.** Підключити наявний `DynamoAdminStore` в окремому режимі з явним вибором `dev`. Зіставити відповіді обох серверів для заявок, мешканців, додаткових квартир, ключів, розсилок, адміністраторів і статистики. Перевірити також фільтри, пагінацію, Excel, доступи та коди помилок. Сценарії запису прогнати на тестових даних, бо запис через локальний API в цьому режимі змінює дані чинного бота. [Інтерфейс сховища](../src/OsbbRequests.Services/IAdminStore.cs)

4. **Реалізувати `PostgresAdminStore`.** Спочатку перенести читання, фільтри, статистику списків і експорт. Потім перенести операції запису разом з аудитом, перевіркою версій та атомарним створенням подій для доставки. Для перевірки версії заявки використовувати `requests.Version`, яку імпорт заповнює з DynamoDB. Історію редагування та видалення ключів читати з `resident_events`. `IsSuperAdmin` доступний у вихідному записі імпорту. Для привілейованих операцій джерелом `IsSuperAdmin` має залишатися актуальне значення в AWS, доступне лише для читання з локального API. [Версія заявки й роль адміністратора](../src/OsbbRequests.AdminApi/Data/DynamoAdminStore.cs) · [Модель PostgreSQL](../src/OsbbRequests.Data/Storage/Models/Request.cs)

5. **Узгодити джерело запису та доставку Telegram.** Щотижневий імпорт переносить дані з DynamoDB у PostgreSQL. Якщо локальний API прийматиме записи в PostgreSQL, а бот і далі читатиме DynamoDB, мешканець не побачить локальну зміну статусу, а чинний воркер не доставить подію з PostgreSQL. Для паралельного запуску зі справжнім ботом джерелом запису варто залишити DynamoDB. Для повноцінного переходу на PostgreSQL потрібен окремий етап із переведенням бота й воркера або з узгодженим перенесенням подій між сховищами. `RRU/WRU` на сторінці статистики залишаються метриками DynamoDB. [Локальна PostgreSQL та імпорт](architecture.md#експериментальна-локальна-postgresql)

6. **Підключити локальний Angular-фронтенд.** У локальній конфігурації змінити `apiUrl` на `http://localhost:<порт>`, використовуючи ті самі Cognito ID. Поточний фронтенд уже дозволяє HTTP на `localhost`. Перевірити в браузері вхід чинним адміністратором, доступ за ролями, оновлення заявки, обробку конфлікту версій і завантаження Excel. [Перевірка URL у фронтенді](../src/Frontend/src/app/auth.service.ts)

## Результат

**Перший результат** — локальний Docker API, до якого можна підключити чинний фронтенд і увійти наявним обліковим записом `dev`.

**Повна заміна бекенду на PostgreSQL** готова після перевірки записів, аудиту та доставки сповіщень у наскрізному сценарії з ботом.
