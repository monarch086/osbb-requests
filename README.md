# ОСББ — заявки мешканців

Telegram-бот для звернень мешканців і вебпанель голови ОСББ та уповноважених адміністраторів. За основу структури взято `~/projects/olx-watcher`: C#, AWS Lambda, Serverless Framework і локальний плагін пакування.

## Поточний стан

Створено початковий каркас на .NET 10: solution із трьома проєктами, конфігурацію Serverless, пакування ZIP та CI. API має тільки `GET /health`, що повертає `{"status":"ok"}`. Це перевірка запуску Lambda, а не доступності майбутніх сховищ.

Обробка Telegram, збереження заявок, авторизація, сповіщення та інтерфейс вебпанелі ще не реалізовані. Webhook Lambda повертає `503`; HTTP-маршрут для неї навмисно не підключений. Реєструвати webhook у Telegram на цьому етапі не потрібно.

## Структура

```text
src/
  OsbbRequests.Bot/        # Майбутня точка входу Telegram webhook
  OsbbRequests.AdminApi/   # API вебпанелі; наразі тільки health
  OsbbRequests.Shared/    # Спільні типи; статуси та видимість заявок
scripts/package-dotnet.sh # Публікація linux-arm64 і ZIP для кожної Lambda
serverless.yml            # AWS Lambda та HTTP API
serverless-dotnet-package.js
.github/workflows/ci.yml  # Збірка, форматування, пакування без деплою
```

Вимоги та наступні етапи описані в [docs/requirements.md](docs/requirements.md), технічні рішення — у [docs/architecture.md](docs/architecture.md).

## Локальна підготовка

Потрібні .NET SDK 10.0.3xx (версію вибирає `global.json`), Node.js 24, npm, Bash і `zip`. Для AWS-пакування та деплою додатково потрібні AWS credentials і налаштована автентифікація Serverless Framework v4. Організація та облікові дані з проєкту-прикладу не копіюються.

```bash
npm ci
dotnet restore OsbbRequests.sln --locked-mode
npm run build
npm run check
npm run package:dotnet
```

Остання команда не потребує AWS: створює `artifacts/OsbbRequests.Bot/bot.zip` і `artifacts/OsbbRequests.AdminApi/admin-api.zip`. Використовує `dotnet publish` і `zip`; глобальний Amazon.Lambda.Tools не потрібний. Після свідомого оновлення NuGet-залежностей виконайте `dotnet restore OsbbRequests.sln --force-evaluate` та додайте зміни `packages.lock.json` до Git.

## Пакування та розгортання AWS

```bash
npm run package -- --stage dev --region eu-central-1
npm run deploy -- --stage dev --region eu-central-1
```

Serverless автоматично викликає скрипт .NET-пакування. `package` генерує CloudFormation у `.serverless/`; команда потребує автентифікації Serverless та AWS credentials, навіть без розгортання. `deploy` створює платні AWS-ресурси: дві Lambda, HTTP API, IAM та журнали CloudWatch. За замовчуванням: `dev`, `eu-central-1`, ARM64, 256 MB, timeout 10 секунд, зберігання журналів 14 днів.

Після деплою перевірте `GET <HTTP API URL>/health`. Публічних маршрутів для читання або зміни заявок ще немає. DynamoDB, S3 та автентифікацію додамо разом із відповідними сценаріями.

Видалення конкретного середовища:

```bash
npm run remove -- --stage dev --region eu-central-1
```

## Секрети

Каркас не використовує токен бота та не потребує `.env`. Перед підключенням Telegram потрібно налаштувати AWS Secrets Manager або SSM SecureString для токена бота й webhook secret. Значення не повинні потрапляти в Git, артефакти збірки чи журнали. Локальні `.env*`, `.aws/`, `.serverless/`, результати збірки та директорії агентів виключені через `.gitignore`.

## Джерела

- [C# у AWS Lambda: підтримувані runtime](https://docs.aws.amazon.com/lambda/latest/dg/lambda-csharp.html)
- [Serverless: пакування власних ZIP-артефактів](https://www.serverless.com/framework/docs/providers/aws/guide/packaging)
