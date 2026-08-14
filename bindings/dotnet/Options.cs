namespace OpenIndexer.ZVec;

/// <summary>Specifies the quantization applied while building a vector index.</summary>
public enum ZVecQuantization
{
    /// <summary>Stores vectors without quantization.</summary>
    None,
    /// <summary>Stores quantized 16-bit floating-point values.</summary>
    Float16,
    /// <summary>Stores quantized 8-bit integer values.</summary>
    Int8,
    /// <summary>Stores quantized 4-bit integer values.</summary>
    Int4
}

/// <summary>Provides the base type for index settings persisted in a collection schema.</summary>
public abstract record ZVecIndexOptions;

/// <summary>Provides common settings for vector indexes.</summary>
public abstract record ZVecVectorIndexOptions : ZVecIndexOptions
{
    /// <summary>Gets the distance metric used by the index.</summary>
    public ZVecMetric Metric { get; init; } = ZVecMetric.Cosine;
    /// <summary>Gets the quantization applied when the index is built.</summary>
    public ZVecQuantization Quantization { get; init; }
    /// <summary>Gets whether quantizer rotation is enabled for supported INT8 and INT4 indexes.</summary>
    public bool EnableQuantizerRotation { get; init; }
}

/// <summary>Configures an exact flat vector index.</summary>
public sealed record ZVecFlatIndexOptions : ZVecVectorIndexOptions;

/// <summary>Configures an HNSW index using ZVec 0.6 defaults.</summary>
public sealed record ZVecHnswIndexOptions : ZVecVectorIndexOptions
{
    /// <summary>Gets the maximum number of graph connections per node.</summary>
    public int M { get; init; } = 50;
    /// <summary>Gets the candidate-list size used while constructing the graph.</summary>
    public int EfConstruction { get; init; } = 500;
}

/// <summary>Configures an HNSW-RaBitQ index for the ZVec 0.6 Linux x64 AVX2 backend.</summary>
/// <remarks>HNSW-RaBitQ uses its own quantization and does not accept <see cref="ZVecVectorIndexOptions.Quantization"/> or <see cref="ZVecVectorIndexOptions.EnableQuantizerRotation"/>.</remarks>
public sealed record ZVecHnswRaBitQIndexOptions : ZVecVectorIndexOptions
{
    /// <summary>Gets the number of quantization bits. Valid values are 1 through 9.</summary>
    public int TotalBits { get; init; } = 7;
    /// <summary>Gets the number of quantization clusters. The value must be positive.</summary>
    public int ClusterCount { get; init; } = 16;
    /// <summary>Gets the maximum number of graph connections per node. Valid values are 5 through 1024.</summary>
    public int M { get; init; } = 50;
    /// <summary>Gets the candidate-list size used while constructing the graph. Valid values are 1 through 2048.</summary>
    public int EfConstruction { get; init; } = 500;
    /// <summary>Gets the number of vectors sampled for training, or <c>0</c> to use all vectors.</summary>
    public int SampleCount { get; init; }
}

/// <summary>Configures an IVF index using ZVec 0.6 defaults.</summary>
public sealed record ZVecIvfIndexOptions : ZVecVectorIndexOptions
{
    /// <summary>Gets the number of inverted lists.</summary>
    public int ListCount { get; init; } = 1024;
    /// <summary>Gets the number of clustering iterations used during index construction.</summary>
    public int IterationCount { get; init; } = 10;
    /// <summary>Gets whether spill-over-aware redundancy is enabled.</summary>
    public bool UseSoar { get; init; }
}

/// <summary>Configures a DiskANN index using ZVec 0.6 defaults.</summary>
public sealed record ZVecDiskAnnIndexOptions : ZVecVectorIndexOptions
{
    /// <summary>Gets the maximum graph degree.</summary>
    public int MaxDegree { get; init; } = 100;
    /// <summary>Gets the candidate-list size used while constructing the index.</summary>
    public int ListSize { get; init; } = 50;
    /// <summary>Gets the number of product-quantization chunks, or <c>0</c> to use the native default.</summary>
    public int PqChunkCount { get; init; }
}

/// <summary>Configures a scalar inverted index.</summary>
public sealed record ZVecInvertedIndexOptions : ZVecIndexOptions
{
    /// <summary>Gets whether range-query optimization is enabled.</summary>
    public bool EnableRangeOptimization { get; init; } = true;
    /// <summary>Gets whether extended wildcard matching is enabled.</summary>
    /// <remarks>ZVec 0.6 does not serialize this value in its schema, so it returns to the native default after reopening a collection.</remarks>
    public bool EnableExtendedWildcard { get; init; }
}

/// <summary>Configures a full-text index.</summary>
public sealed record ZVecFullTextIndexOptions : ZVecIndexOptions
{
    /// <summary>Gets the native tokenizer name.</summary>
    public string Tokenizer { get; init; } = "standard";
    /// <summary>Gets the ordered native token-filter names.</summary>
    public IReadOnlyList<string> Filters { get; init; } = ["lowercase"];
    /// <summary>Gets additional native full-text parameters encoded as JSON, or an empty string when none are supplied.</summary>
    public string ExtraParametersJson { get; init; } = string.Empty;
}

/// <summary>Configures collection creation, reopening validation, and native optimization.</summary>
public sealed class ZVecCollectionOptions
{
    /// <summary>Gets the index settings keyed by schema property name.</summary>
    /// <remarks>Settings are applied during creation and treated as expectations during reopening; changing them does not migrate an existing collection. Without an explicit entry, vector properties use HNSW, properties marked with <see cref="ZVecIndexedAttribute"/> use an inverted index, and other scalar properties remain unindexed.</remarks>
    public IDictionary<string, ZVecIndexOptions> Indexes { get; } = new Dictionary<string, ZVecIndexOptions>(StringComparer.Ordinal);
    /// <summary>Gets whether requested field presence, vector dimensions, and configured index parameters are validated when an existing collection is opened.</summary>
    /// <remarks>Validation does not migrate indexes, reject extra persisted fields, or compare scalar field data types.</remarks>
    public bool ValidateExistingSchema { get; init; } = true;
    /// <summary>Gets the global native optimization thread count, or <see langword="null"/> to keep the ZVec default.</summary>
    /// <remarks>The process's first ZVec initialization fixes this global value. Later attempts to use a different value fail.</remarks>
    public uint? OptimizeThreadCount { get; init; }
}

/// <summary>Provides common search-time vector index parameters.</summary>
public abstract record ZVecVectorQueryOptions
{
    /// <summary>Gets a native ZVec scalar filter expression, or <see langword="null"/> when no filter is applied.</summary>
    public string? Filter { get; init; }
}

/// <summary>Specifies the default boolean operator for adjacent full-text terms.</summary>
public enum ZVecFullTextOperator
{
    /// <summary>Matches documents containing any adjacent term.</summary>
    Or,
    /// <summary>Matches documents containing every adjacent term.</summary>
    And
}

/// <summary>Describes a full-text search request. Exactly one of <see cref="Match"/> or <see cref="Query"/> must be provided.</summary>
public sealed record ZVecFullTextQueryOptions
{
    /// <summary>Gets natural text to match, or <see langword="null"/> when a structured query is used.</summary>
    public string? Match { get; init; }
    /// <summary>Gets a structured native full-text query, or <see langword="null"/> when natural matching is used.</summary>
    public string? Query { get; init; }
    /// <summary>Gets the operator applied between adjacent terms.</summary>
    public ZVecFullTextOperator DefaultOperator { get; init; }
    /// <summary>Gets the maximum number of results. The value must be positive.</summary>
    public int TopK { get; init; } = 10;
}

/// <summary>Configures HNSW search using ZVec 0.6 defaults.</summary>
public sealed record ZVecHnswQueryOptions : ZVecVectorQueryOptions
{
    /// <summary>Gets the search candidate-list size.</summary>
    public int Ef { get; init; } = 300;
    /// <summary>Gets the native distance radius, or <c>0</c> to disable radius filtering.</summary>
    public float Radius { get; init; }
    /// <summary>Gets whether the native backend performs a linear scan.</summary>
    public bool IsLinear { get; init; }
    /// <summary>Gets whether the native backend refines approximate candidates.</summary>
    public bool UseRefiner { get; init; }
}

/// <summary>Configures HNSW-RaBitQ search through the versioned native extension.</summary>
public sealed record ZVecHnswRaBitQQueryOptions : ZVecVectorQueryOptions
{
    /// <summary>Gets the search candidate-list size. Valid values are 1 through 2048.</summary>
    public int Ef { get; init; } = 300;
    /// <summary>Gets the native distance radius, or <c>0</c> to disable radius filtering.</summary>
    public float Radius { get; init; }
    /// <summary>Gets whether the native backend performs a linear scan.</summary>
    public bool IsLinear { get; init; }
    /// <summary>Gets whether the native backend refines approximate candidates.</summary>
    public bool UseRefiner { get; init; }
}

/// <summary>Configures IVF search using ZVec 0.6 defaults.</summary>
public sealed record ZVecIvfQueryOptions : ZVecVectorQueryOptions
{
    /// <summary>Gets the number of inverted lists probed by the search.</summary>
    public int ProbeCount { get; init; } = 10;
    /// <summary>Gets whether the native backend refines approximate candidates.</summary>
    public bool UseRefiner { get; init; }
    /// <summary>Gets the native candidate-expansion factor used for refinement.</summary>
    public float ScaleFactor { get; init; } = 10;
    /// <summary>Gets the native distance radius, or <c>0</c> to disable radius filtering.</summary>
    public float Radius { get; init; }
    /// <summary>Gets whether the native backend performs a linear scan.</summary>
    public bool IsLinear { get; init; }
}

/// <summary>Configures flat search using ZVec 0.6 defaults.</summary>
public sealed record ZVecFlatQueryOptions : ZVecVectorQueryOptions
{
    /// <summary>Gets whether the native backend refines candidates.</summary>
    public bool UseRefiner { get; init; }
    /// <summary>Gets the native candidate-expansion factor used for refinement.</summary>
    public float ScaleFactor { get; init; } = 10;
    /// <summary>Gets the native distance radius, or <c>0</c> to disable radius filtering.</summary>
    public float Radius { get; init; }
    /// <summary>Gets whether the native backend performs a linear scan.</summary>
    public bool IsLinear { get; init; }
}

/// <summary>Configures DiskANN search using ZVec 0.6 defaults.</summary>
public sealed record ZVecDiskAnnQueryOptions : ZVecVectorQueryOptions
{
    /// <summary>Gets the candidate-list size used by the search.</summary>
    public int ListSize { get; init; } = 300;
    /// <summary>Gets the native distance radius, or <c>0</c> to disable radius filtering.</summary>
    public float Radius { get; init; }
    /// <summary>Gets whether the native backend performs a linear scan.</summary>
    public bool IsLinear { get; init; }
    /// <summary>Gets whether the native backend refines approximate candidates.</summary>
    public bool UseRefiner { get; init; }
}

/// <summary>Reports build completeness for one vector index.</summary>
/// <param name="Name">The indexed schema property name.</param>
/// <param name="Completeness">The completeness value reported by ZVec.</param>
public sealed record ZVecIndexStats(string Name, float Completeness);

/// <summary>Provides a managed snapshot of collection statistics.</summary>
/// <param name="DocumentCount">The number of documents in the collection.</param>
/// <param name="Indexes">Completeness statistics for vector indexes.</param>
public sealed record ZVecCollectionStats(ulong DocumentCount, IReadOnlyList<ZVecIndexStats> Indexes);
