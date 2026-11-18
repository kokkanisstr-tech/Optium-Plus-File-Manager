# REST API и MCP

## Адрес и авторизация

Оконное приложение автоматически запускает REST/MCP HTTP на `http://127.0.0.1:37582`; адрес слушает только loopback. Используйте заголовок:

```http
Authorization: Bearer <token>
```

Read-токен разрешает чтение. Write-токен разрешает чтение и изменение. Значения создаются заново при запуске и показываются на вкладке **API и MCP**. Для фиксированных токенов задайте перед запуском `FILE_SORTER_API_READ_TOKEN` и `FILE_SORTER_API_WRITE_TOKEN`; они должны содержать минимум 32 UTF-8 байта и различаться.

`GET /health` открыт без токена. Все обычные `GET` и `HEAD` под `/api` требуют read-токен, а остальные методы `/api` — write-токен. Исключения — короткоживущие upload/download-ссылки: после выпуска они служат одноразовым bearer-токеном до истечения срока. Их URL нельзя публиковать или пересылать.

## Маршруты REST

| Метод и путь | Доступ | Назначение |
|---|---|---|
| `GET /health` | без токена | статус loopback-сервера |
| `GET /api/stats` | read | счётчики состояния |
| `GET /api/categories` | read | допустимый справочник категорий |
| `GET /api/providers/health` | read | наличие ключа и модели; соединение не проверяется |
| `GET /api/files?status=&categoryId=&sourceType=&createdAfterUtc=&take=&skip=` | read | страница истории с фильтрами |
| `GET /api/files/{id}` | read | метаданные и статус записи |
| `GET /api/files/{id}/events` | read | журнал переходов |
| `GET /api/jobs/{id}` | read | краткий статус задания |
| `GET /api/files/{id}/content` | read | поток готового файла, после сверки SHA-256 |
| `POST /api/files` | write | загрузка одного файла: multipart или raw body с `X-File-Name` |
| `POST /api/files/text` | write | создание UTF-8 текста, максимум 1 МБ |
| `POST /api/files/{id}/analyze` | write | повторный анализ |
| `POST /api/files/{id}/approve` | write | подтверждение категории в очереди Review |
| `POST /api/files/{id}/revert` | write | отмена поддерживаемого перемещения |
| `POST /api/upload-tickets` | write | выпуск upload-ссылки на 15 минут и одну загрузку |
| `PUT /api/uploads/{ticket}` | ticket | передача одного raw-файла; заголовок `X-File-Name` обязателен |
| `GET /api/downloads/{ticket}` | ticket | одноразовая загрузка готового файла, ссылка на 5 минут |

Пример запроса на чтение:

```powershell
$headers = @{ Authorization = "Bearer <read-токен>" }
Invoke-RestMethod -Uri 'http://127.0.0.1:37582/api/files?take=20&skip=0' -Headers $headers
```

Загрузка бинарного файла поддерживает `multipart/form-data` или raw body. Для raw body используйте `X-File-Name`. Имена файлов санитизируются, размер ограничивается текущей настройкой приложения. Ответы API не включают локальные абсолютные пути.

Пример тела подтверждения:

```json
{
  "categoryId": "documents",
  "subcategoryId": "study",
  "rememberFileNameRule": false
}
```

`rememberFileNameRule=true` сохраняет локальное правило для точного имени файла. Путь назначения от API-клиента не принимается.

## MCP

Сервер публикует инструменты:

| Инструмент | Scope | Действие |
|---|---|---|
| `list_files` | read | список с фильтром по статусу, категории и источнику |
| `get_file` | read | метаданные файла по UUID |
| `suggest_category` | read | сохранённая классификация и разрешённые категории |
| `get_download_link` | read | одноразовая ссылка на разрешённый файл |
| `submit_local_file` | write | копирование из разрешённой входящей папки |
| `create_upload_ticket` | write | выпуск одноразовой ссылки на загрузку |
| `put_text_file` | write | добавление UTF-8 текста до 1 МБ |
| `request_analysis` | write | повторный глубокий анализ |
| `approve_classification` | write/destructive | подтверждение категории; может сохранить точное правило имени |
| `move_file` | write/destructive | перемещение только в фиксированную категорию |

Ни один инструмент не принимает произвольную папку назначения. `submit_local_file` ограничен настроенными наблюдаемыми корнями; upload/download инструменты возвращают краткоживущие ссылки. В stdio-режиме инструмент работает в локальном процессе, который пользователь сам запускает. По HTTP MCP доступен по `/mcp` и требует bearer-токен; операции записи дополнительно требуют write-токен.

Транспорт HTTP — MCP Streamable HTTP со stateless-сессиями. SSE endpoint не предоставляется.

## Подключение через stdio

В конфигурацию клиента, например Antigravity, добавьте сервер. Замените путь на фактическое расположение опубликованного exe:

```json
{
  "mcpServers": {
    "optium-plus-file-manager": {
      "command": "C:\\Apps\\OptiumPlusFileManager\\OptiumPlusFileManager.exe",
      "args": ["--mcp-stdio"]
    }
  }
}
```

Для stdio отдельный процесс запускает runtime, временный loopback API и MCP stdio transport. HTTP-токены не нужны: доступ ограничивается инструментами и разрешёнными входящими папками. Не запускайте MCP-конфигурацию из недоверенного клиента.

## Подключение через HTTP

Укажите MCP URL `http://127.0.0.1:37582/mcp` и read/write bearer-токен в соответствии с клиентом. Приложение должно оставаться запущенным. Для доступа через локальный HTTPS-туннель задайте `FILE_SORTER_PUBLIC_BASE_URL=https://ваш-домен` до старта приложения и настройте туннель так, чтобы он проксировал адрес на loopback. Приложение само наружу не слушает и отвергает публичный HTTP base URL.

## Приватность и эксплуатация

- Не храните ключи API в конфигурации MCP, если клиент записывает её в синхронизируемый профиль.
- Не включайте write-токен в read-only интеграции.
- Не публикуйте одноразовые ссылки: наличие URL достаточно для одного действия.
- Ограничьте внешнюю сторону туннеля своей авторизацией; HTTPS сам по себе не заменяет bearer-токен.
- `/api/providers/health` показывает только настроенный ключ и модель. Реальный сетевой запрос выполняется только при анализе файла.
