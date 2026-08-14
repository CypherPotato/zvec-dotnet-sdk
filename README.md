# OpenIndexer.ZVec

Typed C# bindings for the native [ZVec](https://github.com/alibaba/zvec) C API. The library uses P/Invoke directly against `zvec_c_api` and does not implement alternative or in-memory storage.

## Installation and native runtime

OpenIndexer.ZVec currently targets `net10.0`, so consumers need a .NET 10-compatible project and runtime. The first public package has not been released yet. After it is published to NuGet.org, install it with:

```bash
dotnet add package OpenIndexer.ZVec
```

Published NuGet packages are designed to include precompiled native runtimes and copy the matching asset to the application output; consumers will not need to compile ZVec:

| RID | Library | HNSW-RaBitQ |
| --- | --- | --- |
| `win-x64` | `zvec_c_api.dll` | No |
| `linux-x64` | `libzvec_c_api.so` | Yes, on AVX2 or newer CPUs |
| `linux-arm64` | `libzvec_c_api.so` | No |
| `osx-arm64` | `libzvec_c_api.dylib` | No |

Release runtimes are built from the ZVec submodule pinned to `ec8a78ee08b14a0b8c94158ffc1de42cd3f97f6d` and include this repository's versioned ABI extension. The managed binding rejects RaBitQ before collection creation unless the process is Linux x64 with AVX2; eligible Linux hosts then pass extension version, commit, and capability checks. It never silently converts the request to Flat. Once a version is published, its native ZIP files, SHA-256 checksums, and NuGet packages will be attached to the corresponding [GitHub Release](https://github.com/CypherPotato/zvec-dotnet-sdk/releases).

The supported ABI is **ZVec C API 0.6.x**. Loading explicitly fails for a different 0.x version line. Upstream notices and licenses are included in the package under `THIRD-PARTY-NOTICES.md` and `licenses/`.

To override the packaged runtime, set `ZVEC_LIBRARY_PATH` to either the library file or the directory containing it:

```powershell
$env:ZVEC_LIBRARY_PATH = "C:\native\zvec_c_api.dll"
```

```bash
export ZVEC_LIBRARY_PATH=/opt/zvec/libzvec_c_api.so
```

The loader first checks the configured override, then searches for the native name in the application directory and under `runtimes/<RID>/native`:

- `zvec_c_api.dll`
- `libzvec_c_api.so`
- `libzvec_c_api.dylib`

For indexes exposed by the stock C API, the official library can be built directly from the v0.6.0 tag. HNSW-RaBitQ requires this repository's versioned extension because the stock C factory converts requested type `4` to Flat (`3`) and does not export the dedicated build and query parameter types.

The extension is not a second library: `bindings/native/zvec_rabitq_extension.cc` is compiled into the same fat `zvec_c_api` target at the pinned ZVec commit. This keeps creation, RTTI, ownership, `shared_ptr`, registries, and destruction within the same module.

## Source code and releases

- `core/zvec/`: official `alibaba/zvec` submodule pinned to the validated commit.
- `bindings/dotnet/`: managed binding and NuGet project.
- `bindings/native/`: versioned C ABI extension for HNSW-RaBitQ.
- `scripts/build-native.ps1`: local native build by RID, without Docker.
- `.github/workflows/release.yml`: native matrix, validation, GitHub Release, and NuGet packaging.

Tags matching `v*` build each runtime on a runner for its own platform. The packaging job downloads the ZIP files from the draft GitHub Release, validates `SHA256SUMS.txt`, extracts exactly the four RIDs, and only then runs `dotnet pack`. The `.nupkg` does not download code or binaries during restore or at runtime.

Publishing to NuGet.org is deliberately separate: the `Publish NuGet` workflow receives an already published tag, downloads the assets from that GitHub Release again, verifies their checksums, recreates the package, and pushes it using the `NUGET_API_KEY` secret. This ensures that no native compilation occurs during publication and that a credential failure does not invalidate the binary release.

## Usage

Every schema has a required persistent identifier through `ZVecCollectionSchema.Id`. Supported scalar properties are `string`, `bool`, `int`, `long`, `uint`, `ulong`, `float`, and `double`. Vectors are `float[]` properties marked with `ZVecVector`.

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

Indexes and build parameters can be configured explicitly per field. The defaults published by the binding are the actual ABI 0.6 defaults, such as HNSW `M=50` and `EfConstruction=500`:

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

Available index options are Flat, HNSW, HNSW-RaBitQ, IVF, DiskANN, inverted, and full-text. Standard vector indexes support FP16, INT8, and INT4 quantization; rotation is valid with INT8 and INT4. HNSW-RaBitQ always uses its own quantization and rejects generic quantization or rotation. Query parameters are distinct types and must match the index type configured for the current `ZVecCollection` instance. With the default `ValidateExistingSchema = true`, that configuration is checked against the persisted index; disabling validation removes this guarantee.

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

Actual ZVec 0.6 backend requirements are Linux x86_64, an AVX2 or newer CPU, dense FP32 vectors with 64–4095 dimensions, and the L2, InnerProduct, or Cosine metric. Validated ranges are `TotalBits` 1–9, `ClusterCount > 0`, `M` 5–1024, `EfConstruction` 1–2048, `SampleCount >= 0` (`0` uses all vectors), and query `Ef` 1–2048. The respective defaults are 7, 16, 50, 500, 0, and 300.

During creation, the binding validates the effective type returned by the native index-parameter factory. During reopening, it validates the persisted schema's index type and RaBitQ parameters. If RaBitQ resolves to Flat or any other type, it throws `ZVecCompatibilityException`; it never labels the index using only the requested type. The exception includes the native version, platform, architecture, library path, requested feature, effective type when available, and technical reason.

`ZVecFullTextIndexOptions` configures the tokenizer, filters, and additional JSON parameters. `FullTextQueryAsync` executes either natural `Match` or structured `Query`. `Filter` in vector query parameters uses ZVec's native expression syntax and benefits from inverted indexes.

`CreateOrOpenAsync` returns `Task<ZVecCollection<TSchema>>` and offloads the synchronous native call to the thread pool. Operations on an instance are serialized to protect the native handle and capture `thread_local` errors on the same thread as the call.

The overload accepting `IReadOnlyList<ReadOnlyMemory<float>>` performs an independent top-K search for each vector. The returned list is flattened; `ZVecQueryResult.QueryIndex` identifies the corresponding input vector.

`ListAsync` uses a native vectorless query supported by ZVec, with `topK` equal to the current document count. The query is a materialized snapshot; the `CancellationToken` is observed before work begins and between yielded items. C API 0.6 does not provide cooperative cancellation for an already running native call.

## Error handling

Native failures are reported as `ZVecException`; inspect `ErrorCode` to distinguish invalid arguments, unavailable resources, unsupported operations, and internal failures. `ZVecCompatibilityException` describes native version, platform, architecture, symbol, and effective-index incompatibilities. Schema and query validation use standard argument or invalid-operation exceptions, and calls after disposal throw `ObjectDisposedException`.

```csharp
try
{
    await collection.OptimizeAsync();
}
catch (ZVecCompatibilityException exception)
{
    Console.Error.WriteLine($"{exception.RequestedFeature}: {exception.Message}");
}
catch (ZVecException exception)
{
    Console.Error.WriteLine($"ZVec error {exception.ErrorCode}: {exception.Message}");
}
```

A missing native runtime causes `DllNotFoundException`; install a package containing the current RID or set `ZVEC_LIBRARY_PATH` to a compatible library.

## Build and tests

```powershell
dotnet build .\bindings\dotnet\OpenIndexer.ZVec.csproj -c Release
$env:ZVEC_LIBRARY_PATH = "C:\native\zvec_c_api.dll"
dotnet test .\tests\OpenIndexer.ZVec.Tests\OpenIndexer.ZVec.Tests.csproj -c Release
```

Native contributor builds require PowerShell, Git, CMake, Ninja, a clean recursive submodule checkout, and the matching platform toolchain and dependencies. Run the script on the target operating system and architecture; it is not a cross-compilation wrapper:

```powershell
.\scripts\build-native.ps1 -Rid win-x64
```

The test suite uses temporary directories and the real ZVec backend. It covers creation and reopening, persistence, CRUD, listing, single and multiple queries, Flat, HNSW, HNSW-RaBitQ on supported Linux systems, IVF, INT8 quantization, inverted indexes, FTS, filters, `OptimizeAsync`, statistics and completeness, schema incompatibility, RaBitQ platform and symbol diagnostics, validation, and disposal. Positive RaBitQ tests run only on supported Linux x64 AVX2 hosts. DiskANN currently has managed option/default coverage; functional DiskANN coverage still needs a compatible Linux runtime in CI.

## Limitations

- The ABI is pinned to ZVec C API 0.6.x; later 0.x versions must be reviewed before they are accepted.
- Supported RIDs depend on a corresponding native library. Stock indexes are also validated on Windows x64; HNSW-RaBitQ is validated only with the extended Linux x64 fat library.
- Native ZVec calls are synchronous. The binding avoids blocking the calling thread by offloading work, but it cannot interrupt a native operation after it has started.
- Changing C# options after a collection is created does not migrate the persisted index. By default, `CreateOrOpenAsync` validates the schema and parameters and fails with an explicit recreation or migration message.
- `OptimizeThreadCount` is a global ZVec runtime setting and can only be set by the process's first initialization. `OptimizeAsync` runs the blocking native operation on a worker; there is no progress callback or cancellation after it starts.
- Statistics expose document counts and vector-field completeness. ABI 0.6 does not report detailed progress, size, job state, INVERT state, or FTS state.
- `EnableExtendedWildcard` is accepted during creation, but v0.6 does not serialize it in the schema protobuf; it returns to its default after reopening the collection.
- DiskANN is officially supported only on Linux.
- The pinned version's HNSW-RaBitQ backend requires Linux x86_64 and AVX2. Windows, ARM, a stock DLL without the extension, an extension with a mismatched API or commit, and builds with `RABITQ_SUPPORTED=0` are rejected before collection creation.
- HNSW-RaBitQ is available only in the `linux-x64` asset; the binding verifies the platform, AVX2 support, extension version, ZVec commit, and native capability before creating the collection.
