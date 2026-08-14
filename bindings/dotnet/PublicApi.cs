using System.Reflection;

namespace OpenIndexer.ZVec;

/// <summary>Specifies the distance metric used by a vector index.</summary>
public enum ZVecMetric
{
    /// <summary>Uses squared Euclidean distance.</summary>
    L2,
    /// <summary>Uses inner-product similarity.</summary>
    InnerProduct,
    /// <summary>Uses cosine distance.</summary>
    Cosine,
    /// <summary>Uses maximum inner-product search transformed to L2 distance.</summary>
    MipsL2
}

/// <summary>Identifies an error returned by the native ZVec C API.</summary>
public enum ZVecErrorCode
{
    /// <summary>The operation completed successfully.</summary>
    Ok = 0,
    /// <summary>The requested resource was not found.</summary>
    NotFound = 1,
    /// <summary>The resource already exists.</summary>
    AlreadyExists = 2,
    /// <summary>An argument is invalid.</summary>
    InvalidArgument = 3,
    /// <summary>The operation is not permitted.</summary>
    PermissionDenied = 4,
    /// <summary>A required precondition was not met.</summary>
    FailedPrecondition = 5,
    /// <summary>A required resource was exhausted.</summary>
    ResourceExhausted = 6,
    /// <summary>The native service or resource is unavailable.</summary>
    Unavailable = 7,
    /// <summary>The native backend reported an internal error.</summary>
    InternalError = 8,
    /// <summary>The requested operation is not supported.</summary>
    NotSupported = 9,
    /// <summary>The native error does not map to a known code.</summary>
    Unknown = 10
}

/// <summary>Represents an error reported by the native ZVec C API.</summary>
public sealed class ZVecException : Exception
{
    /// <summary>Initializes a new native ZVec exception.</summary>
    /// <param name="errorCode">The native error code.</param>
    /// <param name="message">The native error message.</param>
    public ZVecException(ZVecErrorCode errorCode, string message) : base(message) => ErrorCode = errorCode;

    /// <summary>Gets the native ZVec error code.</summary>
    public ZVecErrorCode ErrorCode { get; }
}

/// <summary>Represents an incompatible native version, platform, architecture, symbol set, or effective index.</summary>
public sealed class ZVecCompatibilityException : NotSupportedException
{
    /// <summary>Initializes a new native compatibility exception.</summary>
    /// <param name="message">A description of the incompatibility.</param>
    /// <param name="nativeVersion">The loaded native ZVec version.</param>
    /// <param name="platform">The current operating-system description.</param>
    /// <param name="architecture">The current process architecture.</param>
    /// <param name="nativeLibraryPath">The loaded native library path.</param>
    /// <param name="requestedFeature">The requested feature or index type.</param>
    /// <param name="effectiveFeature">The effective native feature or index type, when available.</param>
    public ZVecCompatibilityException(
        string message,
        string nativeVersion,
        string platform,
        string architecture,
        string nativeLibraryPath,
        string requestedFeature,
        string? effectiveFeature = null) : base(message)
    {
        NativeVersion = nativeVersion;
        Platform = platform;
        Architecture = architecture;
        NativeLibraryPath = nativeLibraryPath;
        RequestedFeature = requestedFeature;
        EffectiveFeature = effectiveFeature;
    }

    /// <summary>Gets the loaded native ZVec version.</summary>
    public string NativeVersion { get; }
    /// <summary>Gets the current operating-system description.</summary>
    public string Platform { get; }
    /// <summary>Gets the current process architecture.</summary>
    public string Architecture { get; }
    /// <summary>Gets the loaded native library path.</summary>
    public string NativeLibraryPath { get; }
    /// <summary>Gets the requested feature or index type.</summary>
    public string RequestedFeature { get; }
    /// <summary>Gets the effective native feature or index type, when available.</summary>
    public string? EffectiveFeature { get; }
}

/// <summary>Marks a <see cref="float"/> array property as a vector field.</summary>
/// <remarks><see cref="Dimensions"/> must be positive. Invalid dimensions, metrics, or property types fail when the schema is mapped.</remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ZVecVectorAttribute : Attribute
{
    /// <summary>Initializes a vector field whose dimensions and metric are set through named arguments.</summary>
    public ZVecVectorAttribute() { }

    /// <summary>Initializes a vector field with explicit dimensions and metric.</summary>
    /// <param name="dimensions">The required number of vector elements.</param>
    /// <param name="metric">The distance metric used by the field.</param>
    public ZVecVectorAttribute(int dimensions, ZVecMetric metric = ZVecMetric.Cosine)
    {
        Dimensions = dimensions;
        Metric = metric;
    }

    /// <summary>Gets or sets the required number of vector elements.</summary>
    public int Dimensions { get; set; }
    /// <summary>Gets or sets the distance metric used by the field.</summary>
    public ZVecMetric Metric { get; set; } = ZVecMetric.Cosine;
}

/// <summary>Marks a scalar property for an inverted index using ZVec 0.6 defaults unless overridden.</summary>
/// <remarks>An explicit entry in <see cref="ZVecCollectionOptions.Indexes"/> for the same property takes precedence.</remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ZVecIndexedAttribute : Attribute
{
    /// <summary>Gets or sets whether range-query optimization is enabled.</summary>
    public bool EnableRangeOptimization { get; set; } = true;
    /// <summary>Gets or sets whether extended wildcard matching is enabled.</summary>
    /// <remarks>ZVec 0.6 does not serialize this value in its schema, so it returns to the native default after reopening a collection.</remarks>
    public bool EnableExtendedWildcard { get; set; }
}

/// <summary>Provides the base schema for documents stored in ZVec.</summary>
/// <remarks>Derived schemas require a public parameterless constructor. Their public instance properties become persistent fields. Supported scalar types are <see cref="string"/>, <see cref="bool"/>, <see cref="int"/>, <see cref="long"/>, <see cref="uint"/>, <see cref="ulong"/>, <see cref="float"/>, and <see cref="double"/>; <see cref="float"/> arrays require <see cref="ZVecVectorAttribute"/>.</remarks>
public abstract class ZVecCollectionSchema
{
    /// <summary>Gets the persistent document identifier.</summary>
    public required string Id { get; init; }
}

/// <summary>Represents a typed nearest-neighbor or full-text query result.</summary>
/// <param name="Id">The persistent document identifier.</param>
/// <param name="Score">The score reported by ZVec.</param>
/// <param name="Data">The materialized document.</param>
/// <param name="QueryIndex">The zero-based input-vector index for batched vector queries.</param>
/// <remarks>Vector searches materialize vector properties. Full-text searches omit vector properties from <paramref name="Data"/>.</remarks>
public sealed record ZVecQueryResult(string Id, float Score, ZVecCollectionSchema Data, int QueryIndex);

/// <summary>Provides an example document model for basic usage and package documentation.</summary>
public sealed class Article : ZVecCollectionSchema
{
    /// <summary>Gets the article title.</summary>
    public required string Title { get; init; }
    /// <summary>Gets the article content.</summary>
    public required string Content { get; init; }
    /// <summary>Gets the indexed category.</summary>
    [ZVecIndexed] public string Category { get; init; } = string.Empty;
    /// <summary>Gets the 768-dimensional cosine embedding.</summary>
    [ZVecVector(Dimensions = 768, Metric = ZVecMetric.Cosine)] public float[] Embedding { get; init; } = [];
}

internal sealed record ZVecField(PropertyInfo Property, string Name, string DataType, ZVecVectorAttribute? Vector, ZVecIndexedAttribute? Indexed);

internal static class ZVecSchemaMapper<T> where T : ZVecCollectionSchema
{
    internal static readonly ZVecField[] Fields = typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(x => x.Name != nameof(ZVecCollectionSchema.Id))
        .Select(x => new ZVecField(x, x.Name, GetDataType(x), x.GetCustomAttribute<ZVecVectorAttribute>(), x.GetCustomAttribute<ZVecIndexedAttribute>()))
        .ToArray();

    private static string GetDataType(PropertyInfo property)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type == typeof(float[]))
        {
            if (property.GetCustomAttribute<ZVecVectorAttribute>() is not { Dimensions: > 0 } vector)
                throw new InvalidOperationException($"Float array property '{property.Name}' must have ZVecVectorAttribute with Dimensions greater than zero.");
            if (!Enum.IsDefined(vector.Metric))
                throw new InvalidOperationException($"Vector property '{property.Name}' has an unsupported metric value.");
            return "VECTOR_FP32";
        }
        if (property.GetCustomAttribute<ZVecVectorAttribute>() is not null)
            throw new InvalidOperationException($"Vector property '{property.Name}' must be float[].");
        return type == typeof(string) ? "STRING" :
            type == typeof(bool) ? "BOOL" :
            type == typeof(int) ? "INT32" :
            type == typeof(long) ? "INT64" :
            type == typeof(uint) ? "UINT32" :
            type == typeof(ulong) ? "UINT64" :
            type == typeof(float) ? "FLOAT" :
            type == typeof(double) ? "DOUBLE" :
            throw new NotSupportedException($"Property '{property.Name}' has unsupported type '{property.PropertyType}'.");
    }
}
