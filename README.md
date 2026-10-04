# Expenses Service

API em ASP.NET Core (.NET 10). A solution `Expenses.sln` contém os projetos
`Expenses.Api`, `Expenses.Application`, `Expenses.Domain`, `Expenses.Infrastructure`
e `Expenses.Test`.

O `GET /health` informa se a API responde. O `GET /v1/identity/users` consulta
`identity.users` no PostgreSQL e valida o fluxo API → service → repository → banco.
Esta listagem é um diagnóstico local: fica disponível apenas em `Development`.
Em `Production`, `Staging` e demais ambientes, a rota não é registrada e retorna
`404`. A publicação desse recurso depende de uma política de autorização futura.

## Requisitos

- SDK .NET 10 instalado. Verifique com `dotnet --version`.
- PostgreSQL local do Docker Desktop, com a sprint-1 do Liquibase aplicada.
- Para o teste isolado completo: Python 3, Docker CLI, Liquibase e Java.

## Conexão local

A API lê `ConnectionStrings:Expenses`. Neste ambiente, a conexão com
`127.0.0.1:5432/expenses`, usuário `admin`, já está configurada em **User Secrets**,
usando as credenciais do `.env` da raiz. A senha não fica no código nem no README.
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

A documentação inclui `GET /health` e `GET /v1/identity/users`, os parâmetros
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

Essa suíte verifica paginação, mapeamento, respostas HTTP, parâmetros inválidos,
erros sanitizados, cancelamento/timeout, restrição por ambiente e independência do
health check. Os testes HTTP usam `WebApplicationFactory`. O cenário com banco real
fica explicitamente marcado como ignorado quando a conexão de teste não foi fornecida.

Para executar **todos** os testes, incluindo PostgreSQL real:

```bash
python3 scripts/test-postgres.py
```

O script usa o container `expenses-postgres` no contexto `desktop-linux`, obtém as
credenciais do `.env` da raiz ou de `POSTGRES_USER`/`POSTGRES_PASSWORD`, cria um banco
aleatório `expenses_users_check_*`, aplica o Liquibase e executa a suíte. O teste
insere três usuários sintéticos nesse banco e verifica ordenação, páginas, colunas
geradas, datas UTC e ausência de tracking. O banco temporário é removido ao final;
nenhum usuário de teste é inserido em `expenses`. Se algum teste falhar, o comando
retorna erro após tentar a limpeza.

`EXPENSES_TEST_CONNECTION_STRING` é preenchida pelo script. O teste recusa bancos
cujo nome não comece com `expenses_users_check_`.

## Encerrar a aplicação

No terminal em que a API está sendo executada, pressione `Ctrl+C`.
Aguarde a mensagem `Application is shutting down...` e o retorno do prompt.

Após o encerramento, `http://localhost:5183/health` deixa de responder.
Para iniciar novamente, execute o mesmo comando `dotnet run` acima.
