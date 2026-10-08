# Entrega 2 — mapa de evidências

Documento de consolidação: liga cada entregável exigido ao artefato que o
comprova. Os campos marcados com `[ ]` são os que precisam ser preenchidos
depois de rodar o pipeline.

| Entregável | Artefato no repositório | Evidência de execução |
|---|---|---|
| Dockerfile otimizado | `Dockerfile.api`, `Dockerfile.web`, `.dockerignore` | Job `imagens` do CI (build em todo PR) |
| Orquestração local | `docker-compose.yml`, `.env.example` | `docker compose ps` com os quatro serviços `healthy` |
| Pipeline de CD | `.github/workflows/cd.yml`, `k8s/` | Run do CD: publicação no GHCR + rollout no kind |
| Gestão de segredos | `docs/segredos.md`, Environments do GitHub | Job `segredos` verde + `evidencias/secret.txt` |
| Versionamento e rollback | `CHANGELOG.md`, `Directory.Build.props`, `docs/rollback.md` | `/health` reportando a versão + `rollout history` |

---

## 1. Dockerfile

Decisões que respondem ao critério de "otimizado":

- **Multi-stage com restore isolado.** Só os `.csproj` entram na camada de
  restore, então mudar código-fonte não reinvalida o download dos pacotes.
- **Runtime alpine** (`aspnet:9.0-alpine`, ~110 MB contra ~220 MB do
  bookworm-slim), viável porque `InvariantGlobalization=true` dispensa a ICU.
- **Usuário sem privilégio** via `USER $APP_UID`.
- **`HEALTHCHECK`** apontando para `/health`.
- **Rótulos OCI** com a versão, para rastrear a imagem até a release.
- **Duas imagens, não uma**: a API é um processo .NET; o Blazor WebAssembly é
  arquivo estático e vai atrás de nginx. Forçar as duas na mesma imagem
  obrigaria o ASP.NET a servir estático sem ganho algum.
- **Testes fora do Dockerfile.** Três testes exigem MongoDB (categoria
  `Integration`) e o build do container não tem banco.

Para registrar:

```bash
docker build -f Dockerfile.api -t encurtai-api:dev .
docker build -f Dockerfile.web -t encurtai-web:dev .
docker images | grep encurtai                 # [ ] tamanho das imagens
docker run --rm --entrypoint id encurtai-api:dev   # [ ] confirma não-root
```

---

## 2. Orquestração local

```bash
cp .env.example .env     # preencher MONGO_ROOT_PASSWORD
docker compose up -d --build
docker compose ps        # [ ] mongo, redis, api e web em healthy
```

Verificações:

```bash
# Saúde
curl -s localhost:5080/health
curl -s localhost:5080/health/ready

# Fluxo completo
curl -s -X POST localhost:8080/api/encurtar \
  -H 'Content-Type: application/json' \
  -d '{"url":"https://ceub.br"}'
# [ ] urlCurta precisa usar PUBLIC_BASE_URL, não o host interno do container

curl -i localhost:8080/<codigo>    # [ ] 302 para o destino

# Persistência
docker compose down && docker compose up -d
curl -i localhost:8080/<codigo>    # [ ] continua resolvendo (volume nomeado)
```

Dependências encadeadas: `api` só sobe depois de `mongo` e `redis` ficarem
`healthy`; `web` só depois de `api`. Sem isso, a API morre no boot por falta de
banco e o nginx falha ao resolver o upstream.

---

## 3. Pipeline de CD

Encadeamento: `imagens` → `homologacao` → `producao` (com gate).

O que o run comprova:

- [ ] Duas imagens publicadas em `ghcr.io/<owner>/encurtai-{api,web}`
- [ ] Tags semânticas geradas (`1.1.0`, `1.1`, `1`, `sha-...`, `latest`)
- [ ] Deploy aplicado **por digest**, visível no log do step `Aplicar manifestos`
- [ ] `rollout status` convergindo nos quatro Deployments
- [ ] Smoke test aprovado: versão conferida, link encurtado, redirect seguido
- [ ] `producao` parado em "Review deployments" aguardando aprovação
- [ ] Artefato `evidencias-homologacao-<n>` baixado

O smoke test não se contenta com "o pod subiu". Ele confere que a versão no ar é
a que foi implantada, encurta um link de verdade, valida que a URL curta usa o
endereço público configurado e segue o redirecionamento até o destino.

---

## 4. Gestão de segredos

- [ ] Environments `homologacao` e `producao` criados
- [ ] Três secrets cadastrados em cada, com valores distintos
- [ ] `producao` com *required reviewers* ativo
- [ ] Job `segredos` (gitleaks) verde
- [ ] `evidencias/secret.txt` mostrando que o Secret existe com as chaves
      esperadas, **sem** nenhum valor
- [ ] Nenhuma credencial de registry cadastrada à mão (GHCR usa `GITHUB_TOKEN`)

Detalhe que vale mencionar na defesa: o repositório nunca teve segredo
versionado — varredura em todos os commits de todas as refs não achou nada. O
job de gitleaks existe para que continue assim.

---

## 5. Versionamento e rollback

- [ ] `v1.0.0` e `v1.1.0` criadas e enviadas
- [ ] `CHANGELOG.md` com as duas versões
- [ ] `curl /api/health` reportando a versão implantada
- [ ] Simulação de falha executada (`docs/rollback.md`, seção 4)
- [ ] `kubectl describe pod` com a readinessProbe reprovando com `503`
- [ ] Aplicação respondendo **durante** a falha (prova de `maxUnavailable: 0`)
- [ ] Step `Rollback automatico` no log
- [ ] `kubectl rollout history` com as revisões
- [ ] `/health` voltando a reportar a versão anterior

---

## Defeitos encontrados e corrigidos nesta entrega

Não eram itens do enunciado. Apareceram ao preparar a containerização, e cada um
teria quebrado a entrega em execução — motivo pelo qual estão aqui.

| # | Defeito | Consequência se não tratado |
|---|---|---|
| 1 | URL curta montada com `Request.Host` | Atrás de nginx/ingress, o encurtador devolveria links apontando para o hostname interno do pod. O recurso principal do produto quebrado só no ambiente containerizado |
| 2 | URL da API cravada no Blazor WebAssembly | O frontend chamaria `localhost:5080` do navegador do usuário. Funciona na máquina do desenvolvedor e em nenhum outro lugar |
| 3 | Default de connection string no `appsettings.json` | Em container, `localhost` é o próprio container: a imagem subiria "saudável" e quebraria na primeira escrita. Erro de configuração silencioso |
| 4 | Ausência de endpoint de saúde | Sem `HEALTHCHECK` e sem probes — e, sem readiness, o rollback automático não existiria |
| 5 | Readiness levando 30s para falhar | Probe prendendo thread; timeout do driver Mongo decidindo no lugar da probe |
| 6 | Banco fora do ar devolvendo `500` com stack trace | Sinal errado para cliente e proxy; em `Development`, exposição de rastreamento interno |
| 7 | Unitários dependendo de MongoDB no CI | O trait `Category=Integration` existia mas nunca era usado como filtro |

Os defeitos 1, 3 e 5 têm cobertura de teste ou verificação automática
(`ShortUrlBuilderTests`, falha no boot, smoke test do pipeline).

---

## Limitações declaradas

Transparência sobre o que a entrega **não** é:

1. **"Produção" é simulada.** São dois clusters kind efêmeros, criados e
   destruídos no run. Não há domínio, TLS, ingress nem banco gerenciado.
2. **MongoDB de homologação é instância única** com `Deployment` + PVC, não
   replica set. Suficiente para homologação, insuficiente para produção real.
3. **Sem observabilidade.** Não há métricas, tracing nem alerta — o
   diagnóstico depende de `kubectl logs` e dos artefatos do pipeline.
4. **Sem rate limiting.** `POST /encurtar` é escrita anônima e irrestrita.
5. **Lockfiles em modo permissivo.** `packages.lock.json` é gerado, mas o
   restore ainda não roda em modo travado — isso deve ser ligado depois de
   confirmar no CI que o lockfile gerado no Windows serve ao build Linux.
6. **`dashboard/`** é um projeto alheio ao encurtador (dashboard de resultados
   eleitorais) depositado nesta pasta. Está excluído do contexto de build pelo
   `.dockerignore` e segue fora do versionamento.
