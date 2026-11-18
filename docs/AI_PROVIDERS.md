# Облачные провайдеры

Адаптеры отправляют текстовый запрос с ограниченным извлечённым текстом и безопасными техническими полями. Для PNG/JPEG/WebP можно отдельно разрешить визуальный анализ: тогда отправляется изображение размером до 12 МБ. Такое разрешение по умолчанию выключено и действует только вместе с allowlist форматов и выбранным облачным режимом. Ответ ограничивается схемой и затем дополнительно проверяется приложением: разрешённая категория, числовая уверенность, известные поля и их длина.

Для вкладки **ИИ-поиск** используются отдельные структурированные ответы `matches[{candidateId, score, reason}]`. Провайдер получает запрос и до 16 кандидатов за вызов; следующие разрешённые файлы проверяются следующими пакетами, пока не найдено совпадение или не исчерпана месячная квота. Абсолютные пути не входят в payload. Приложение отклоняет ID, которого нет среди переданных кандидатов, повторные ID и оценки вне диапазона 0–1. Облачный поиск следует allowlist, фильтру секретов и политике приватных папок. Визуальный поиск добавляет до двух изображений PNG/JPEG/WebP за запрос при отдельном разрешении.

## Текущие значения по умолчанию

| Провайдер | Модель по умолчанию | Анализ файла | ИИ-поиск |
|---|---|---|---|
| OpenAI | `gpt-5.6-terra` | `POST /v1/responses`, `text.format` с `json_schema` | `POST /v1/responses`, тот же строгий формат JSON Schema |
| Gemini | `gemini-3.6-flash` | `POST /v1beta/models/{model}:generateContent`, `generationConfig.responseFormat.text` | `POST /v1beta/interactions`, `response_format` с MIME `application/json` и схемой; `store: false` |
| Claude | `claude-sonnet-5-5` | `POST /v1/messages`, `output_config.format` с `json_schema` | `POST /v1/messages`, тот же строгий формат JSON Schema |

Имена моделей можно изменить на вкладке **Настройки**. Старые значения по умолчанию из предыдущей версии мигрируются при запуске; пользовательские значения сохраняются. Список моделей и формат API меняются независимо от приложения, поэтому перед использованием проверьте актуальную документацию провайдера.

## Визуальный вход

OpenAI получает `input_image` с base64 data URL. Классификатор Gemini использует `inlineData`, а поиск Gemini — элемент Interactions API типа `image` с полями `data` и `mime_type`. Claude получает base64-блок `image`. Справочник приложения при отправке ограничивает визуальный вход PNG, JPEG и WebP; все три формата поддержаны опубликованными интерфейсами провайдеров. Анимированные GIF, BMP, TIFF и HEIF/AVIF остаются на локальном OCR/метаданных и не отправляются как визуальное содержимое. Категория выбирается из справочника: фото, скриншот, скан, схема, иллюстрация или прочее. Тема изображения записывается в сводку и теги анализа, но не превращается в произвольное имя каталога.

## Официальные спецификации

- [OpenAI GPT-5.6 Terra](https://developers.openai.com/api/docs/models/gpt-5.6-terra) и [Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs).
- [Gemini 3.6 Flash](https://ai.google.dev/gemini-api/docs/models/gemini-3.6-flash), [Interactions API](https://ai.google.dev/gemini-api/docs/interactions-overview), [structured outputs](https://ai.google.dev/gemini-api/docs/structured-output) и [image understanding](https://ai.google.dev/gemini-api/docs/image-understanding).
- [Claude Sonnet 5.5](https://platform.claude.com/docs/en/models/sonnet-5-5/overview) и [Structured Outputs](https://platform.claude.com/docs/en/build-with-claude/structured-outputs).
- [OpenAI image inputs](https://developers.openai.com/api/docs/guides/images-vision), [Gemini image understanding](https://ai.google.dev/gemini-api/docs/image-understanding) и [Claude vision](https://docs.anthropic.com/en/docs/build-with-claude/vision).

## Проверка

Автоматизированные проверки подставляют фиктивный HTTP-ответ и проверяют URL, заголовки, модель и поля JSON-схемы каждого адаптера. Контрактные тесты не подтверждают доступность аккаунта, тарифа или модели. Для завершения плана нужен один контролируемый live-запрос с ключом, установленным локально в окружении пользователя.

Каждый вызов провайдера учитывается в месячной квоте; `Compare` заранее резервирует две единицы. Квота считает запросы, не денежный расход. Вероятные секреты, недопущенные форматы и приватные подпапки блокируют отправку до вызова адаптера.
