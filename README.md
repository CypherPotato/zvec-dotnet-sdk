# OpenIndexer.ZVec

Bindings C# tipados para a C API nativa do [ZVec](https://github.com/alibaba/zvec). A biblioteca usa P/Invoke diretamente sobre `zvec_c_api` e não implementa armazenamento alternativo ou em memória.

## Instalação e runtime nativo

```bash
dotnet add package OpenIndexer.ZVec
```

O pacote NuGet inclui runtimes nativos pré-compilados e os copia para a saída da aplicação; o consumidor não precisa compilar o ZVec:

| RID | Biblioteca | HNSW-RaBitQ |
| --- | --- | --- |
| `win-x64` | `zvec_c_api.dll` | Não |
| `linux-x64` | `libzvec_c_api.so` | Sim, em CPUs AVX2 ou superiores |
| `linux-arm64` | `libzvec_c_api.so` | Não |
| `osx-arm64` | `libzvec_c_api.dylib` | Não |

Todos os runtimes são compilados do submodule ZVec fixado em `ec8a78ee08b14a0b8c94158ffc1de42cd3f97f6d` e contêm a extensão ABI versionada deste repositório. Em plataformas sem suporte upstream, a extensão informa que RaBitQ está indisponível; ela nunca converte silenciosamente a solicitação em Flat. Os ZIPs nativos, checksums SHA-256 e pacotes NuGet de cada versão ficam disponíveis na [GitHub Release](https://github.com/CypherPotato/zvec-dotnet-sdk/releases) correspondente.

A ABI suportada é **ZVec C API 0.6.x**. O carregamento falha explicitamente para outra linha de versão 0.x. Os avisos e licenças upstream acompanham o pacote em `THIRD-PARTY-NOTICES.md` e `licenses/`.

Para substituir o runtime empacotado, defina `ZVEC_LIBRARY_PATH` como o arquivo da biblioteca ou como o diretório que o contém:

```powershell
$env:ZVEC_LIBRARY_PATH = "C:\native\zvec_c_api.dll"
```

```bash
export ZVEC_LIBRARY_PATH=/opt/zvec/libzvec_c_api.so
```

O loader procura primeiro o override configurado e depois o nome nativo no diretório da aplicação e em `runtimes/<RID>/native`:

- `zvec_c_api.dll`
- `libzvec_c_api.so`
- `libzvec_c_api.dylib`

Para os índices expostos pela C API stock, a biblioteca oficial pode ser compilada diretamente do tag v0.6.0. HNSW-RaBitQ exige a extensão versionada deste repositório porque o factory C stock transforma o tipo solicitado `4` em Flat (`3`) e não exporta os tipos próprios de construção/query.

A extensão não é uma segunda biblioteca: `bindings/native/zvec_rabitq_extension.cc` é compilado dentro do mesmo target fat `zvec_c_api`, no commit ZVec fixado. Isso mantém criação, RTTI, ownership, `shared_ptr`, registries e destruição no mesmo módulo.

## Código-fonte e releases

- `core/zvec/`: submodule oficial `alibaba/zvec`, fixado no commit validado.
- `bindings/dotnet/`: binding gerenciado e projeto NuGet.
- `bindings/native/`: extensão C ABI versionada para HNSW-RaBitQ.
- `scripts/build-native.ps1`: build nativo local por RID, sem Docker.
- `.github/workflows/release.yml`: matriz nativa, validação, GitHub Release e empacotamento NuGet.

Tags `v*` compilam cada runtime em um runner da própria plataforma. O job de empacotamento baixa os ZIPs da GitHub Release em draft, valida `SHA256SUMS.txt`, extrai exatamente os quatro RIDs e só então executa `dotnet pack`. O `.nupkg` não baixa código ou binários durante restore nem em runtime.

A publicação no NuGet.org é deliberadamente separada: o workflow `Publish NuGet` recebe uma tag já publicada, baixa novamente os assets dessa GitHub Release, verifica os checksums, recria o pacote e envia com o secret `NUGET_API_KEY`. Assim, nenhuma compilação nativa ocorre durante a publicação e uma falha de credencial não invalida a release de binários.

## Uso

Todo schema possui um identificador persistente obrigatório por meio de `ZVecCollectionSchema.Id`. Propriedades escalares suportadas são `string`, `bool`, `int`, `long`, `uint`, `ulong`, `float` e `double`. Vetores são `float[]` marcados com `ZVecVector`.

```csharp
public sealed class Article : ZVecCollectionSchema
{
    public required string Title { get; init; }
    public required string Content { get; init; }

    [ZVecVector(Dimensions = 768, Metric = ZVecMetric.Cosine)]
    public float[] Embedding { get; init; } = [];

    [ZVecIndexed]
    public string Category { get; init; } = string.Empty;
}

await using var articles =
    await ZVecCollection<Article>.CreateOrOpenAsync("./articles");

await articles.UpsertAsync(article);

var results = await articles.QueryAsync(
    nameof(Article.Embedding),
    queryEmbedding,
    topK: 20);
```

Índices e parâmetros de construção podem ser definidos explicitamente por campo. Os defaults publicados pelo binding são os defaults reais da ABI 0.6, por exemplo HNSW `M=50` e `EfConstruction=500`:

```csharp
var options = new ZVecCollectionOptions
{
    OptimizeThreadCount = 6
};
options.Indexes[nameof(Article.Embedding)] = new ZVecHnswIndexOptions
{
    Metric = ZVecMetric.Cosine,
    M = 32,
    EfConstruction = 400,
    Quantization = ZVecQuantization.Int8,
    EnableQuantizerRotation = true
};
options.Indexes[nameof(Article.Category)] = new ZVecInvertedIndexOptions
{
    EnableRangeOptimization = true,
    EnableExtendedWildcard = false
};

await using var configured =
    await ZVecCollection<Article>.CreateOrOpenAsync("./articles", options);

var configuredResults = await configured.QueryAsync(
    nameof(Article.Embedding),
    queryEmbedding,
    topK: 20,
    new ZVecHnswQueryOptions { Ef = 100 });

await configured.OptimizeAsync();
var stats = await configured.GetStatsAsync();
```

Opções de índice disponíveis: Flat, HNSW, HNSW-RaBitQ, IVF, DiskANN, inverted e full-text. Índices vetoriais comuns aceitam quantização FP16, INT8 e INT4; rotação é válida com INT8/INT4. HNSW-RaBitQ usa obrigatoriamente sua própria quantização e rejeita quantização genérica/rotation. Os parâmetros de query são tipos distintos e precisam corresponder ao índice persistido.

### HNSW-RaBitQ

```csharp
var options = new ZVecCollectionOptions();
options.Indexes[nameof(Article.Embedding)] = new ZVecHnswRaBitQIndexOptions
{
    Metric = ZVecMetric.Cosine,
    TotalBits = 7,
    ClusterCount = 16,
    M = 50,
    EfConstruction = 500,
    SampleCount = 0
};

await using var collection =
    await ZVecCollection<Article>.CreateOrOpenAsync("./articles-rabitq", options);

await collection.OptimizeAsync();
var results = await collection.QueryAsync(
    nameof(Article.Embedding),
    queryEmbedding,
    topK: 20,
    new ZVecHnswRaBitQQueryOptions { Ef = 300 });
```

Requisitos reais do backend ZVec 0.6: Linux x86_64, CPU AVX2 ou superior, vetor FP32 denso com 64–4095 dimensões e métrica L2, InnerProduct ou Cosine. Faixas validadas: `TotalBits` 1–9, `ClusterCount > 0`, `M` 5–1024, `EfConstruction` 1–2048, `SampleCount >= 0` (`0` usa todos os vetores) e query `Ef` 1–2048. Defaults: 7, 16, 50, 500, 0 e 300, respectivamente.

Na criação e reabertura, o binding lê o tipo efetivo do handle/schema. Se RaBitQ resultar em Flat ou qualquer outro tipo, lança `ZVecCompatibilityException`; nunca rotula o índice pelo tipo solicitado. A exceção inclui versão nativa, plataforma, arquitetura, caminho da biblioteca, recurso solicitado, tipo efetivo quando disponível e motivo técnico.

`ZVecFullTextIndexOptions` configura tokenizer, filtros e parâmetros extras JSON. `FullTextQueryAsync` executa `Match` natural ou `Query` estruturada. `Filter` nos parâmetros de consulta vetorial usa a expressão nativa do ZVec e aproveita índices inverted.

`CreateOrOpenAsync` retorna `Task<ZVecCollection<TSchema>>` e desloca a chamada síncrona nativa para o thread pool. Operações sobre uma instância são serializadas para proteger o handle nativo e capturar erros `thread_local` no mesmo thread da chamada.

A sobrecarga com `IReadOnlyList<ReadOnlyMemory<float>>` executa uma busca top-K independente por vetor. A lista retornada é achatada; `ZVecQueryResult.QueryIndex` identifica o vetor de entrada correspondente.

`ListAsync` usa uma consulta nativa sem vetor, suportada pelo ZVec, com `topK` igual ao número atual de documentos. A consulta é um snapshot materializado; o `CancellationToken` é observado antes do trabalho e entre itens produzidos. A C API 0.6 não oferece cancelamento cooperativo de uma chamada nativa já iniciada.

## Build e testes

```powershell
dotnet build .\bindings\dotnet\OpenIndexer.ZVec.csproj -c Release
$env:ZVEC_LIBRARY_PATH = "C:\native\zvec_c_api.dll"
dotnet test .\tests\OpenIndexer.ZVec.Tests\OpenIndexer.ZVec.Tests.csproj -c Release
```

A suíte usa diretórios temporários e o backend ZVec real. Ela cobre criação e reabertura, persistência, CRUD, listagem, consultas simples e múltiplas, Flat, HNSW, HNSW-RaBitQ no Linux suportado, IVF, quantização INT8, inverted, FTS, filtros, `OptimizeAsync`, stats/completeness, incompatibilidade de schema, diagnóstico de plataforma/símbolo RaBitQ, validações e descarte. DiskANN e RaBitQ executam seus testes positivos somente em Linux.

## Limitações

- ABI fixada em ZVec C API 0.6.x; versões 0.x posteriores precisam ser revisadas antes de serem aceitas.
- RIDs suportados dependem de uma biblioteca nativa correspondente. Os índices stock são validados também no Windows x64; HNSW-RaBitQ é validado somente com a fat library estendida Linux x64.
- Chamadas nativas do ZVec são síncronas. O binding evita bloquear o thread chamador por offload, mas não consegue interromper uma operação nativa que já começou.
- Alterar as opções C# depois que uma coleção foi criada não migra o índice persistido. Por padrão, `CreateOrOpenAsync` valida schema e parâmetros e falha com uma mensagem de recriação/migração explícita.
- `OptimizeThreadCount` é configuração global do runtime ZVec e só pode ser definido pela primeira inicialização do processo. `OptimizeAsync` chama a operação nativa bloqueante em um worker; não há callback de progresso nem cancelamento depois que ela começa.
- Stats expõem contagem de documentos e completeness de campos vetoriais. A ABI 0.6 não reporta progresso detalhado, tamanho, estado de jobs, INVERT ou FTS.
- `EnableExtendedWildcard` é aceito na criação, mas a v0.6 não o serializa no protobuf do schema; ele volta ao default ao reabrir a coleção.
- DiskANN é suportado oficialmente somente em Linux.
- O backend HNSW-RaBitQ da versão fixada é Linux x86_64 + AVX2; Windows, ARM, DLL stock sem extensão, extensão com API/commit divergente e build com `RABITQ_SUPPORTED=0` são rejeitados antes da criação.
- HNSW-RaBitQ está disponível apenas no asset `linux-x64`; o binding verifica plataforma, AVX2, versão da extensão, commit ZVec e capacidade nativa antes de criar a coleção.
