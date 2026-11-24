# fiapx-video-api

API do ciclo de vida dos vídeos da plataforma **FIAP X** (.NET 10). É a dona do agregado Vídeo e do seu status:
cria o vídeo, gera as URLs pré-assinadas de upload e download no S3, publica `video.uploaded` e consome os eventos do
`video-processor` para atualizar o status que o usuário lista.

> Parte da plataforma FIAP X: arquitetura, ADRs e contratos ficam em [`FiapXHacka/fiapx`](https://github.com/FiapXHacka/fiapx).

[![Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=FiapXHacka_fiapx-video-api&metric=alert_status)](https://sonarcloud.io/summary/new_code?id=FiapXHacka_fiapx-video-api)
[![Coverage](https://sonarcloud.io/api/project_badges/measure?project=FiapXHacka_fiapx-video-api&metric=coverage)](https://sonarcloud.io/summary/new_code?id=FiapXHacka_fiapx-video-api)

## Platform overview

```
cliente ── JWT ──▶ API Gateway ──▶ video-api (este repo) ──▶ PostgreSQL video_db
                                     │  outbox ──▶ RabbitMQ fiapx.events ── video.uploaded ──▶ video-processor
                                     └◀── video-api.video-status ◀── processing-started | processed | processing-failed
```

## Estado atual (scaffold, issue #1)

| Pronto | Próximas issues |
|---|---|
| Clean Architecture (Domain, Application, Infrastructure, Api) | #2 máquina de estados do Vídeo |
| EF Core 10 + Npgsql, migration inicial = [`db/schema.sql`](db/schema.sql) | #3 `POST /videos` com URL pré-assinada |
| Outbox transacional + relay com publisher confirms | #4 confirmar upload e publicar `video.uploaded` |
| Consumer com ack manual, inbox e DLQ | #5 handlers dos eventos de processamento |
| Validação de JWT RS256 do identity-service | #6 listagem com cache Redis, #7 download |
| `/health/live`, `/health/ready`, OpenTelemetry (OTLP), logs JSON | #9 manifests Kubernetes e deploy |

## Estrutura

```
src/FiapX.VideoApi.Domain           agregado Video (por enquanto só a criação)
src/FiapX.VideoApi.Application      portas: IOutbox, IIntegrationEventConsumer<T>, contratos dos eventos
src/FiapX.VideoApi.Infrastructure   Persistence/ (EF Core, migrations)  Messaging/ (CloudEvents, outbox, RabbitMQ)
src/FiapX.VideoApi.Api              Program, autenticação, health checks, telemetria
tests/FiapX.VideoApi.UnitTests
tests/FiapX.VideoApi.IntegrationTests   Testcontainers: PostgreSQL 17 + RabbitMQ 4
```

### Mensageria (ADR-0006 e ADR-0007)

- **Publicar:** o caso de uso chama `IOutbox.Enqueue(type, subject, data)` e salva o `DbContext`. O evento é gravado
  em `outbox_messages` na mesma transação. O `OutboxRelay` publica em `fiapx.events` com *publisher confirms* e
  `mandatory`: sem fila vinculada, a mensagem continua na outbox, em vez de ser descartada.
- **Consumir:** `services.AddIntegrationEventConsumer<VideoProcessedData, Consumer>("video.processed")`. A fila
  `video-api.video-status`, a DLQ e os bindings são declarados ao subir. O `BasicConsume` só acontece se houver
  pelo menos um consumer registrado; sem consumer, as mensagens esperam na fila durável.

| Situação | Ação |
|---|---|
| Consumer executou (efeito e inbox gravados) | `BasicAck` |
| Mensagem já presente na inbox (duplicada) | `BasicAck` |
| `type` não tratado aqui, ou envelope sem `id`/`type` | `BasicAck` + log de aviso |
| `type` conhecido com `data` inválido | `BasicReject(requeue: false)` → DLQ, sem novas tentativas |
| Consumer lançou exceção | até `RabbitMq:MaxHandlerAttempts` (3) tentativas no processo, com backoff exponencial e jitter a partir de `RabbitMq:HandlerRetryDelay` (200 ms), cada uma em escopo e transação novos; depois `BasicReject(requeue: false)` → DLQ |
| Desligamento durante o processamento | `BasicNack(requeue: true)`, que não conta como falha |
| Processo cai no meio da mensagem | o broker reentrega, a contagem sobe e o `x-delivery-limit` manda para a DLQ |

`BasicNack(requeue: true)` nunca é usado para falha: no RabbitMQ 4 ele não incrementa o contador de entregas da
quorum queue, e a mensagem ficaria em loop. O consumer processa `RabbitMq:ConsumerConcurrency` (4) mensagens em
paralelo, com `RabbitMq:PrefetchCount` (16).

## Configuração

Chaves canônicas, iguais nos três serviços .NET. Credenciais só por variável de ambiente, nunca nos `appsettings`.

| Variável | Padrão / exemplo | Descrição |
|---|---|---|
| `ConnectionStrings__Postgres` | `Host=postgres;Database=video_db;Username=video_svc;Password=…` | Banco `video_db` |
| `RabbitMq__Uri` | `amqp://video_svc:…@rabbitmq:5672/` | Broker |
| `RabbitMq__ClientName` | `video-api` | Nome da conexão e `source` dos eventos (`fiapx/video-api`) |
| `RabbitMq__Exchange` / `RabbitMq__DeadLetterExchange` | `fiapx.events` / `fiapx.dlx` | |
| `RabbitMq__PrefetchCount` / `RabbitMq__ConsumerConcurrency` | `16` / `4` | |
| `RabbitMq__MaxHandlerAttempts` / `RabbitMq__HandlerRetryDelay` | `3` / `00:00:00.200` | Retry no processo |
| `Outbox__Enabled` / `PollInterval` / `BatchSize` / `MaxBackoff` | `true` / `00:00:01` / `50` / `00:00:30` | Relay da outbox |
| `Auth__Issuer` | `http://localhost:8080` | `iss` público (o gateway) |
| `Auth__Audience` | `fiapx-api` | |
| `Auth__MetadataAddress` | `http://identity-service:8080/.well-known/openid-configuration` | Descoberta OIDC acessível pelo serviço |
| `Auth__RequireHttpsMetadata` | `true` (`false` só localmente) | |
| `Database__MigrateOnStartup` | `false` (`true` no docker-compose) | Aplicar migrations ao subir |
| `OTEL_EXPORTER_OTLP_ENDPOINT` / `OTEL_SERVICE_NAME` | `http://otel-lgtm:4317` / `video-api` | Sem endpoint, nada é exportado |

`GET /me` devolve `sub`, `email` e `name` do token: serve para conferir que um token do identity-service é aceito aqui.

## Desenvolvimento

Requisitos: SDK .NET 10 e Docker (os testes de integração sobem Postgres e RabbitMQ com Testcontainers).

```bash
dotnet build FiapX.VideoApi.slnx
dotnet test FiapX.VideoApi.slnx
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/FiapX.VideoApi.Api   # http://localhost:8082
```

Para subir junto com o resto da plataforma, use o `docker-compose.yml` do repositório [`fiapx`](https://github.com/FiapXHacka/fiapx).

### Schema do banco

As migrations do EF Core são a fonte da verdade (ADR-0009). O `db/schema.sql`, entregável do hackathon, é a saída
idempotente delas, e o CI falha se o modelo tiver mudanças sem migration ou se o script estiver desatualizado:

```bash
dotnet tool restore     # dotnet-ef 10.0.15, fixado em .config/dotnet-tools.json
dotnet ef migrations add <Nome> -p src/FiapX.VideoApi.Infrastructure -s src/FiapX.VideoApi.Infrastructure -o Persistence/Migrations
dotnet ef migrations script --idempotent -p src/FiapX.VideoApi.Infrastructure -s src/FiapX.VideoApi.Infrastructure -o db/schema.sql
```

Com `Database__MigrateOnStartup=true`, várias réplicas podem subir juntas: o EF Core 9+ pega um lock de migração, e no
Npgsql esse lock é um `LOCK TABLE "__EFMigrationsHistory" IN ACCESS EXCLUSIVE MODE`. Só uma réplica aplica as
migrations; as outras esperam e encontram tudo aplicado.

## CI

`.github/workflows/ci.yml`: build → checagem das migrations e do `db/schema.sql` → testes (unitários e integração) com cobertura → SonarQube Cloud (análise *secretless*
via OIDC) → build da imagem. `commit-lint.yml` exige Conventional Commits em inglês, sem `Co-authored-by`.
