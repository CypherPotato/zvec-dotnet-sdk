namespace OpenIndexer.ZVec;

/// <summary>Quantization applied while building a vector index.</summary>
public enum ZVecQuantization
{
    None,
    Float16,
    Int8,
    Int4
}

/// <summary>Base type for index settings persisted in a collection schema.</summary>
public abstract record ZVecIndexOptions;

/// <summary>Base type for vector index settings.</summary>
public abstract record ZVecVectorIndexOptions : ZVecIndexOptions
{
    public ZVecMetric Metric { get; init; } = ZVecMetric.Cosine;
    public ZVecQuantization Quantization { get; init; }
    public bool EnableQuantizerRotation { get; init; }
}

/// <summary>Exact flat vector search.</summary>
public sealed record ZVecFlatIndexOptions : ZVecVectorIndexOptions;

/// <summary>HNSW index settings. Defaults match ZVec 0.6.</summary>
public sealed record ZVecHnswIndexOptions : ZVecVectorIndexOptions
{
    public int M { get; init; } = 50;
    public int EfConstruction { get; init; } = 500;
}

/// <summary>HNSW-RaBitQ settings for the ZVec 0.6 Linux x64 AVX2 backend.</summary>
public sealed record ZVecHnswRaBitQIndexOptions : ZVecVectorIndexOptions
{
    public int TotalBits { get; init; } = 7;
    public int ClusterCount { get; init; } = 16;
    public int M { get; init; } = 50;
    public int EfConstruction { get; init; } = 500;
    public int SampleCount { get; init; }
}

/// <summary>IVF index settings. Defaults match ZVec 0.6.</summary>
public sealed record ZVecIvfIndexOptions : ZVecVectorIndexOptions
{
    public int ListCount { get; init; } = 1024;
    public int IterationCount { get; init; } = 10;
    public bool UseSoar { get; init; }
}

/// <summary>DiskANN index settings. Defaults match ZVec 0.6.</summary>
public sealed record ZVecDiskAnnIndexOptions : ZVecVectorIndexOptions
{
    public int MaxDegree { get; init; } = 100;
    public int ListSize { get; init; } = 50;
    public int PqChunkCount { get; init; }
}

/// <summary>Scalar inverted-index settings.</summary>
public sealed record ZVecInvertedIndexOptions : ZVecIndexOptions
{
    public bool EnableRangeOptimization { get; init; } = true;
    public bool EnableExtendedWildcard { get; init; }
}

/// <summary>Full-text index settings.</summary>
public sealed record ZVecFullTextIndexOptions : ZVecIndexOptions
{
    public string Tokenizer { get; init; } = "standard";
    public IReadOnlyList<string> Filters { get; init; } = ["lowercase"];
    public string ExtraParametersJson { get; init; } = string.Empty;
}

/// <summary>Index settings used only when creating a collection and validated when reopening it.</summary>
public sealed class ZVecCollectionOptions
{
    public IDictionary<string, ZVecIndexOptions> Indexes { get; } = new Dictionary<string, ZVecIndexOptions>(StringComparer.Ordinal);
    public bool ValidateExistingSchema { get; init; } = true;
    public uint? OptimizeThreadCount { get; init; }
}

/// <summary>Base type for search-time vector index parameters.</summary>
public abstract record ZVecVectorQueryOptions
{
    public string? Filter { get; init; }
}

/// <summary>Default boolean operator used for adjacent full-text terms.</summary>
public enum ZVecFullTextOperator { Or, And }

/// <summary>Full-text search request. Exactly one of Match or Query must be provided.</summary>
public sealed record ZVecFullTextQueryOptions
{
    public string? Match { get; init; }
    public string? Query { get; init; }
    public ZVecFullTextOperator DefaultOperator { get; init; }
    public int TopK { get; init; } = 10;
}

/// <summary>HNSW search parameters. Defaults match ZVec 0.6.</summary>
public sealed record ZVecHnswQueryOptions : ZVecVectorQueryOptions
{
    public int Ef { get; init; } = 300;
    public float Radius { get; init; }
    public bool IsLinear { get; init; }
    public bool UseRefiner { get; init; }
}

/// <summary>HNSW-RaBitQ search parameters for the versioned native extension.</summary>
public sealed record ZVecHnswRaBitQQueryOptions : ZVecVectorQueryOptions
{
    public int Ef { get; init; } = 300;
    public float Radius { get; init; }
    public bool IsLinear { get; init; }
    public bool UseRefiner { get; init; }
}

/// <summary>IVF search parameters. Defaults match ZVec 0.6.</summary>
public sealed record ZVecIvfQueryOptions : ZVecVectorQueryOptions
{
    public int ProbeCount { get; init; } = 10;
    public bool UseRefiner { get; init; }
    public float ScaleFactor { get; init; } = 10;
    public float Radius { get; init; }
    public bool IsLinear { get; init; }
}

/// <summary>Flat search parameters. Defaults match ZVec 0.6.</summary>
public sealed record ZVecFlatQueryOptions : ZVecVectorQueryOptions
{
    public bool UseRefiner { get; init; }
    public float ScaleFactor { get; init; } = 10;
    public float Radius { get; init; }
    public bool IsLinear { get; init; }
}

/// <summary>DiskANN search parameters. Defaults match ZVec 0.6.</summary>
public sealed record ZVecDiskAnnQueryOptions : ZVecVectorQueryOptions
{
    public int ListSize { get; init; } = 300;
    public float Radius { get; init; }
    public bool IsLinear { get; init; }
    public bool UseRefiner { get; init; }
}

/// <summary>Completeness reported for one vector index.</summary>
public sealed record ZVecIndexStats(string Name, float Completeness);

/// <summary>Managed snapshot of collection statistics.</summary>
public sealed record ZVecCollectionStats(ulong DocumentCount, IReadOnlyList<ZVecIndexStats> Indexes);
