# Expenses Service

API em ASP.NET Core (.NET 10). A solution `Expenses.sln` contém os projetos
`Expenses.Api`, `Expenses.Application`, `Expenses.Domain`, `Expenses.Infrastructure`
e `Expenses.UnitTests` (somente testes unitários). Integrações ficam no projeto irmão `expenses-tests`.

O `GET /health` informa se a API responde. O `GET /health/db` verifica se a aplicação
consegue acessar o PostgreSQL. O `GET /v1/identity/users` consulta
`identity.users` no PostgreSQL e valida o fluxo API → service → repository → banco.
Esta listagem é um diagnóstico local: fica disponível apenas em `Development`.
Em `Production`, `Staging` e demais ambientes, a rota não é registrada e retorna
`404`. A publicação desse recurso depende de uma política de autorização futura.

## Integração Cognito — fundação de Identity

O backend possui um adaptador Cognito registrado na injeção de dependência, com
SDK AWS v4 isolado em Infrastructure e contrato em Application. Ele suporta
criação administrativa sem envio de mensagens, definição de senha permanente,
consulta do principal, login administrativo, rotação de refresh token, revogação
e exclusão compensatória. Cada chamada tem limite de três segundos, propaga
cancelamento e não executa retries automáticos. Exceções externas são convertidas
em erros categorizados sem reter payloads ou mensagens do fornecedor.

O `POST /v1/identity/users` agora cria a conta e o perfil com idempotência,
reserva concorrente, auditoria e recuperação de falhas parciais. Login HTTP, bloqueio por e-mail, cookies/CSRF e revogação local imediata também estão implementados; veja a seção de sessões abaixo.
Timeout de escrita pode ter resultado desconhecido; a API nunca exclui uma conta
por e-mail sem comprovar que pertence à operação.

## Cadastro pela API

Enviar JSON com `email` e `password` e, preferencialmente, `Idempotency-Key`
(UUID aleatório). O e-mail é normalizado para minúsculas e espaços externos são
removidos; limite final de 128 caracteres ASCII. Senha de 8 a 256 caracteres,
com letra e algarismo ASCII, sem espaços/controles, sem normalização. Corpo até
8 KB; campos desconhecidos são rejeitados.

`201` retorna `{ "user": { "id": "<uuid>", "email": "usuario@exemplo.com", "createdAt": "<UTC>" } }`.
Não retorna tokens, cookies ou Location. Mesma chave/e-mail repete o resultado
por 24 horas da reserva sem mudar a senha. Chave em execução, expirada, reutilizada
para outro e-mail ou novo cadastro com e-mail existente resulta em `409`.

Erros usam ProblemDetails com `code`, `traceId` W3C e `correlationId` UUID.
`400`: entrada inválida; `422`: política de senha; `413`: tamanho; `415`: formato;
`403`: origem não permitida; `503`: indisponibilidade/reconciliação; `500`: falha
inesperada sanitizada. Sucesso/erro usam `Cache-Control: no-store`.

Aplicar `sprint-2-001` antes de usar o cadastro. O caso de uso possui orçamento
cooperativo de cinco segundos após leitura HTTP; chamadas Cognito continuam
limitadas a três segundos sem retry. Consulte [contrato e evidências E2](../expenses-docs/fdd/FDD-Criacao-Usuario-Autenticacao/2-desenvolvimento.md#cadastro-e2)
e [implantação e reconciliação](../expenses-docs/fdd/FDD-Criacao-Usuario-Autenticacao/4-operacao.md).

Configuração usada ao resolver o adaptador:

| Chave | Uso |
| --- | --- |
| `Cognito:Region` | Região do User Pool. |
| `Cognito:UserPoolId` | Pool de identidade. |
| `Cognito:ClientId` | App Client confidencial. |
| `Cognito:ClientSecret` | Segredo privado, nunca no appsettings versionado. |
| `AWS:Profile` | Perfil local opcional; sem ele, usa a cadeia padrão do SDK, incluindo roles AWS. |

O perfil `expenses-dev` está configurado apenas em Development. O pacote
`AWSSDK.Signin` permite usar o login de console feito com
`aws login --profile expenses-dev`. O segredo local pode ser informado sem
eco/histórico com `python3 ../expenses-infrastructure/scripts/configure-cognito-secret.py`. User Secrets
ficam fora do repositório, mas não são um cofre criptografado; destinam-se ao
desenvolvimento local. Containers não recebem automaticamente esses segredos
nem o perfil do host. Não copiar credenciais para a imagem.

A configuração é validada quando o adaptador é resolvido, sem fazer acesso AWS
na inicialização ou no health check. Assim os diagnósticos existentes continuam
independentes da configuração Cognito. Os histogramas e counters do adaptador
usam o Meter `Expenses.Identity.Cognito`, com duração em segundos e tags de
operação/resultado; spans usam ActivitySource do mesmo nome. Exportação para
OpenTelemetry/CloudWatch ainda não foi configurada.

### Validação real e isolada de Cognito

A suíte comum não acessa a AWS. Para executar explicitamente o teste remoto,
com User Secrets configurado e perfil autenticado, a partir de `expenses-service`:

```bash
EXPENSES_RUN_COGNITO_TESTS=1 \
EXPENSES_COGNITO_TEST_POOL_ID=us-east-1_DyDv5WCxr \
python3 ../expenses-tests/scripts/external.py
```

Faça build antes se o código mudou. O teste exige que o pool configurado coincida
com o destino explícito. Ele cria somente um usuário aleatório
`expenses-check-<uuid>@example.invalid`, sem enviar mensagem, e verifica criação,
senha permanente, login imediato, duração do access token, rotação e rejeição do
refresh após revogação. A limpeza consulta a identidade antes de excluir e
confirma sua remoção. Em falha de limpeza, o erro informa somente o identificador
sintético para recuperação manual. Senhas e tokens não são impressos.

Em 2026-10-07, essa validação passou no pool de desenvolvimento e a limpeza foi
confirmada. O teste não grava perfis no PostgreSQL, não testa os futuros endpoints
HTTP nem comprova revogação imediata na API. Durações individuais são apenas
amostras; não comprovam a meta p95 < 150 ms.

## Requisitos

- SDK .NET 10 instalado. Verifique com `dotnet --version`.
- PostgreSQL local do Docker Desktop, com o master Liquibase aplicado (sprints 1, 2 e 3).
- Para o teste isolado completo: Python 3, Docker CLI, Liquibase e Java.

## Conexão local

A API lê `ConnectionStrings:Expenses`. Neste ambiente, a conexão com
`127.0.0.1:5432/expenses`, usuário `admin`, já está configurada em **User Secrets**,
usando as credenciais de `expenses-infrastructure/.env`. A senha não fica no código nem no README.
O perfil `Development` carrega esses segredos automaticamente. Após alterar a
senha do banco, atualize os User Secrets também.

Em outra máquina, configure a chave com `dotnet user-secrets` no projeto
`Expenses.Api`, ou forneça a variável `ConnectionStrings__Expenses` ao processo.
Formato Npgsql, com a senha do ambiente no lugar do marcador:

```text
Host=127.0.0.1;Port=5432;Database=expenses;Username=admin;Password=<senha>;Timeout=5
```

O .NET não carrega automaticamente o `.env` do Docker nem o arquivo de configuração
do Liquibase. A conexão da API é independente da configuração JDBC do Liquibase.
Sem uma conexão válida, a consulta de usuários falha; `/health` não abre conexão.

O schema é mantido exclusivamente pelo [Liquibase](../expenses-liquibase/README.md).
O backend não executa `EnsureCreated`, migrations do EF Core ou seed de usuários
durante a inicialização.

## Iniciar a aplicação

Para executar em container, a partir da raiz entre no diretório de infraestrutura:

```bash
cd expenses-infrastructure
docker --context desktop-linux compose up -d --build --wait backend
```

Esse comando inicia também o PostgreSQL. A API fica em `http://localhost:5183`,
com conexão fornecida pelo Compose a partir de `expenses-infrastructure/.env`. O build publica em
Release, mas o ambiente local é `Development` por padrão. Veja as instruções de
frontend, logs e encerramento no [guia de infraestrutura](../expenses-infrastructure/README.md).

Para executar diretamente pelo SDK, siga os passos abaixo.

Na raiz do repositório, entre no diretório do serviço:

```bash
cd expenses-service
```

Inicie a API usando o perfil HTTP:

```bash
dotnet run --project Expenses.Api --launch-profile http
```

O comando restaura as dependências, compila o projeto e inicia o servidor em
`http://localhost:5183`, no ambiente `Development`. Mantenha o terminal aberto
enquanto estiver usando a aplicação.

## Swagger / OpenAPI

Com a API em execução no ambiente `Development`:

- Interface Swagger UI: <http://localhost:5183/swagger>
- Documento OpenAPI JSON: <http://localhost:5183/swagger/v1/swagger.json>
- No perfil HTTPS, a interface fica em <https://localhost:7285/swagger>.

A documentação inclui `POST /v1/identity/users`, `GET /health`, `GET /health/db` e `GET /v1/identity/users`, os parâmetros
`page`/`pageSize`, seus limites e valores padrão, os modelos de resposta e os
códigos HTTP. Use **Try it out** e **Execute** para chamar os endpoints. O JSON
também pode ser importado no Postman.

Swagger UI e JSON ficam disponíveis somente em `Development`. Os perfis de
inicialização apontam para `swagger` nas IDEs que abrem o navegador automaticamente.
As descrições vêm dos comentários XML dos controllers e DTOs; a geração do contrato
é feita pelo Swashbuckle a partir dos endpoints e de seus metadados.

## Verificar o health check

Abra `http://localhost:5183/health` no navegador ou execute em outro terminal:

```bash
curl -i http://localhost:5183/health
```

A resposta esperada é HTTP `200 OK` com o corpo:

```json
{"status":"up"}
```

Para verificar o acesso ao banco pela aplicação:

```bash
curl -i http://localhost:5183/health/db
```

| Resultado | HTTP | Corpo |
| --- | --- | --- |
| Banco acessível | `200 OK` | `{"status":"up"}` |
| Falha de conexão, configuração inválida ou timeout | `503 Service Unavailable` | `{"status":"down"}` |

O endpoint está disponível em todos os ambientes e usa o `ExpensesDbContext` com
a mesma `ConnectionStrings:Expenses` da aplicação. A verificação chama
`Database.CanConnectAsync`, sem consultar tabelas de negócio, alterar dados ou
validar migrations. Um banco acessível pode estar com o schema desatualizado.
O [Health Checks do ASP.NET Core](https://learn.microsoft.com/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0)
executa a verificação em um escopo próprio, com timeout de cinco segundos e
cancelamento da requisição. Nesse escopo, o timeout de abertura da conexão também
é limitado a cinco segundos, preservando valores menores já configurados.
Não há retry automático nem cache do resultado.
A resposta contém apenas o estado, sem detalhes de conexão ou credenciais.
O `/health` permanece independente da disponibilidade do banco.

## Consultar usuários

```bash
curl -i 'http://localhost:5183/v1/identity/users?page=1&pageSize=20'
```

Resposta com banco vazio:

```json
{
  "items": [],
  "page": 1,
  "pageSize": 20,
  "totalCount": 0
}
```

Cada item contém somente `id`, `email`, `createdAt` e `updatedAt`; datas usam UTC.
`cognito_sub` e `normalized_email` não são expostos. A resposta não deve ser armazenada
em cache. Não há filtros nem ordenação configurável nesta versão.

| Regra | Comportamento |
| --- | --- |
| `page` omitido | Página 1. |
| `pageSize` omitido | 20 itens. |
| Limites | `page >= 1`, `1 <= pageSize <= 100`; offset precisa caber em `int`. |
| Ordenação | `id` crescente, usando a PK do PostgreSQL. |
| Banco vazio ou página após o fim | `200` com `items: []`; total global preservado. |
| Parâmetro inválido | `400` com `ValidationProblemDetails`, antes de consultar o banco. |
| Indisponibilidade transitória ou timeout | `503` com `ProblemDetails`. |
| Erro inesperado, schema ausente ou configuração inválida | `500` com `ProblemDetails`. |
| Ambiente diferente de `Development` | `404`, sem acessar o banco. |

Erros retornam mensagem genérica e `traceId`, sem detalhes de SQL, conexão ou
credenciais. O orçamento de consulta é de cinco segundos, propagando o cancelamento
do cliente. Não há retry automático. Contagem e itens usam duas consultas sequenciais,
sem transação de snapshot; alterações concorrentes podem afetar essa visão. A leitura
usa `AsNoTracking` e paginação no PostgreSQL.

## Build e testes

A partir de `expenses-service`:

```bash
dotnet build Expenses.sln
dotnet test Expenses.sln --no-build
```

Essa solução agora executa **46 testes unitários**, sem HTTP, banco ou AWS reais.
Os 49 testes de integração e os três testes Cognito foram movidos para o projeto
irmão [expenses-tests](../expenses-tests/README.md).

Para as integrações locais, o comando de compatibilidade permanece:

```bash
python3 ../expenses-infrastructure/scripts/test-postgres.py
```

Ele cria um container PostgreSQL próprio com credenciais aleatórias, aplica Liquibase,
exige 49 aprovações sem ignorados e remove somente os recursos daquela execução.
Não lê o `.env` do desenvolvimento, não usa `expenses-postgres` e não executa Cognito.
Os runners de HTTPS, navegador, carga e segurança estão documentados em `expenses-tests`.

## Encerrar a aplicação

No terminal em que a API está sendo executada, pressione `Ctrl+C`.
Aguarde a mensagem `Application is shutting down...` e o retorno do prompt.

Após o encerramento, `http://localhost:5183/health` deixa de responder.
Para iniciar novamente, execute o mesmo comando `dotnet run` acima.

## Cadastro com PostgreSQL e Cognito reais

A partir de `expenses-service`, com Docker/PostgreSQL disponível, SDK compilável,
perfil AWS autenticado e User Secrets configurado:

```bash
EXPENSES_RUN_COGNITO_TESTS=1 \
EXPENSES_COGNITO_TEST_POOL_ID=us-east-1_DyDv5WCxr \
EXPENSES_COGNITO_REGISTRATION_SAMPLES=20 \
python3 ../expenses-tests/scripts/external.py
```

Esse comando cria banco descartável, aplica o master e executa a suíte incluindo
cadastro HTTP, login direto no Cognito para verificar a credencial criada, replay
e limpeza confirmada. O número de amostras admite 1 a 30; o padrão é 1. O teste
recusa outro pool que não corresponda à configuração. O cenário de cadastro verifica a credencial diretamente; o cenário CognitoSessions adicional cobre também login HTTP. `EXPENSES_REGISTRATION_REPORT_PATH` opcional grava somente
tempos e metadados não secretos em JSON.

Em 2026-10-07 a suíte integrada passou com 78 testes. Vinte amostras sequenciais
resultaram em p95 **1202,96 ms**, acima da meta de 150 ms. É evidência local com
TestServer, PostgreSQL local e Cognito real; não comprova latência de produção.
[Relatório](../expenses-docs/fdd/FDD-Criacao-Usuario-Autenticacao/3-testes-latencia-cadastro.json).

## Login, sessões e logout

A entrega [E3/E4](../expenses-docs/fdd/FDD-Criacao-Usuario-Autenticacao/2-desenvolvimento.md#login-e-sessoes-e3-e4) documenta contratos,
concorrência, recuperação e implantação. O frontend ainda não possui essas telas.
Aplicar `sprint-3-001` e executar `python3 ../expenses-infrastructure/scripts/configure-session-secret.py`
para configurar a chave privada HMAC de e-mail; no ambiente atual ela já foi salva.

Em HTTPS, obter `GET /v1/identity/csrf` e enviar o token retornado no header
`X-CSRF-Token`, preservando cookies, para:

- `POST /v1/identity/sessions`: JSON email/password; cria sessão.
- `GET /v1/identity/session/current`: consulta ou renova sessão.
- `DELETE /v1/identity/sessions/current`: revoga sessão imediatamente; `204` sem corpo.

Dez credenciais inválidas em 15 minutos bloqueiam somente o e-mail por 15 minutos.
As últimas três tentativas retornam aviso. Sessões usam cookies Secure/HttpOnly/
SameSite=Strict e estado no PostgreSQL; logout prevalece sobre refresh concorrente.
Falha de renovação limpa cookies e encerra autorização. Falha de persistência do
logout retorna `503`, sem afirmar que a revogação foi confirmada.

`[Authorize]` aplica a política de sessão para novos endpoints; não aceita bearer
JWT isolado nem consulta cache positivo. Escritas também exigem proteção CSRF e
autorização por recurso. A listagem global de usuários permanece diagnóstico local.

A suíte remota já descrita inclui `Category=CognitoSessions`. Para salvar tempos
não secretos, usar `EXPENSES_SESSION_REPORT_PATH` opcional. O teste cria/remove uma
conta sintética e usa banco descartável; não atualiza `expenses`. A medição funcional
local não comprova p95 < 150 ms. Para múltiplas réplicas, provisionar key ring Data
Protection durável, compartilhado e protegido, além da mesma chave HMAC; não usar
chaves efêmeras ou copiar credenciais para imagens.

Validação histórica E3/E4, antes da separação das suítes: **98 testes aprovados, zero ignorados**, incluindo PostgreSQL e Cognito reais; validação de upgrade/rollback Liquibase aprovada. As migrações ainda precisam ser aplicadas ao banco local `expenses` para uso da API fora dos testes.
