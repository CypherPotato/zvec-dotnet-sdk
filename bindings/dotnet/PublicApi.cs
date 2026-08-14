using System.Reflection;

namespace OpenIndexer.ZVec;

/// <summary>Distance metric used by a vector index.</summary>
public enum ZVecMetric { L2, InnerProduct, Cosine, MipsL2 }

/// <summary>ZVec native error codes.</summary>
public enum ZVecErrorCode
{
    Ok = 0, NotFound = 1, AlreadyExists = 2, InvalidArgument = 3,
    PermissionDenied = 4, FailedPrecondition = 5, ResourceExhausted = 6,
    Unavailable = 7, InternalError = 8, NotSupported = 9, Unknown = 10
}

/// <summary>An error reported by the native ZVec C API.</summary>
public sealed class ZVecException : Exception
{
    public ZVecException(ZVecErrorCode errorCode, string message) : base(message) => ErrorCode = errorCode;
    public ZVecErrorCode ErrorCode { get; }
}

/// <summary>A native version, platform, architecture, symbol, or effective-index incompatibility.</summary>
public sealed class ZVecCompatibilityException : NotSupportedException
{
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

    public string NativeVersion { get; }
    public string Platform { get; }
    public string Architecture { get; }
    public string NativeLibraryPath { get; }
    public string RequestedFeature { get; }
    public string? EffectiveFeature { get; }
}

/// <summary>Marks a float array as a vector field.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ZVecVectorAttribute : Attribute
{
    public ZVecVectorAttribute() { }
    public ZVecVectorAttribute(int dimensions, ZVecMetric metric = ZVecMetric.Cosine)
    {
        Dimensions = dimensions;
        Metric = metric;
    }

    public int Dimensions { get; set; }
    public ZVecMetric Metric { get; set; } = ZVecMetric.Cosine;
}

/// <summary>Marks a scalar field for an inverted index using ZVec 0.6 defaults unless overridden.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ZVecIndexedAttribute : Attribute
{
    public bool EnableRangeOptimization { get; set; } = true;
    public bool EnableExtendedWildcard { get; set; }
}

/// <summary>Base schema for documents stored in ZVec.</summary>
public abstract class ZVecCollectionSchema
{
    /// <summary>Persistent document identifier.</summary>
    public required string Id { get; init; }
}

/// <summary>A typed nearest-neighbor query result.</summary>
public sealed record ZVecQueryResult(string Id, float Score, ZVecCollectionSchema Data, int QueryIndex);

/// <summary>Example document model.</summary>
public sealed class Article : ZVecCollectionSchema
{
    public required string Title { get; init; }
    public required string Content { get; init; }
    [ZVecIndexed] public string Category { get; init; } = string.Empty;
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
