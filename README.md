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
- **Consumir:** `services.AddIntegrationEventConsumer<VideoProcessedData, Handler>("video.processed")`. O handler roda na
  transação do consumer junto com o registro em `inbox_messages`. O ack só acontece depois do commit. Uma exceção gera até 3 tentativas
  no próprio processo (backoff de 1 s, 2 s) e depois `nack` sem requeue, que manda a mensagem para
  `video-api.video-status.dlq`. O `x-delivery-limit` da fila cobre o caso de o processo cair no meio da mensagem. Sem handler registrado o
  consumer não inicia, e as mensagens esperam na fila durável.

## Configuração

| Variável | Exemplo | Descrição |
|---|---|---|
| `ConnectionStrings__Postgres` | `Host=postgres;Database=video_db;Username=video_svc;Password=…` | Banco `video_db` |
| `RabbitMq__Uri` | `amqp://video_svc:…@rabbitmq:5672/` | Broker |
| `Auth__Issuer` | `http://localhost:8080` | Valor do `iss` dos tokens |
| `Auth__MetadataAddress` | `http://identity-service:8080/.well-known/openid-configuration` | Descoberta OIDC do identity-service (com `jwks_uri`) |
| `Auth__RequireHttpsMetadata` | `false` (local) | Exigir HTTPS na descoberta |
| `Database__MigrateOnStartup` | `true` (local) | Aplicar migrations ao subir |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://otel-lgtm:4317` | Sem ela, nada é exportado |
| `OTEL_SERVICE_NAME` | `video-api` | |

## Desenvolvimento

Requisitos: SDK .NET 10 e Docker (os testes de integração sobem Postgres e RabbitMQ com Testcontainers).

```bash
dotnet build FiapX.VideoApi.slnx
dotnet test FiapX.VideoApi.slnx
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/FiapX.VideoApi.Api   # http://localhost:8082
```

Para subir junto com o resto da plataforma, use o `docker-compose.yml` do repositório [`fiapx`](https://github.com/FiapXHacka/fiapx).

Nova migration (depois, regenere o `db/schema.sql`, que é a entrega do script do banco):

```bash
dotnet ef migrations add <Nome> -p src/FiapX.VideoApi.Infrastructure -s src/FiapX.VideoApi.Infrastructure -o Persistence/Migrations
dotnet ef migrations script -p src/FiapX.VideoApi.Infrastructure -s src/FiapX.VideoApi.Infrastructure -o db/schema.sql
```

## CI

`.github/workflows/ci.yml`: build → testes (unitários e integração) com cobertura → SonarQube Cloud (análise *secretless*
via OIDC) → build da imagem. `commit-lint.yml` exige Conventional Commits em inglês, sem `Co-authored-by`.
