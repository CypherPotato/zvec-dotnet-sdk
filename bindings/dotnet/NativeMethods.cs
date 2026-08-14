using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Microsoft.Win32.SafeHandles;

namespace OpenIndexer.ZVec;

internal enum NativeDataType : uint
{
    String = 2,
    Bool = 3,
    Int32 = 4,
    Int64 = 5,
    UInt32 = 6,
    UInt64 = 7,
    Float = 8,
    Double = 9,
    VectorFloat32 = 23
}

internal enum NativeIndexType : uint { Hnsw = 1, Ivf = 2, Flat = 3, HnswRaBitQ = 4, DiskAnn = 5, Invert = 10, FullText = 11 }
internal enum NativeMetric : uint { L2 = 1, InnerProduct = 2, Cosine = 3, MipsL2 = 4 }
internal enum NativeQuantization : uint { None = 0, Float16 = 1, Int8 = 2, Int4 = 3 }

[StructLayout(LayoutKind.Sequential)]
internal struct NativeString
{
    internal IntPtr Data;
    internal nuint Length;
    internal nuint Capacity;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeStringArray
{
    internal IntPtr Strings;
    internal nuint Count;
}

internal sealed class ZVecCollectionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private ZVecCollectionHandle() : base(true) { }
    internal ZVecCollectionHandle(IntPtr handle) : base(true) => SetHandle(handle);
    protected override bool ReleaseHandle() => NativeMethods.CollectionClose(handle) == ZVecErrorCode.Ok;
}

internal static unsafe class NativeMethods
{
    private const string Library = "zvec_c_api";
    private static readonly object InitializeLock = new();
    private const uint RaBitQExtensionApiVersion = 1;
    private const string RaBitQZVecCommit = "ec8a78ee08b14a0b8c94158ffc1de42cd3f97f6d";
    private static bool initialized;
    private static uint? initializedOptimizeThreadCount;
    private static string loadedLibraryPath = "unresolved";

    static NativeMethods() => NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, ResolveLibrary);

    internal static void EnsureInitialized(uint? optimizeThreadCount = null)
    {
        if (initialized)
        {
            if (optimizeThreadCount is not null && optimizeThreadCount != initializedOptimizeThreadCount)
                throw new InvalidOperationException("ZVec OptimizeThreadCount is process-wide and can only be set before the first collection initializes the native runtime.");
            return;
        }
        lock (InitializeLock)
        {
            if (initialized)
            {
                if (optimizeThreadCount is not null && optimizeThreadCount != initializedOptimizeThreadCount)
                    throw new InvalidOperationException("ZVec OptimizeThreadCount is process-wide and can only be set before the first collection initializes the native runtime.");
                return;
            }
            if (GetVersionMajor() != 0 || GetVersionMinor() != 6)
                throw CompatibilityError("ZVec C API 0.6.x", $"The loaded backend version is {NativeVersion}.");
            var config = optimizeThreadCount is null ? IntPtr.Zero : ConfigCreate();
            try
            {
                if (config != IntPtr.Zero) ThrowIfError(ConfigSetOptimizeThreadCount(config, optimizeThreadCount!.Value));
                ThrowIfError(Initialize(config));
                initializedOptimizeThreadCount = optimizeThreadCount;
                initialized = true;
            }
            finally
            {
                if (config != IntPtr.Zero) ConfigDestroy(config);
            }
        }
    }

    internal static void ThrowIfError(ZVecErrorCode code)
    {
        if (code == ZVecErrorCode.Ok) return;
        IntPtr message = IntPtr.Zero;
        var text = code.ToString();
        if (GetLastError(out message) == ZVecErrorCode.Ok && message != IntPtr.Zero)
        {
            try { text = Marshal.PtrToStringUTF8(message) ?? text; }
            finally { Free(message); }
        }
        throw new ZVecException(code, text);
    }

    internal static string NativeVersion => $"{GetVersionMajor()}.{GetVersionMinor()}.{GetVersionPatch()}";

    internal static void EnsureRaBitQSupported()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || !Avx2.IsSupported)
            throw CompatibilityError("HNSW-RaBitQ", "ZVec 0.6 enables HNSW-RaBitQ only on Linux x64 with AVX2 or newer instructions.");
        try
        {
            if (RaBitQExtensionVersion() != RaBitQExtensionApiVersion)
                throw CompatibilityError("HNSW-RaBitQ", $"The native extension API must be {RaBitQExtensionApiVersion}.");
            var commit = Marshal.PtrToStringUTF8(RaBitQExtensionCommit()) ?? "unknown";
            if (!string.Equals(commit, RaBitQZVecCommit, StringComparison.Ordinal))
                throw CompatibilityError("HNSW-RaBitQ", $"The native extension was built from ZVec commit {commit}; expected {RaBitQZVecCommit}.");
            if (!RaBitQIsSupported())
                throw CompatibilityError("HNSW-RaBitQ", "The native library was compiled without RABITQ_SUPPORTED.");
        }
        catch (EntryPointNotFoundException exception)
        {
            throw CompatibilityError("HNSW-RaBitQ", "The loaded zvec_c_api library does not contain the versioned OpenIndexer RaBitQ extension symbols.", innerException: exception);
        }
    }

    internal static ZVecCompatibilityException CompatibilityError(string requested, string reason, NativeIndexType? effective = null, Exception? innerException = null)
    {
        var message = $"{reason} NativeVersion={NativeVersion}; Platform={RuntimeInformation.OSDescription}; Architecture={RuntimeInformation.ProcessArchitecture}; Requested={requested}; Effective={effective?.ToString() ?? "unknown"}; Library={loadedLibraryPath}.";
        var exception = new ZVecCompatibilityException(message, NativeVersion, RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), loadedLibraryPath, requested, effective?.ToString());
        if (innerException is not null) exception.Data[nameof(innerException)] = innerException.Message;
        return exception;
    }

    internal static IntPtr Utf8(string value) => Marshal.StringToCoTaskMemUTF8(value);
    internal static void FreeUtf8(IntPtr value) => Marshal.FreeCoTaskMem(value);

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != Library) return IntPtr.Zero;
        var fileName = OperatingSystem.IsWindows() ? "zvec_c_api.dll" : OperatingSystem.IsMacOS() ? "libzvec_c_api.dylib" : "libzvec_c_api.so";
        var configured = Environment.GetEnvironmentVariable("ZVEC_LIBRARY_PATH");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
            candidates.Add(File.Exists(configured) ? configured : Path.Combine(configured, fileName));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, fileName));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", fileName));
        foreach (var candidate in candidates.Where(File.Exists))
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                loadedLibraryPath = Path.GetFullPath(candidate);
                return handle;
            }
        if (NativeLibrary.TryLoad(fileName, assembly, searchPath, out var defaultHandle))
        {
            loadedLibraryPath = fileName;
            return defaultHandle;
        }
        throw new DllNotFoundException($"Could not load {fileName}. Set ZVEC_LIBRARY_PATH to the ZVec C API 0.6.x library file or directory.");
    }

    [DllImport(Library, EntryPoint = "zvec_get_version_major", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetVersionMajor();
    [DllImport(Library, EntryPoint = "zvec_get_version_minor", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetVersionMinor();
    [DllImport(Library, EntryPoint = "zvec_get_version_patch", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetVersionPatch();
    [DllImport(Library, EntryPoint = "openindexer_zvec_rabitq_extension_api_version", CallingConvention = CallingConvention.Cdecl)] private static extern uint RaBitQExtensionVersion();
    [DllImport(Library, EntryPoint = "openindexer_zvec_rabitq_zvec_commit", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr RaBitQExtensionCommit();
    [DllImport(Library, EntryPoint = "openindexer_zvec_rabitq_is_supported", CallingConvention = CallingConvention.Cdecl)][return: MarshalAs(UnmanagedType.I1)] private static extern bool RaBitQIsSupported();
    [DllImport(Library, EntryPoint = "openindexer_zvec_rabitq_index_params_create", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr RaBitQIndexParamsCreate(NativeMetric metric, int totalBits, int clusterCount, int m, int efConstruction, int sampleCount);
    [DllImport(Library, EntryPoint = "openindexer_zvec_rabitq_index_params_get", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode RaBitQIndexParamsGet(IntPtr parameters, out NativeMetric metric, out int totalBits, out int clusterCount, out int m, out int efConstruction, out int sampleCount);
    [DllImport(Library, EntryPoint = "openindexer_zvec_rabitq_query_params_create", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr RaBitQQueryParamsCreate(int ef, float radius, [MarshalAs(UnmanagedType.I1)] bool isLinear, [MarshalAs(UnmanagedType.I1)] bool useRefiner);
    [DllImport(Library, EntryPoint = "openindexer_zvec_rabitq_query_params_get", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode RaBitQQueryParamsGet(IntPtr parameters, out int ef, out float radius, out byte isLinear, out byte useRefiner);
    [DllImport(Library, EntryPoint = "openindexer_zvec_rabitq_query_params_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void RaBitQQueryParamsDestroy(IntPtr parameters);
    [DllImport(Library, EntryPoint = "openindexer_zvec_vector_query_set_rabitq_params", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode QuerySetRaBitQParams(IntPtr query, IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_config_data_create", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr ConfigCreate();
    [DllImport(Library, EntryPoint = "zvec_config_data_destroy", CallingConvention = CallingConvention.Cdecl)] private static extern void ConfigDestroy(IntPtr config);
    [DllImport(Library, EntryPoint = "zvec_config_data_set_optimize_thread_count", CallingConvention = CallingConvention.Cdecl)] private static extern ZVecErrorCode ConfigSetOptimizeThreadCount(IntPtr config, uint threadCount);
    [DllImport(Library, EntryPoint = "zvec_initialize", CallingConvention = CallingConvention.Cdecl)] private static extern ZVecErrorCode Initialize(IntPtr config);
    [DllImport(Library, EntryPoint = "zvec_get_last_error", CallingConvention = CallingConvention.Cdecl)] private static extern ZVecErrorCode GetLastError(out IntPtr message);
    [DllImport(Library, EntryPoint = "zvec_free", CallingConvention = CallingConvention.Cdecl)] internal static extern void Free(IntPtr pointer);

    [DllImport(Library, EntryPoint = "zvec_index_params_create", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr IndexParamsCreate(NativeIndexType type);
    [DllImport(Library, EntryPoint = "zvec_index_params_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void IndexParamsDestroy(IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_index_params_get_type", CallingConvention = CallingConvention.Cdecl)] internal static extern NativeIndexType IndexParamsGetType(IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_index_params_set_metric_type", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode IndexParamsSetMetric(IntPtr parameters, NativeMetric metric);
    [DllImport(Library, EntryPoint = "zvec_index_params_get_metric_type", CallingConvention = CallingConvention.Cdecl)] internal static extern NativeMetric IndexParamsGetMetric(IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_index_params_set_quantize_type", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode IndexParamsSetQuantization(IntPtr parameters, NativeQuantization quantization);
    [DllImport(Library, EntryPoint = "zvec_index_params_get_quantize_type", CallingConvention = CallingConvention.Cdecl)] internal static extern NativeQuantization IndexParamsGetQuantization(IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_index_params_set_quantizer_enable_rotate", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode IndexParamsSetQuantizerRotation(IntPtr parameters, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [DllImport(Library, EntryPoint = "zvec_index_params_get_quantizer_enable_rotate", CallingConvention = CallingConvention.Cdecl)][return: MarshalAs(UnmanagedType.I1)] internal static extern bool IndexParamsGetQuantizerRotation(IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_index_params_set_hnsw_params", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode IndexParamsSetHnsw(IntPtr parameters, int m, int efConstruction);
    [DllImport(Library, EntryPoint = "zvec_index_params_get_hnsw_m", CallingConvention = CallingConvention.Cdecl)] internal static extern int IndexParamsGetHnswM(IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_index_params_get_hnsw_ef_construction", CallingConvention = CallingConvention.Cdecl)] internal static extern int IndexParamsGetHnswEfConstruction(IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_index_params_set_ivf_params", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode IndexParamsSetIvf(IntPtr parameters, int listCount, int iterationCount, [MarshalAs(UnmanagedType.I1)] bool useSoar);
    [DllImport(Library, EntryPoint = "zvec_index_params_get_ivf_params", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode IndexParamsGetIvf(IntPtr parameters, out int listCount, out int iterationCount, out byte useSoar);
    [DllImport(Library, EntryPoint = "zvec_index_params_set_diskann_params", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode IndexParamsSetDiskAnn(IntPtr parameters, int maxDegree, int listSize, int pqChunkCount);
    [DllImport(Library, EntryPoint = "zvec_index_params_get_diskann_max_degree", CallingConvention = CallingConvention.Cdecl)] internal static extern int IndexParamsGetDiskAnnMaxDegree(IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_index_params_get_diskann_list_size", CallingConvention = CallingConvention.Cdecl)] internal static extern int IndexParamsGetDiskAnnListSize(IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_index_params_get_diskann_pq_chunk_num", CallingConvention = CallingConvention.Cdecl)] internal static extern int IndexParamsGetDiskAnnPqChunkCount(IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_index_params_set_invert_params", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode IndexParamsSetInvert(IntPtr parameters, [MarshalAs(UnmanagedType.I1)] bool range, [MarshalAs(UnmanagedType.I1)] bool wildcard);
    [DllImport(Library, EntryPoint = "zvec_index_params_get_invert_params", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode IndexParamsGetInvert(IntPtr parameters, out byte range, out byte wildcard);
    [DllImport(Library, EntryPoint = "zvec_index_params_set_fts_params", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode IndexParamsSetFullText(IntPtr parameters, IntPtr tokenizer, IntPtr filters, IntPtr extraParameters);
    [DllImport(Library, EntryPoint = "zvec_index_params_get_fts_params", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode IndexParamsGetFullText(IntPtr parameters, out IntPtr tokenizer, out IntPtr filters, out IntPtr extraParameters);
    [DllImport(Library, EntryPoint = "zvec_string_array_create", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr StringArrayCreate(nuint count);
    [DllImport(Library, EntryPoint = "zvec_string_array_add", CallingConvention = CallingConvention.Cdecl)] internal static extern void StringArrayAdd(IntPtr array, nuint index, IntPtr value);
    [DllImport(Library, EntryPoint = "zvec_string_array_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void StringArrayDestroy(IntPtr array);

    [DllImport(Library, EntryPoint = "zvec_field_schema_create", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FieldSchemaCreate(IntPtr name, NativeDataType type, [MarshalAs(UnmanagedType.I1)] bool nullable, uint dimension);
    [DllImport(Library, EntryPoint = "zvec_field_schema_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void FieldSchemaDestroy(IntPtr schema);
    [DllImport(Library, EntryPoint = "zvec_field_schema_set_index_params", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode FieldSchemaSetIndex(IntPtr schema, IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_collection_schema_create", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr CollectionSchemaCreate(IntPtr name);
    [DllImport(Library, EntryPoint = "zvec_collection_schema_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void CollectionSchemaDestroy(IntPtr schema);
    [DllImport(Library, EntryPoint = "zvec_collection_schema_add_field", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode CollectionSchemaAddField(IntPtr schema, IntPtr field);
    [DllImport(Library, EntryPoint = "zvec_collection_schema_get_field", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr CollectionSchemaGetField(IntPtr schema, IntPtr fieldName);
    [DllImport(Library, EntryPoint = "zvec_field_schema_get_dimension", CallingConvention = CallingConvention.Cdecl)] internal static extern uint FieldSchemaGetDimension(IntPtr schema);
    [DllImport(Library, EntryPoint = "zvec_field_schema_get_index_params", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FieldSchemaGetIndexParams(IntPtr schema);

    [DllImport(Library, EntryPoint = "zvec_collection_create_and_open", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode CollectionCreateAndOpen(IntPtr path, IntPtr schema, IntPtr options, out IntPtr collection);
    [DllImport(Library, EntryPoint = "zvec_collection_open", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode CollectionOpen(IntPtr path, IntPtr options, out IntPtr collection);
    [DllImport(Library, EntryPoint = "zvec_collection_close", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode CollectionClose(IntPtr collection);
    [DllImport(Library, EntryPoint = "zvec_collection_get_schema", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode CollectionGetSchema(ZVecCollectionHandle collection, out IntPtr schema);
    [DllImport(Library, EntryPoint = "zvec_collection_optimize", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode CollectionOptimize(ZVecCollectionHandle collection);
    [DllImport(Library, EntryPoint = "zvec_collection_upsert", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode CollectionUpsert(ZVecCollectionHandle collection, IntPtr* documents, nuint count, out nuint successCount, out nuint errorCount);
    [DllImport(Library, EntryPoint = "zvec_collection_delete", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode CollectionDelete(ZVecCollectionHandle collection, IntPtr* keys, nuint count, out nuint successCount, out nuint errorCount);
    [DllImport(Library, EntryPoint = "zvec_collection_fetch", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode CollectionFetch(ZVecCollectionHandle collection, IntPtr* keys, nuint keyCount, IntPtr outputFields, nuint outputFieldCount, [MarshalAs(UnmanagedType.I1)] bool includeVector, out IntPtr documents, out nuint foundCount);
    [DllImport(Library, EntryPoint = "zvec_collection_query", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode CollectionQuery(ZVecCollectionHandle collection, IntPtr query, out IntPtr documents, out nuint count);
    [DllImport(Library, EntryPoint = "zvec_docs_free", CallingConvention = CallingConvention.Cdecl)] internal static extern void DocsFree(IntPtr documents, nuint count);

    internal static ZVecErrorCode CollectionUpsertOne(ZVecCollectionHandle collection, IntPtr document, out nuint successCount, out nuint errorCount)
    {
        var documents = stackalloc IntPtr[1];
        documents[0] = document;
        return CollectionUpsert(collection, documents, 1, out successCount, out errorCount);
    }

    internal static ZVecErrorCode CollectionDeleteOne(ZVecCollectionHandle collection, IntPtr key, out nuint successCount, out nuint errorCount)
    {
        var keys = stackalloc IntPtr[1];
        keys[0] = key;
        return CollectionDelete(collection, keys, 1, out successCount, out errorCount);
    }

    internal static ZVecErrorCode CollectionFetchOne(ZVecCollectionHandle collection, IntPtr key, out IntPtr documents, out nuint foundCount)
    {
        var keys = stackalloc IntPtr[1];
        keys[0] = key;
        return CollectionFetch(collection, keys, 1, IntPtr.Zero, 0, true, out documents, out foundCount);
    }
    [DllImport(Library, EntryPoint = "zvec_collection_get_stats", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode CollectionGetStats(ZVecCollectionHandle collection, out IntPtr stats);
    [DllImport(Library, EntryPoint = "zvec_collection_stats_get_doc_count", CallingConvention = CallingConvention.Cdecl)] internal static extern ulong StatsGetDocCount(IntPtr stats);
    [DllImport(Library, EntryPoint = "zvec_collection_stats_get_index_count", CallingConvention = CallingConvention.Cdecl)] internal static extern nuint StatsGetIndexCount(IntPtr stats);
    [DllImport(Library, EntryPoint = "zvec_collection_stats_get_index_name", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr StatsGetIndexName(IntPtr stats, nuint index);
    [DllImport(Library, EntryPoint = "zvec_collection_stats_get_index_completeness", CallingConvention = CallingConvention.Cdecl)] internal static extern float StatsGetIndexCompleteness(IntPtr stats, nuint index);
    [DllImport(Library, EntryPoint = "zvec_collection_stats_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void StatsDestroy(IntPtr stats);

    [DllImport(Library, EntryPoint = "zvec_doc_create", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr DocCreate();
    [DllImport(Library, EntryPoint = "zvec_doc_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void DocDestroy(IntPtr document);
    [DllImport(Library, EntryPoint = "zvec_doc_set_pk", CallingConvention = CallingConvention.Cdecl)] internal static extern void DocSetPrimaryKey(IntPtr document, IntPtr key);
    [DllImport(Library, EntryPoint = "zvec_doc_get_pk_pointer", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr DocGetPrimaryKey(IntPtr document);
    [DllImport(Library, EntryPoint = "zvec_doc_get_score", CallingConvention = CallingConvention.Cdecl)] internal static extern float DocGetScore(IntPtr document);
    [DllImport(Library, EntryPoint = "zvec_doc_add_field_by_value", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode DocAddField(IntPtr document, IntPtr name, NativeDataType type, void* value, nuint size);
    [DllImport(Library, EntryPoint = "zvec_doc_get_field_value_pointer", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode DocGetField(IntPtr document, IntPtr name, NativeDataType type, out IntPtr value, out nuint size);

    [DllImport(Library, EntryPoint = "zvec_vector_query_create", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr QueryCreate();
    [DllImport(Library, EntryPoint = "zvec_vector_query_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void QueryDestroy(IntPtr query);
    [DllImport(Library, EntryPoint = "zvec_vector_query_set_topk", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode QuerySetTopK(IntPtr query, int topK);
    [DllImport(Library, EntryPoint = "zvec_vector_query_set_field_name", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode QuerySetFieldName(IntPtr query, IntPtr fieldName);
    [DllImport(Library, EntryPoint = "zvec_vector_query_set_query_vector", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode QuerySetVector(IntPtr query, void* data, nuint size);
    [DllImport(Library, EntryPoint = "zvec_vector_query_set_include_vector", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode QuerySetIncludeVector(IntPtr query, [MarshalAs(UnmanagedType.I1)] bool include);
    [DllImport(Library, EntryPoint = "zvec_vector_query_set_filter", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode QuerySetFilter(IntPtr query, IntPtr filter);
    [DllImport(Library, EntryPoint = "zvec_vector_query_set_output_fields", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode QuerySetOutputFields(IntPtr query, IntPtr* fields, nuint count);
    [DllImport(Library, EntryPoint = "zvec_fts_create", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FullTextCreate();
    [DllImport(Library, EntryPoint = "zvec_fts_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void FullTextDestroy(IntPtr fullText);
    [DllImport(Library, EntryPoint = "zvec_fts_set_query_string", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode FullTextSetQuery(IntPtr fullText, IntPtr query);
    [DllImport(Library, EntryPoint = "zvec_fts_set_match_string", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode FullTextSetMatch(IntPtr fullText, IntPtr match);
    [DllImport(Library, EntryPoint = "zvec_vector_query_set_fts", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode QuerySetFullText(IntPtr query, IntPtr fullText);
    [DllImport(Library, EntryPoint = "zvec_query_params_fts_create", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FullTextQueryParamsCreate(IntPtr defaultOperator);
    [DllImport(Library, EntryPoint = "zvec_query_params_fts_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void FullTextQueryParamsDestroy(IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_vector_query_set_fts_params", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode QuerySetFullTextParams(IntPtr query, IntPtr parameters);

    [DllImport(Library, EntryPoint = "zvec_query_params_hnsw_create", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr HnswQueryParamsCreate(int ef, float radius, [MarshalAs(UnmanagedType.I1)] bool isLinear, [MarshalAs(UnmanagedType.I1)] bool useRefiner);
    [DllImport(Library, EntryPoint = "zvec_query_params_hnsw_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void HnswQueryParamsDestroy(IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_vector_query_set_hnsw_params", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode QuerySetHnswParams(IntPtr query, IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_query_params_ivf_create", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr IvfQueryParamsCreate(int probeCount, [MarshalAs(UnmanagedType.I1)] bool useRefiner, float scaleFactor);
    [DllImport(Library, EntryPoint = "zvec_query_params_ivf_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void IvfQueryParamsDestroy(IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_query_params_ivf_set_radius", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode IvfQueryParamsSetRadius(IntPtr parameters, float radius);
    [DllImport(Library, EntryPoint = "zvec_query_params_ivf_set_is_linear", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode IvfQueryParamsSetIsLinear(IntPtr parameters, [MarshalAs(UnmanagedType.I1)] bool isLinear);
    [DllImport(Library, EntryPoint = "zvec_vector_query_set_ivf_params", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode QuerySetIvfParams(IntPtr query, IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_query_params_flat_create", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FlatQueryParamsCreate([MarshalAs(UnmanagedType.I1)] bool useRefiner, float scaleFactor);
    [DllImport(Library, EntryPoint = "zvec_query_params_flat_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void FlatQueryParamsDestroy(IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_query_params_flat_set_radius", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode FlatQueryParamsSetRadius(IntPtr parameters, float radius);
    [DllImport(Library, EntryPoint = "zvec_query_params_flat_set_is_linear", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode FlatQueryParamsSetIsLinear(IntPtr parameters, [MarshalAs(UnmanagedType.I1)] bool isLinear);
    [DllImport(Library, EntryPoint = "zvec_vector_query_set_flat_params", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode QuerySetFlatParams(IntPtr query, IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_query_params_diskann_create", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr DiskAnnQueryParamsCreate(int listSize);
    [DllImport(Library, EntryPoint = "zvec_query_params_diskann_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void DiskAnnQueryParamsDestroy(IntPtr parameters);
    [DllImport(Library, EntryPoint = "zvec_query_params_diskann_set_radius", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode DiskAnnQueryParamsSetRadius(IntPtr parameters, float radius);
    [DllImport(Library, EntryPoint = "zvec_query_params_diskann_set_is_linear", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode DiskAnnQueryParamsSetIsLinear(IntPtr parameters, [MarshalAs(UnmanagedType.I1)] bool isLinear);
    [DllImport(Library, EntryPoint = "zvec_query_params_diskann_set_is_using_refiner", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode DiskAnnQueryParamsSetUseRefiner(IntPtr parameters, [MarshalAs(UnmanagedType.I1)] bool useRefiner);
    [DllImport(Library, EntryPoint = "zvec_vector_query_set_diskann_params", CallingConvention = CallingConvention.Cdecl)] internal static extern ZVecErrorCode QuerySetDiskAnnParams(IntPtr query, IntPtr parameters);
}
