# ОСББ — заявки мешканців

Telegram-бот для звернень мешканців і майбутня вебпанель голови ОСББ. Структуру взято з `~/projects/olx-watcher`: C#, AWS Lambda, Serverless Framework і локальний плагін ZIP-пакування. Runtime — .NET 10, Linux ARM64.

## Що працює

У приватному чаті бот підтримує:

| Команда | Результат |
| --- | --- |
| `/start` | Привітання, Telegram ID для надання доступу, довідка |
| `/help` | Список команд |
| `/categories` | Категорії заявок |
| `/new категорія \| місце \| опис` | Створення приватної заявки зі статусом «Нова» |
| `/my` | Власні заявки, по 10 на сторінку |
| `/my номер` | Наступна сторінка за курсором із попереднього списку |
| `/request номер` | Деталі власної заявки |

Команди нечутливі до регістру та підтримують `/command@BotUsername`. Створення і перегляд заявок потребують активного доступу до будинку. Приклад:

```text
/new сантехніка | під’їзд 2, поверх 3 | Протікає труба біля ліфта
```

Категорії: сантехніка, електрика, прибирання, місця загального користування, інше. Місце — 1–200 символів, опис — 10–2000. Текст відповіді надсилається без HTML/Markdown. Час подання показується за Києвом.

Фото, діалог покрокового створення, коментарі, публічні заявки, підписки, зміни статусів та вебпанель ще не реалізовані. API вебпанелі має лише `GET /health`. Цільові вимоги: [docs/requirements.md](docs/requirements.md).

## Структура та обробка

```text
POST /telegram/webhook
  → перевірка secret із SSM
  → FIFO-черга команд
  → CommandFunction
  → DynamoDB: заявка + історія + збережена відповідь
  → Telegram sendMessage
```

`OsbbRequests.Bot.Function` перевіряє secret до розбору payload, приймає лише повідомлення від людини у приватному чаті та підтверджує update після запису в SQS. Непідтримувані/некоректні update підтверджуються без обробки; недоступність SSM або черги дає `503`, щоб Telegram повторив доставку.

`OsbbRequests.Bot.CommandFunction` обробляє команди із SQS; транзакція DynamoDB захищає від повторного створення заявки. Збережена відповідь повторно використовується після збою доставки. Після успішної доставки встановлюється ознака `delivered`. FIFO забезпечує послідовну обробку повідомлень одного чату.

Спільні типи — у `OsbbRequests.Shared`; API вебпанелі — у `OsbbRequests.AdminApi`. Деталі таблиці та гарантій: [docs/architecture.md](docs/architecture.md).

## Локальний запуск перевірок

Потрібні .NET SDK 10.0.3xx, Node.js 24, npm, Bash і `zip`.

```bash
npm ci
dotnet restore OsbbRequests.sln --locked-mode
npm run build
npm run test -- --no-build --no-restore
npm run check
npm run package:dotnet
```

Тести не звертаються до AWS або Telegram. Остання команда створює ZIP для Bot та AdminApi у `artifacts/` без AWS credentials; глобальний Amazon.Lambda.Tools не потрібний. Після оновлення NuGet-залежностей виконайте `dotnet restore OsbbRequests.sln --force-evaluate` і додайте `packages.lock.json` до Git.

GitHub Actions запускає restore, build, тести, перевірку форматування та ZIP-пакування. Деплой у CI не налаштований.

## Конфігурація та секрети

Одне середовище — один бот та один будинок. Перед розгортанням задайте:

```bash
export TELEGRAM_BOT_USERNAME=YourOsbbBot  # без @
export BUILDING_ID=osbb-example
# Необов'язково: приватний чат або група для операційних помилок.
# У приватному чаті адміністратор спочатку має запустити бота.
export ADMIN_TELEGRAM_CHAT_ID=123456789
```

Без перевизначення використовується username `OsbbRequestsBot` і building ID `osbb-requests-<stage>`. Команди з адресацією `@username` потребують правильного `TELEGRAM_BOT_USERNAME`. Для імен без `@` значення username не впливає на маршрутизацію.

У AWS Systems Manager Parameter Store створіть два `SecureString` у тому самому регіоні:

- `/osbb-requests/dev/telegram-bot-token` — токен від BotFather;
- `/osbb-requests/dev/telegram-webhook-secret` — випадковий секрет із символів `A–Z`, `a–z`, `0–9`, `_`, `-`, довжиною 1–256.

Для іншого stage замініть `dev`. Конфігурація розрахована на стандартний ключ `aws/ssm`; для власного KMS-ключа додайте точні `kms:Decrypt` permissions у відповідну роль та key policy. Lambda отримують тільки імена параметрів і читають секрети під час роботи з кешем до п'яти хвилин. Секрети не підставляються в Serverless-артефакти. Значення вводьте через захищений інтерфейс AWS або свій менеджер секретів; не додавайте їх у Git, журнали чи shell history.

## AWS-пакування й розгортання

Потрібні AWS credentials та автентифікація Serverless Framework v4.

```bash
npm run package -- --stage dev --region eu-central-1
npm run deploy -- --stage dev --region eu-central-1
```

`package` генерує CloudFormation у `.serverless/` без розгортання; потребує AWS credentials. `deploy` створює три Lambda, HTTP API, FIFO-чергу та DLQ, DynamoDB із point-in-time recovery, окремі IAM-ролі та CloudWatch logs. Регіон за замовчуванням — `eu-central-1`, stage — `dev`.

Після розгортання зареєструйте в Telegram `setWebhook` URL `<HTTP API URL>/telegram/webhook`, вкажіть той самий `secret_token` і `allowed_updates: ["message"]`. Токен бота та webhook secret передавайте з менеджера секретів без журналювання. Перевірка реального обміну з Telegram виконується після налаштування секретів і деплою.

## Надання доступу мешканцю

Поки вебпанелі немає, уповноважений оператор додає запис у DynamoDB через AWS Console або CLI. Мешканець надсилає голові свій Telegram ID з відповіді `/start`. Приклад для ID `123456789`, будинку `osbb-example` та `dev`:

```bash
aws dynamodb put-item \
  --region eu-central-1 \
  --table-name osbb-requests-dev-command-data \
  --item '{"pk":{"S":"USER#123456789"},"sk":{"S":"MEMBERSHIP"},"buildingId":{"S":"osbb-example"},"active":{"BOOL":true}}'
```

`buildingId` повинен збігатися з `BUILDING_ID` Lambda. Для відкликання доступу встановіть `active: false` у цьому записі. Бот не має операцій надання доступу; вони потребують окремого адміністративного AWS-доступу.

## Відмови та експлуатація

Повторна обробка одного update не створює другу заявку. При неоднозначному мережевому результаті `sendMessage` або збої між доставкою та записом `delivered` повторне Telegram-повідомлення можливе: Telegram не має idempotency key для цього виклику.

Черга утримує команди до чотирьох днів; після п'яти невдалих обробок повідомлення переходить у DLQ зі зберіганням до 14 днів. Звичайний retry — після 180 секунд; для Telegram `429` враховується `retry_after` через visibility timeout (до ліміту SQS 12 годин). Після усунення причини помилки оператор має виконати redrive DLQ до основної черги. Не запускайте довільні нові команди зі старих payload: відновлюйте оригінальні повідомлення в межах 30-денного строку дедуплікації.

Записи відповідей та дедуплікації мають TTL 30 днів і можуть містити приватний текст заявки. TTL видаляється DynamoDB асинхронно. Заявки, історія та членство TTL не мають. Черга й DLQ також містять текст команди — доступ до них лише для операторів і відповідних Lambda.

Помилки журналюються за типом та SQS Message ID без тексту команд, контактів і секретів. Якщо заданий `ADMIN_TELEGRAM_CHAT_ID`, воркер намагається сповістити адміністратора; збій цього повідомлення не перешкоджає retry. Стежте за DLQ та CloudWatch; окремі зовнішні alarm destinations ще не налаштовані.

Видалення середовища:

```bash
npm run remove -- --stage dev --region eu-central-1
```

DynamoDB має `DeletionPolicy: Retain`: дані й витрати на таблицю зберігаються після видалення stack. Повторне створення stack з тим самим іменем таблиці потребуватиме імпорту збереженої таблиці або окремого рішення оператора. Секрети SSM також не видаляються автоматично.

## Документація платформ

- [Telegram Bot API](https://core.telegram.org/bots/api)
- [AWS Lambda та SQS: помилки й FIFO](https://docs.aws.amazon.com/lambda/latest/dg/services-sqs-errorhandling.html)
- [SSM SecureString і KMS](https://docs.aws.amazon.com/systems-manager/latest/userguide/secure-string-parameter-kms-encryption.html)
