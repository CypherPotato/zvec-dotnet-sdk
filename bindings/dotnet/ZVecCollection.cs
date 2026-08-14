using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenIndexer.ZVec;

/// <summary>Provides a typed persistent collection backed by the native ZVec C API.</summary>
/// <typeparam name="TSchema">The document schema stored in the collection.</typeparam>
/// <remarks>Operations on one instance are serialized. Native calls are synchronous and cannot be interrupted after they start. <typeparamref name="TSchema"/> must satisfy the materialization requirements documented by <see cref="ZVecCollectionSchema"/>.</remarks>
/// <example>
/// <code>
/// await using var collection = await ZVecCollection&lt;Article&gt;.CreateOrOpenAsync("./articles");
/// await collection.UpsertAsync(article);
/// var results = await collection.QueryAsync(nameof(Article.Embedding), embedding, topK: 10);
/// </code>
/// </example>
public sealed class ZVecCollection<TSchema> : IDisposable, IAsyncDisposable where TSchema : ZVecCollectionSchema
{
    private readonly ZVecCollectionHandle handle;
    private readonly ZVecCollectionOptions options;
    private readonly SemaphoreSlim gate = new(1, 1);
    private int disposed;

    private ZVecCollection(ZVecCollectionHandle handle, ZVecCollectionOptions options)
    {
        this.handle = handle;
        this.options = options;
    }

    /// <summary>Opens an existing collection or creates one from <typeparamref name="TSchema"/> metadata.</summary>
    /// <param name="path">The collection directory.</param>
    /// <param name="cancellation">A token observed before and while scheduling the native operation.</param>
    /// <returns>A task containing the opened collection.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or consists only of white-space characters.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> is canceled before the native operation completes.</exception>
    /// <exception cref="ZVecException">The native backend cannot create or open the collection.</exception>
    public static Task<ZVecCollection<TSchema>> CreateOrOpenAsync(string path, CancellationToken cancellation = default) =>
        CreateOrOpenAsync(path, new ZVecCollectionOptions(), cancellation);

    /// <summary>Opens or creates a collection using explicit persisted index settings.</summary>
    /// <param name="path">The collection directory.</param>
    /// <param name="options">Creation settings and reopening-validation behavior.</param>
    /// <param name="cancellation">A token observed before and while scheduling the native operation.</param>
    /// <returns>A task containing the opened collection.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> or an option is invalid.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> is canceled before the native operation completes.</exception>
    /// <exception cref="ZVecCompatibilityException">A requested feature is incompatible with the loaded native runtime.</exception>
    /// <exception cref="ZVecException">The native backend cannot create, open, or validate the collection.</exception>
    public static Task<ZVecCollection<TSchema>> CreateOrOpenAsync(string path, ZVecCollectionOptions options, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);
        ValidateCollectionOptions(options);
        return Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            NativeMethods.EnsureInitialized(options.OptimizeThreadCount);
            var fullPath = Path.GetFullPath(path);
            var nativePath = NativeMethods.Utf8(fullPath);
            try
            {
                if (Directory.Exists(fullPath))
                {
                    NativeMethods.ThrowIfError(NativeMethods.CollectionOpen(nativePath, IntPtr.Zero, out var existing));
                    var collection = new ZVecCollection<TSchema>(new ZVecCollectionHandle(existing), options);
                    try
                    {
                        if (options.ValidateExistingSchema) collection.ValidateExistingSchema();
                        return collection;
                    }
                    catch
                    {
                        collection.Dispose();
                        throw;
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                var schema = CreateNativeSchema(options);
                try
                {
                    var createCode = NativeMethods.CollectionCreateAndOpen(nativePath, schema, IntPtr.Zero, out var created);
                    if (createCode == ZVecErrorCode.AlreadyExists)
                    {
                        NativeMethods.ThrowIfError(NativeMethods.CollectionOpen(nativePath, IntPtr.Zero, out created));
                    }
                    else NativeMethods.ThrowIfError(createCode);
                    return new ZVecCollection<TSchema>(new ZVecCollectionHandle(created), options);
                }
                finally { NativeMethods.CollectionSchemaDestroy(schema); }
            }
            finally { NativeMethods.FreeUtf8(nativePath); }
        }, cancellation);
    }

    /// <summary>Inserts or replaces a document by its <see cref="ZVecCollectionSchema.Id"/>.</summary>
    /// <param name="data">The document to store.</param>
    /// <param name="cancellation">A token observed before and while scheduling the native operation.</param>
    /// <returns>A value task representing the operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The identifier or a vector value is invalid.</exception>
    /// <exception cref="ObjectDisposedException">The collection has been disposed.</exception>
    /// <exception cref="ZVecException">The native backend rejects the document.</exception>
    public ValueTask UpsertAsync(TSchema data, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ValidateDocument(data);
        return new ValueTask(ExecuteAsync(() =>
        {
            var document = CreateNativeDocument(data);
            try
            {
                NativeMethods.ThrowIfError(NativeMethods.CollectionUpsertOne(handle, document, out var success, out var errors));
                if (success != 1 || errors != 0)
                    throw new ZVecException(ZVecErrorCode.InternalError, $"ZVec upsert completed with {success} success and {errors} errors.");
            }
            finally { NativeMethods.DocDestroy(document); }
        }, cancellation));
    }

    /// <summary>Deletes a document by identifier and succeeds when it does not exist.</summary>
    /// <param name="id">The persistent document identifier.</param>
    /// <param name="cancellation">A token observed before and while scheduling the native operation.</param>
    /// <returns>A value task representing the operation.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or consists only of white-space characters.</exception>
    /// <exception cref="ObjectDisposedException">The collection has been disposed.</exception>
    /// <exception cref="ZVecException">The native backend cannot fetch or delete the document.</exception>
    public ValueTask DeleteAsync(string id, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return new ValueTask(ExecuteAsync(() =>
        {
            var nativeId = NativeMethods.Utf8(id);
            IntPtr existing = IntPtr.Zero;
            try
            {
                NativeMethods.ThrowIfError(NativeMethods.CollectionFetchOne(handle, nativeId, out existing, out var found));
                if (existing != IntPtr.Zero) NativeMethods.DocsFree(existing, found);
                existing = IntPtr.Zero;
                if (found == 0) return;

                NativeMethods.ThrowIfError(NativeMethods.CollectionDeleteOne(handle, nativeId, out var success, out var errors));
                if (success != 1 || errors != 0)
                    throw new ZVecException(ZVecErrorCode.InternalError, $"ZVec delete completed with {success} success and {errors} errors.");
            }
            finally
            {
                if (existing != IntPtr.Zero) NativeMethods.DocsFree(existing, 1);
                NativeMethods.FreeUtf8(nativeId);
            }
        }, cancellation));
    }

    /// <summary>Gets one document, or <see langword="null"/> when the identifier does not exist.</summary>
    /// <param name="id">The persistent document identifier.</param>
    /// <param name="cancellation">A token observed before and while scheduling the native operation.</param>
    /// <returns>A value task containing the document, or <see langword="null"/> when it was not found.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or consists only of white-space characters.</exception>
    /// <exception cref="ObjectDisposedException">The collection has been disposed.</exception>
    /// <exception cref="ZVecException">The native backend cannot fetch the document.</exception>
    public ValueTask<TSchema?> GetAsync(string id, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return new ValueTask<TSchema?>(ExecuteAsync(() =>
        {
            var nativeId = NativeMethods.Utf8(id);
            IntPtr documents = IntPtr.Zero;
            nuint count = 0;
            try
            {
                NativeMethods.ThrowIfError(NativeMethods.CollectionFetchOne(handle, nativeId, out documents, out count));
                return count == 0 ? null : ReadDocument(Marshal.ReadIntPtr(documents));
            }
            finally
            {
                if (documents != IntPtr.Zero) NativeMethods.DocsFree(documents, count);
                NativeMethods.FreeUtf8(nativeId);
            }
        }, cancellation));
    }

    /// <summary>Enumerates a materialized snapshot of the documents present when enumeration starts.</summary>
    /// <param name="cancellation">A token observed before the native query and between yielded documents.</param>
    /// <returns>An asynchronous sequence of documents.</returns>
    /// <remarks>The native query materializes all documents in managed memory before the first item is yielded.</remarks>
    /// <exception cref="ObjectDisposedException">The collection has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> is canceled before materialization or between yielded documents.</exception>
    /// <exception cref="NotSupportedException">The collection contains more than <see cref="int.MaxValue"/> documents.</exception>
    /// <exception cref="ZVecException">The native backend cannot list the documents.</exception>
    public async IAsyncEnumerable<TSchema> ListAsync([EnumeratorCancellation] CancellationToken cancellation = default)
    {
        var documents = await ExecuteAsync(ReadAllDocuments, cancellation).ConfigureAwait(false);
        foreach (var document in documents)
        {
            cancellation.ThrowIfCancellationRequested();
            yield return document;
        }
    }

    /// <summary>Executes a top-K nearest-neighbor search using the persisted index defaults.</summary>
    /// <param name="fieldName">The vector property name.</param>
    /// <param name="vector">The query vector, whose length must match the field dimensions.</param>
    /// <param name="topK">The positive maximum number of results.</param>
    /// <param name="cancellation">A token observed before and while scheduling the native operation.</param>
    /// <returns>A task containing the ranked results.</returns>
    /// <exception cref="ArgumentException">The field, vector, or result count is invalid.</exception>
    /// <exception cref="ObjectDisposedException">The collection has been disposed.</exception>
    /// <exception cref="ZVecException">The native backend cannot execute the query.</exception>
    public Task<IList<ZVecQueryResult>> QueryAsync(string fieldName, ReadOnlyMemory<float> vector, int topK, CancellationToken cancellation = default) =>
        QueryAsync(fieldName, vector, topK, queryOptions: null, cancellation);

    /// <summary>Executes a top-K nearest-neighbor search with explicit search-time index settings.</summary>
    /// <param name="fieldName">The vector property name.</param>
    /// <param name="vector">The query vector, whose length must match the field dimensions.</param>
    /// <param name="topK">The positive maximum number of results.</param>
    /// <param name="queryOptions">Search parameters matching the persisted index type, or <see langword="null"/> for native defaults.</param>
    /// <param name="cancellation">A token observed before and while scheduling the native operation.</param>
    /// <returns>A task containing the ranked results.</returns>
    /// <exception cref="ArgumentException">The field, vector, result count, or query-option type is invalid.</exception>
    /// <exception cref="ZVecCompatibilityException">RaBitQ query support is incompatible with the loaded native runtime.</exception>
    /// <exception cref="ObjectDisposedException">The collection has been disposed.</exception>
    /// <exception cref="ZVecException">The native backend cannot execute the query.</exception>
    public Task<IList<ZVecQueryResult>> QueryAsync(string fieldName, ReadOnlyMemory<float> vector, int topK, ZVecVectorQueryOptions? queryOptions, CancellationToken cancellation = default)
    {
        ValidateQuery(fieldName, vector, topK, queryOptions);
        return ExecuteAsync<IList<ZVecQueryResult>>(() => Query(fieldName, vector.Span, topK, 0, queryOptions), cancellation);
    }

    /// <summary>Executes a full-text search over a field configured with <see cref="ZVecFullTextIndexOptions"/>.</summary>
    /// <param name="fieldName">The full-text indexed property name.</param>
    /// <param name="queryOptions">The full-text request. Exactly one of match text or structured query must be supplied.</param>
    /// <param name="cancellation">A token observed before and while scheduling the native operation.</param>
    /// <returns>A task containing ranked documents without vector-property values.</returns>
    /// <remarks>Vector properties are omitted from materialized full-text results.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="queryOptions"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The field or full-text request is invalid.</exception>
    /// <exception cref="ObjectDisposedException">The collection has been disposed.</exception>
    /// <exception cref="ZVecException">The native backend cannot execute the query.</exception>
    public Task<IList<ZVecQueryResult>> FullTextQueryAsync(string fieldName, ZVecFullTextQueryOptions queryOptions, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentNullException.ThrowIfNull(queryOptions);
        if (queryOptions.TopK <= 0) throw new ArgumentOutOfRangeException(nameof(queryOptions.TopK));
        if (string.IsNullOrWhiteSpace(queryOptions.Match) == string.IsNullOrWhiteSpace(queryOptions.Query))
            throw new ArgumentException("Exactly one of Match or Query must be provided.", nameof(queryOptions));
        var field = ZVecSchemaMapper<TSchema>.Fields.SingleOrDefault(x => x.Name == fieldName)
            ?? throw new ArgumentException($"Field '{fieldName}' does not exist in {typeof(TSchema).Name}.", nameof(fieldName));
        if (GetIndexOptions(field, options) is not ZVecFullTextIndexOptions)
            throw new ArgumentException($"Field '{fieldName}' is not configured with a full-text index.", nameof(fieldName));
        return ExecuteAsync<IList<ZVecQueryResult>>(() => FullTextQuery(fieldName, queryOptions), cancellation);
    }

    /// <summary>Runs ZVec's synchronous index rebuild and segment merge on a worker thread.</summary>
    /// <param name="cancellation">A token observed before the native operation starts.</param>
    /// <returns>A value task representing the operation.</returns>
    /// <remarks>The native operation cannot be canceled after it starts and does not expose progress.</remarks>
    /// <exception cref="ObjectDisposedException">The collection has been disposed.</exception>
    /// <exception cref="ZVecException">The native backend cannot optimize the collection.</exception>
    public ValueTask OptimizeAsync(CancellationToken cancellation = default) =>
        new(ExecuteAsync(() => NativeMethods.ThrowIfError(NativeMethods.CollectionOptimize(handle)), cancellation));

    /// <summary>Returns a managed snapshot of document count and vector-index completeness.</summary>
    /// <param name="cancellation">A token observed before and while scheduling the native operation.</param>
    /// <returns>A task containing the statistics snapshot.</returns>
    /// <exception cref="ObjectDisposedException">The collection has been disposed.</exception>
    /// <exception cref="ZVecException">The native backend cannot read collection statistics.</exception>
    public Task<ZVecCollectionStats> GetStatsAsync(CancellationToken cancellation = default) =>
        ExecuteAsync(ReadStats, cancellation);

    /// <summary>Executes independent top-K searches and flattens their results with <see cref="ZVecQueryResult.QueryIndex"/>.</summary>
    /// <param name="fieldName">The vector property name.</param>
    /// <param name="vectors">One or more query vectors whose lengths match the field dimensions.</param>
    /// <param name="topK">The positive maximum number of results per vector.</param>
    /// <param name="cancellation">A token observed before each native query.</param>
    /// <returns>A task containing flattened results in input-vector order.</returns>
    /// <remarks>Queries run sequentially and use persisted index defaults; this overload is not a native batch operation.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="vectors"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">No vectors are supplied, or a field, vector, or result count is invalid.</exception>
    /// <exception cref="ObjectDisposedException">The collection has been disposed.</exception>
    /// <exception cref="ZVecException">The native backend cannot execute a query.</exception>
    public Task<IList<ZVecQueryResult>> QueryAsync(string fieldName, IReadOnlyList<ReadOnlyMemory<float>> vectors, int topK, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        if (vectors.Count == 0) throw new ArgumentException("At least one query vector is required.", nameof(vectors));
        foreach (var vector in vectors) ValidateQuery(fieldName, vector, topK);
        return ExecuteAsync<IList<ZVecQueryResult>>(() =>
        {
            var results = new List<ZVecQueryResult>(checked(vectors.Count * topK));
            for (var index = 0; index < vectors.Count; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                results.AddRange(Query(fieldName, vectors[index].Span, topK, index, queryOptions: null));
            }
            return results;
        }, cancellation);
    }

    /// <summary>Releases the native collection handle after any active operation completes.</summary>
    /// <remarks>This method is idempotent. Subsequent operations throw <see cref="ObjectDisposedException"/>.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        gate.Wait();
        try { handle.Dispose(); }
        finally { gate.Release(); gate.Dispose(); }
    }

    /// <summary>Asynchronously waits for any active operation and releases the native collection handle.</summary>
    /// <returns>A value task representing asynchronous disposal.</returns>
    /// <remarks>This method is idempotent. Handle release is synchronous; asynchronous waiting applies to the collection's serialization gate.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await gate.WaitAsync().ConfigureAwait(false);
        try { handle.Dispose(); }
        finally { gate.Release(); gate.Dispose(); }
    }

    private async Task ExecuteAsync(Action operation, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            await Task.Run(operation, cancellation).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<TResult> ExecuteAsync<TResult>(Func<TResult> operation, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            return await Task.Run(operation, cancellation).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private static IntPtr CreateNativeSchema(ZVecCollectionOptions options)
    {
        _ = ZVecSchemaMapper<TSchema>.Fields;
        var name = NativeMethods.Utf8(typeof(TSchema).Name);
        var schema = NativeMethods.CollectionSchemaCreate(name);
        NativeMethods.FreeUtf8(name);
        if (schema == IntPtr.Zero) throw new ZVecException(ZVecErrorCode.InternalError, "Could not create the native collection schema.");
        try
        {
            foreach (var field in ZVecSchemaMapper<TSchema>.Fields) AddNativeField(schema, field, GetIndexOptions(field, options));
            return schema;
        }
        catch { NativeMethods.CollectionSchemaDestroy(schema); throw; }
    }

    private static void AddNativeField(IntPtr schema, ZVecField field, ZVecIndexOptions? indexOptions)
    {
        var name = NativeMethods.Utf8(field.Name);
        var nativeField = NativeMethods.FieldSchemaCreate(name, GetNativeType(field), IsNullable(field.Property), checked((uint)(field.Vector?.Dimensions ?? 0)));
        NativeMethods.FreeUtf8(name);
        if (nativeField == IntPtr.Zero) throw new ZVecException(ZVecErrorCode.InvalidArgument, $"Could not create field schema '{field.Name}'.");
        IntPtr index = IntPtr.Zero;
        try
        {
            if (indexOptions is not null) index = CreateIndexParams(field, indexOptions);
            if (index != IntPtr.Zero)
            {
                NativeMethods.ThrowIfError(NativeMethods.FieldSchemaSetIndex(nativeField, index));
                if (indexOptions is ZVecHnswRaBitQIndexOptions)
                {
                    var cloned = NativeMethods.FieldSchemaGetIndexParams(nativeField);
                    var actualType = cloned == IntPtr.Zero ? NativeIndexType.Flat : NativeMethods.IndexParamsGetType(cloned);
                    if (actualType != NativeIndexType.HnswRaBitQ)
                        throw NativeMethods.CompatibilityError("HNSW-RaBitQ", "The field schema clone did not preserve the requested index type.", actualType);
                }
            }
            NativeMethods.ThrowIfError(NativeMethods.CollectionSchemaAddField(schema, nativeField));
        }
        finally
        {
            if (index != IntPtr.Zero) NativeMethods.IndexParamsDestroy(index);
            NativeMethods.FieldSchemaDestroy(nativeField);
        }
    }

    private static unsafe IntPtr CreateNativeDocument(TSchema data)
    {
        var document = NativeMethods.DocCreate();
        if (document == IntPtr.Zero) throw new ZVecException(ZVecErrorCode.InternalError, "Could not create a native document.");
        try
        {
            var id = NativeMethods.Utf8(data.Id);
            try { NativeMethods.DocSetPrimaryKey(document, id); }
            finally { NativeMethods.FreeUtf8(id); }
            foreach (var field in ZVecSchemaMapper<TSchema>.Fields) AddField(document, field, field.Property.GetValue(data));
            return document;
        }
        catch { NativeMethods.DocDestroy(document); throw; }
    }

    private static unsafe void AddField(IntPtr document, ZVecField field, object? value)
    {
        if (value is null) return;
        var name = NativeMethods.Utf8(field.Name);
        try
        {
            if (value is string text)
            {
                var nativeText = NativeMethods.Utf8(text);
                try { NativeMethods.ThrowIfError(NativeMethods.DocAddField(document, name, NativeDataType.String, (void*)nativeText, (nuint)Encoding.UTF8.GetByteCount(text))); }
                finally { NativeMethods.FreeUtf8(nativeText); }
                return;
            }
            if (value is float[] vector)
            {
                fixed (float* pointer = vector)
                    NativeMethods.ThrowIfError(NativeMethods.DocAddField(document, name, NativeDataType.VectorFloat32, pointer, checked((nuint)(vector.Length * sizeof(float)))));
                return;
            }
            var type = GetNativeType(field);
            if (value is bool boolean) { byte item = boolean ? (byte)1 : (byte)0; NativeMethods.ThrowIfError(NativeMethods.DocAddField(document, name, type, &item, 1)); }
            else if (value is int int32) NativeMethods.ThrowIfError(NativeMethods.DocAddField(document, name, type, &int32, sizeof(int)));
            else if (value is long int64) NativeMethods.ThrowIfError(NativeMethods.DocAddField(document, name, type, &int64, sizeof(long)));
            else if (value is uint uint32) NativeMethods.ThrowIfError(NativeMethods.DocAddField(document, name, type, &uint32, sizeof(uint)));
            else if (value is ulong uint64) NativeMethods.ThrowIfError(NativeMethods.DocAddField(document, name, type, &uint64, sizeof(ulong)));
            else if (value is float single) NativeMethods.ThrowIfError(NativeMethods.DocAddField(document, name, type, &single, sizeof(float)));
            else if (value is double number) NativeMethods.ThrowIfError(NativeMethods.DocAddField(document, name, type, &number, sizeof(double)));
        }
        finally { NativeMethods.FreeUtf8(name); }
    }

    private static TSchema ReadDocument(IntPtr document, bool includeVectors = true)
    {
        var instance = Activator.CreateInstance<TSchema>() ?? throw new InvalidOperationException($"{typeof(TSchema)} must have a parameterless constructor.");
        typeof(ZVecCollectionSchema).GetProperty(nameof(ZVecCollectionSchema.Id))!.SetValue(instance, Marshal.PtrToStringUTF8(NativeMethods.DocGetPrimaryKey(document)) ?? string.Empty);
        foreach (var field in ZVecSchemaMapper<TSchema>.Fields.Where(x => includeVectors || x.Vector is null))
        {
            var name = NativeMethods.Utf8(field.Name);
            try
            {
                var code = NativeMethods.DocGetField(document, name, GetNativeType(field), out var value, out var size);
                if (code == ZVecErrorCode.NotFound) continue;
                NativeMethods.ThrowIfError(code);
                field.Property.SetValue(instance, ReadValue(field, value, size));
            }
            finally { NativeMethods.FreeUtf8(name); }
        }
        return instance;
    }

    private static unsafe object ReadValue(ZVecField field, IntPtr value, nuint size)
    {
        var type = Nullable.GetUnderlyingType(field.Property.PropertyType) ?? field.Property.PropertyType;
        if (type == typeof(string)) return Marshal.PtrToStringUTF8(value, checked((int)size)) ?? string.Empty;
        if (type == typeof(float[]))
        {
            var result = new float[checked((int)size / sizeof(float))];
            Marshal.Copy(value, result, 0, result.Length);
            return result;
        }
        if (type == typeof(bool)) return Marshal.ReadByte(value) != 0;
        if (type == typeof(int)) return Marshal.ReadInt32(value);
        if (type == typeof(long)) return Marshal.ReadInt64(value);
        if (type == typeof(uint)) return *(uint*)value;
        if (type == typeof(ulong)) return *(ulong*)value;
        if (type == typeof(float)) return *(float*)value;
        if (type == typeof(double)) return *(double*)value;
        throw new NotSupportedException($"Unsupported field type {type}.");
    }

    private List<TSchema> ReadAllDocuments()
    {
        NativeMethods.ThrowIfError(NativeMethods.CollectionGetStats(handle, out var stats));
        ulong count;
        try { count = NativeMethods.StatsGetDocCount(stats); }
        finally { NativeMethods.StatsDestroy(stats); }
        if (count == 0) return [];
        if (count > int.MaxValue) throw new NotSupportedException("ListAsync cannot materialize more than Int32.MaxValue documents in one ZVec query.");
        var query = NativeMethods.QueryCreate();
        if (query == IntPtr.Zero) throw new ZVecException(ZVecErrorCode.InternalError, "Could not create a native list query.");
        IntPtr documents = IntPtr.Zero;
        nuint returned = 0;
        try
        {
            NativeMethods.ThrowIfError(NativeMethods.QuerySetTopK(query, (int)count));
            NativeMethods.ThrowIfError(NativeMethods.QuerySetIncludeVector(query, true));
            NativeMethods.ThrowIfError(NativeMethods.CollectionQuery(handle, query, out documents, out returned));
            var result = new List<TSchema>(checked((int)returned));
            for (nuint index = 0; index < returned; index++) result.Add(ReadDocument(Marshal.ReadIntPtr(documents, checked((int)index * IntPtr.Size))));
            return result;
        }
        finally
        {
            if (documents != IntPtr.Zero) NativeMethods.DocsFree(documents, returned);
            NativeMethods.QueryDestroy(query);
        }
    }

    private unsafe List<ZVecQueryResult> Query(string fieldName, ReadOnlySpan<float> vector, int topK, int queryIndex, ZVecVectorQueryOptions? queryOptions)
    {
        var query = NativeMethods.QueryCreate();
        if (query == IntPtr.Zero) throw new ZVecException(ZVecErrorCode.InternalError, "Could not create a native vector query.");
        IntPtr documents = IntPtr.Zero;
        nuint count = 0;
        var name = NativeMethods.Utf8(fieldName);
        try
        {
            NativeMethods.ThrowIfError(NativeMethods.QuerySetTopK(query, topK));
            NativeMethods.ThrowIfError(NativeMethods.QuerySetFieldName(query, name));
            NativeMethods.ThrowIfError(NativeMethods.QuerySetIncludeVector(query, true));
            if (!string.IsNullOrWhiteSpace(queryOptions?.Filter))
            {
                var filter = NativeMethods.Utf8(queryOptions.Filter);
                try { NativeMethods.ThrowIfError(NativeMethods.QuerySetFilter(query, filter)); }
                finally { NativeMethods.FreeUtf8(filter); }
            }
            AttachQueryOptions(query, queryOptions);
            fixed (float* pointer = vector)
                NativeMethods.ThrowIfError(NativeMethods.QuerySetVector(query, pointer, checked((nuint)(vector.Length * sizeof(float)))));
            NativeMethods.ThrowIfError(NativeMethods.CollectionQuery(handle, query, out documents, out count));
            var result = new List<ZVecQueryResult>(checked((int)count));
            for (nuint index = 0; index < count; index++)
            {
                var nativeDocument = Marshal.ReadIntPtr(documents, checked((int)index * IntPtr.Size));
                var data = ReadDocument(nativeDocument);
                result.Add(new ZVecQueryResult(data.Id, NativeMethods.DocGetScore(nativeDocument), data, queryIndex));
            }
            return result;
        }
        finally
        {
            if (documents != IntPtr.Zero) NativeMethods.DocsFree(documents, count);
            NativeMethods.FreeUtf8(name);
            NativeMethods.QueryDestroy(query);
        }
    }

    private List<ZVecQueryResult> FullTextQuery(string fieldName, ZVecFullTextQueryOptions options)
    {
        var query = NativeMethods.QueryCreate();
        var fullText = NativeMethods.FullTextCreate();
        if (query == IntPtr.Zero || fullText == IntPtr.Zero)
        {
            if (query != IntPtr.Zero) NativeMethods.QueryDestroy(query);
            if (fullText != IntPtr.Zero) NativeMethods.FullTextDestroy(fullText);
            throw new ZVecException(ZVecErrorCode.InternalError, "Could not create a native full-text query.");
        }
        IntPtr documents = IntPtr.Zero;
        nuint count = 0;
        IntPtr parameters = IntPtr.Zero;
        try
        {
            NativeMethods.ThrowIfError(NativeMethods.QuerySetTopK(query, options.TopK));
            var field = NativeMethods.Utf8(fieldName);
            try { NativeMethods.ThrowIfError(NativeMethods.QuerySetFieldName(query, field)); }
            finally { NativeMethods.FreeUtf8(field); }
            NativeMethods.ThrowIfError(NativeMethods.QuerySetIncludeVector(query, false));
            SetScalarOutputFields(query);
            var text = NativeMethods.Utf8(options.Match ?? options.Query!);
            try
            {
                NativeMethods.ThrowIfError(options.Match is not null
                    ? NativeMethods.FullTextSetMatch(fullText, text)
                    : NativeMethods.FullTextSetQuery(fullText, text));
            }
            finally { NativeMethods.FreeUtf8(text); }
            NativeMethods.ThrowIfError(NativeMethods.QuerySetFullText(query, fullText));
            var op = NativeMethods.Utf8(options.DefaultOperator == ZVecFullTextOperator.And ? "AND" : "OR");
            try { parameters = NativeMethods.FullTextQueryParamsCreate(op); }
            finally { NativeMethods.FreeUtf8(op); }
            if (parameters == IntPtr.Zero) throw new ZVecException(ZVecErrorCode.InternalError, "Could not create full-text query parameters.");
            NativeMethods.ThrowIfError(NativeMethods.QuerySetFullTextParams(query, parameters));
            parameters = IntPtr.Zero;
            NativeMethods.ThrowIfError(NativeMethods.CollectionQuery(handle, query, out documents, out count));
            var result = new List<ZVecQueryResult>(checked((int)count));
            for (nuint index = 0; index < count; index++)
            {
                var nativeDocument = Marshal.ReadIntPtr(documents, checked((int)index * IntPtr.Size));
                var data = ReadDocument(nativeDocument, includeVectors: false);
                result.Add(new ZVecQueryResult(data.Id, NativeMethods.DocGetScore(nativeDocument), data, 0));
            }
            return result;
        }
        finally
        {
            if (parameters != IntPtr.Zero) NativeMethods.FullTextQueryParamsDestroy(parameters);
            if (documents != IntPtr.Zero) NativeMethods.DocsFree(documents, count);
            NativeMethods.FullTextDestroy(fullText);
            NativeMethods.QueryDestroy(query);
        }
    }

    private static unsafe void SetScalarOutputFields(IntPtr query)
    {
        var fields = ZVecSchemaMapper<TSchema>.Fields.Where(x => x.Vector is null).Select(x => NativeMethods.Utf8(x.Name)).ToArray();
        try
        {
            fixed (IntPtr* pointer = fields)
                NativeMethods.ThrowIfError(NativeMethods.QuerySetOutputFields(query, pointer, (nuint)fields.Length));
        }
        finally
        {
            foreach (var field in fields) NativeMethods.FreeUtf8(field);
        }
    }

    private static ZVecIndexOptions? GetIndexOptions(ZVecField field, ZVecCollectionOptions options)
    {
        if (options.Indexes.TryGetValue(field.Name, out var configured)) return configured;
        if (field.Vector is { } vector) return new ZVecHnswIndexOptions { Metric = vector.Metric };
        return field.Indexed is { } indexed ? new ZVecInvertedIndexOptions
        {
            EnableRangeOptimization = indexed.EnableRangeOptimization,
            EnableExtendedWildcard = indexed.EnableExtendedWildcard
        } : null;
    }

    private static IntPtr CreateIndexParams(ZVecField field, ZVecIndexOptions options)
    {
        var nativeType = options switch
        {
            ZVecHnswIndexOptions => NativeIndexType.Hnsw,
            ZVecIvfIndexOptions => NativeIndexType.Ivf,
            ZVecFlatIndexOptions => NativeIndexType.Flat,
            ZVecHnswRaBitQIndexOptions => NativeIndexType.HnswRaBitQ,
            ZVecDiskAnnIndexOptions => NativeIndexType.DiskAnn,
            ZVecInvertedIndexOptions => NativeIndexType.Invert,
            ZVecFullTextIndexOptions => NativeIndexType.FullText,
            _ => throw new NotSupportedException($"Unsupported index option type '{options.GetType().Name}'.")
        };
        if (options is ZVecHnswRaBitQIndexOptions) NativeMethods.EnsureRaBitQSupported();
        var parameters = options is ZVecHnswRaBitQIndexOptions rabitq
            ? NativeMethods.RaBitQIndexParamsCreate(ToNativeMetric(rabitq.Metric), rabitq.TotalBits, rabitq.ClusterCount, rabitq.M, rabitq.EfConstruction, rabitq.SampleCount)
            : NativeMethods.IndexParamsCreate(nativeType);
        if (parameters == IntPtr.Zero)
            throw options is ZVecHnswRaBitQIndexOptions
                ? NativeMethods.CompatibilityError("HNSW-RaBitQ", $"The native extension could not create parameters for field '{field.Name}'.")
                : new ZVecException(ZVecErrorCode.InternalError, $"Could not create {nativeType} parameters for '{field.Name}'.");
        try
        {
            var actualType = NativeMethods.IndexParamsGetType(parameters);
            if (actualType != nativeType)
                throw NativeMethods.CompatibilityError(nativeType.ToString(), "The native parameter factory returned a different effective index type.", actualType);
            if (options is ZVecVectorIndexOptions vector && options is not ZVecHnswRaBitQIndexOptions)
            {
                NativeMethods.ThrowIfError(NativeMethods.IndexParamsSetMetric(parameters, ToNativeMetric(vector.Metric)));
                NativeMethods.ThrowIfError(NativeMethods.IndexParamsSetQuantization(parameters, ToNativeQuantization(vector.Quantization)));
                NativeMethods.ThrowIfError(NativeMethods.IndexParamsSetQuantizerRotation(parameters, vector.EnableQuantizerRotation));
            }
            switch (options)
            {
                case ZVecHnswIndexOptions hnsw:
                    NativeMethods.ThrowIfError(NativeMethods.IndexParamsSetHnsw(parameters, hnsw.M, hnsw.EfConstruction));
                    break;
                case ZVecIvfIndexOptions ivf:
                    NativeMethods.ThrowIfError(NativeMethods.IndexParamsSetIvf(parameters, ivf.ListCount, ivf.IterationCount, ivf.UseSoar));
                    break;
                case ZVecDiskAnnIndexOptions diskAnn:
                    NativeMethods.ThrowIfError(NativeMethods.IndexParamsSetDiskAnn(parameters, diskAnn.MaxDegree, diskAnn.ListSize, diskAnn.PqChunkCount));
                    break;
                case ZVecInvertedIndexOptions inverted:
                    NativeMethods.ThrowIfError(NativeMethods.IndexParamsSetInvert(parameters, inverted.EnableRangeOptimization, inverted.EnableExtendedWildcard));
                    break;
                case ZVecFullTextIndexOptions fullText:
                    SetFullTextParams(parameters, fullText);
                    break;
            }
            return parameters;
        }
        catch
        {
            NativeMethods.IndexParamsDestroy(parameters);
            throw;
        }
    }

    private static void SetFullTextParams(IntPtr parameters, ZVecFullTextIndexOptions options)
    {
        var tokenizer = NativeMethods.Utf8(options.Tokenizer);
        var extra = NativeMethods.Utf8(options.ExtraParametersJson);
        var filters = NativeMethods.StringArrayCreate((nuint)options.Filters.Count);
        if (filters == IntPtr.Zero) throw new ZVecException(ZVecErrorCode.InternalError, "Could not create the full-text filter array.");
        try
        {
            for (var index = 0; index < options.Filters.Count; index++)
            {
                var value = NativeMethods.Utf8(options.Filters[index]);
                try { NativeMethods.StringArrayAdd(filters, (nuint)index, value); }
                finally { NativeMethods.FreeUtf8(value); }
            }
            NativeMethods.ThrowIfError(NativeMethods.IndexParamsSetFullText(parameters, tokenizer, filters, extra));
        }
        finally
        {
            NativeMethods.StringArrayDestroy(filters);
            NativeMethods.FreeUtf8(tokenizer);
            NativeMethods.FreeUtf8(extra);
        }
    }

    private void ValidateExistingSchema()
    {
        NativeMethods.ThrowIfError(NativeMethods.CollectionGetSchema(handle, out var schema));
        try
        {
            foreach (var field in ZVecSchemaMapper<TSchema>.Fields)
            {
                var name = NativeMethods.Utf8(field.Name);
                try
                {
                    var nativeField = NativeMethods.CollectionSchemaGetField(schema, name);
                    if (nativeField == IntPtr.Zero) throw SchemaMismatch(field.Name, "field is missing");
                    if (field.Vector is { } vector && NativeMethods.FieldSchemaGetDimension(nativeField) != vector.Dimensions)
                        throw SchemaMismatch(field.Name, "vector dimensions differ");
                    ValidateExistingIndex(field, GetIndexOptions(field, options), NativeMethods.FieldSchemaGetIndexParams(nativeField));
                }
                finally { NativeMethods.FreeUtf8(name); }
            }
        }
        finally { NativeMethods.CollectionSchemaDestroy(schema); }
    }

    private static void ValidateExistingIndex(ZVecField field, ZVecIndexOptions? expected, IntPtr actual)
    {
        if (expected is null)
        {
            if (actual != IntPtr.Zero) throw SchemaMismatch(field.Name, "an unexpected index exists");
            return;
        }
        if (actual == IntPtr.Zero) throw SchemaMismatch(field.Name, "the configured index is missing");
        var expectedType = expected switch
        {
            ZVecHnswIndexOptions => NativeIndexType.Hnsw,
            ZVecIvfIndexOptions => NativeIndexType.Ivf,
            ZVecFlatIndexOptions => NativeIndexType.Flat,
            ZVecHnswRaBitQIndexOptions => NativeIndexType.HnswRaBitQ,
            ZVecDiskAnnIndexOptions => NativeIndexType.DiskAnn,
            ZVecInvertedIndexOptions => NativeIndexType.Invert,
            ZVecFullTextIndexOptions => NativeIndexType.FullText,
            _ => throw new NotSupportedException()
        };
        var actualType = NativeMethods.IndexParamsGetType(actual);
        if (actualType != expectedType)
        {
            if (expected is ZVecHnswRaBitQIndexOptions)
                throw NativeMethods.CompatibilityError("HNSW-RaBitQ", $"Existing schema field '{field.Name}' has a different effective index type.", actualType);
            throw SchemaMismatch(field.Name, "index type differs");
        }
        if (expected is ZVecVectorIndexOptions vector && expected is not ZVecHnswRaBitQIndexOptions &&
            (NativeMethods.IndexParamsGetMetric(actual) != ToNativeMetric(vector.Metric) ||
             NativeMethods.IndexParamsGetQuantization(actual) != ToNativeQuantization(vector.Quantization) ||
             NativeMethods.IndexParamsGetQuantizerRotation(actual) != vector.EnableQuantizerRotation))
            throw SchemaMismatch(field.Name, "metric or quantization differs");
        var matches = expected switch
        {
            ZVecHnswIndexOptions value => NativeMethods.IndexParamsGetHnswM(actual) == value.M && NativeMethods.IndexParamsGetHnswEfConstruction(actual) == value.EfConstruction,
            ZVecHnswRaBitQIndexOptions value => RaBitQMatches(actual, value),
            ZVecIvfIndexOptions value => IvfMatches(actual, value),
            ZVecDiskAnnIndexOptions value => NativeMethods.IndexParamsGetDiskAnnMaxDegree(actual) == value.MaxDegree && NativeMethods.IndexParamsGetDiskAnnListSize(actual) == value.ListSize && NativeMethods.IndexParamsGetDiskAnnPqChunkCount(actual) == value.PqChunkCount,
            ZVecInvertedIndexOptions value => InvertedMatches(actual, value),
            ZVecFullTextIndexOptions value => FullTextMatches(actual, value),
            _ => true
        };
        if (!matches) throw SchemaMismatch(field.Name, "index construction parameters differ");
    }

    private static bool RaBitQMatches(IntPtr actual, ZVecHnswRaBitQIndexOptions expected)
    {
        NativeMethods.EnsureRaBitQSupported();
        NativeMethods.ThrowIfError(NativeMethods.RaBitQIndexParamsGet(actual, out var metric, out var totalBits, out var clusters, out var m, out var efConstruction, out var sampleCount));
        return metric == ToNativeMetric(expected.Metric) && totalBits == expected.TotalBits && clusters == expected.ClusterCount &&
            m == expected.M && efConstruction == expected.EfConstruction && sampleCount == expected.SampleCount;
    }

    private static bool IvfMatches(IntPtr actual, ZVecIvfIndexOptions expected)
    {
        NativeMethods.ThrowIfError(NativeMethods.IndexParamsGetIvf(actual, out var lists, out var iterations, out var soar));
        return lists == expected.ListCount && iterations == expected.IterationCount && (soar != 0) == expected.UseSoar;
    }

    private static bool InvertedMatches(IntPtr actual, ZVecInvertedIndexOptions expected)
    {
        NativeMethods.ThrowIfError(NativeMethods.IndexParamsGetInvert(actual, out var range, out _));
        return (range != 0) == expected.EnableRangeOptimization;
    }

    private static bool FullTextMatches(IntPtr actual, ZVecFullTextIndexOptions expected)
    {
        NativeMethods.ThrowIfError(NativeMethods.IndexParamsGetFullText(actual, out var tokenizer, out var filters, out var extra));
        try
        {
            var nativeFilters = Marshal.PtrToStructure<NativeStringArray>(filters);
            if (nativeFilters.Count != (nuint)expected.Filters.Count) return false;
            for (nuint index = 0; index < nativeFilters.Count; index++)
            {
                var value = Marshal.PtrToStructure<NativeString>(nativeFilters.Strings + checked((int)index * Marshal.SizeOf<NativeString>()));
                if (Marshal.PtrToStringUTF8(value.Data, checked((int)value.Length)) != expected.Filters[(int)index]) return false;
            }
            return Marshal.PtrToStringUTF8(tokenizer) == expected.Tokenizer && Marshal.PtrToStringUTF8(extra) == expected.ExtraParametersJson;
        }
        finally
        {
            if (filters != IntPtr.Zero) NativeMethods.StringArrayDestroy(filters);
        }
    }

    private static InvalidOperationException SchemaMismatch(string field, string reason) =>
        new($"Existing ZVec schema for field '{field}' does not match the requested configuration: {reason}. Changing managed options does not migrate an existing index; recreate or migrate the collection explicitly.");

    private static void AttachQueryOptions(IntPtr query, ZVecVectorQueryOptions? options)
    {
        if (options is null) return;
        IntPtr parameters = IntPtr.Zero;
        Action<IntPtr>? destroy = null;
        try
        {
            ZVecErrorCode code;
            switch (options)
            {
                case ZVecHnswQueryOptions hnsw:
                    parameters = NativeMethods.HnswQueryParamsCreate(hnsw.Ef, hnsw.Radius, hnsw.IsLinear, hnsw.UseRefiner);
                    destroy = NativeMethods.HnswQueryParamsDestroy;
                    code = NativeMethods.QuerySetHnswParams(query, parameters);
                    break;
                case ZVecIvfQueryOptions ivf:
                    parameters = NativeMethods.IvfQueryParamsCreate(ivf.ProbeCount, ivf.UseRefiner, ivf.ScaleFactor);
                    destroy = NativeMethods.IvfQueryParamsDestroy;
                    NativeMethods.ThrowIfError(NativeMethods.IvfQueryParamsSetRadius(parameters, ivf.Radius));
                    NativeMethods.ThrowIfError(NativeMethods.IvfQueryParamsSetIsLinear(parameters, ivf.IsLinear));
                    code = NativeMethods.QuerySetIvfParams(query, parameters);
                    break;
                case ZVecFlatQueryOptions flat:
                    parameters = NativeMethods.FlatQueryParamsCreate(flat.UseRefiner, flat.ScaleFactor);
                    destroy = NativeMethods.FlatQueryParamsDestroy;
                    NativeMethods.ThrowIfError(NativeMethods.FlatQueryParamsSetRadius(parameters, flat.Radius));
                    NativeMethods.ThrowIfError(NativeMethods.FlatQueryParamsSetIsLinear(parameters, flat.IsLinear));
                    code = NativeMethods.QuerySetFlatParams(query, parameters);
                    break;
                case ZVecDiskAnnQueryOptions diskAnn:
                    parameters = NativeMethods.DiskAnnQueryParamsCreate(diskAnn.ListSize);
                    destroy = NativeMethods.DiskAnnQueryParamsDestroy;
                    NativeMethods.ThrowIfError(NativeMethods.DiskAnnQueryParamsSetRadius(parameters, diskAnn.Radius));
                    NativeMethods.ThrowIfError(NativeMethods.DiskAnnQueryParamsSetIsLinear(parameters, diskAnn.IsLinear));
                    NativeMethods.ThrowIfError(NativeMethods.DiskAnnQueryParamsSetUseRefiner(parameters, diskAnn.UseRefiner));
                    code = NativeMethods.QuerySetDiskAnnParams(query, parameters);
                    break;
                case ZVecHnswRaBitQQueryOptions rabitq:
                    NativeMethods.EnsureRaBitQSupported();
                    parameters = NativeMethods.RaBitQQueryParamsCreate(rabitq.Ef, rabitq.Radius, rabitq.IsLinear, rabitq.UseRefiner);
                    if (parameters == IntPtr.Zero) throw NativeMethods.CompatibilityError("HNSW-RaBitQ query", "The native extension could not create query parameters.");
                    destroy = NativeMethods.RaBitQQueryParamsDestroy;
                    NativeMethods.ThrowIfError(NativeMethods.RaBitQQueryParamsGet(parameters, out var actualEf, out var actualRadius, out var actualLinear, out var actualRefiner));
                    if (actualEf != rabitq.Ef || actualRadius != rabitq.Radius || (actualLinear != 0) != rabitq.IsLinear || (actualRefiner != 0) != rabitq.UseRefiner)
                        throw NativeMethods.CompatibilityError("HNSW-RaBitQ query", "The native query parameters did not round-trip with the requested values.");
                    code = NativeMethods.QuerySetRaBitQParams(query, parameters);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported query option type '{options.GetType().Name}'.");
            }
            NativeMethods.ThrowIfError(code);
            parameters = IntPtr.Zero;
        }
        finally
        {
            if (parameters != IntPtr.Zero) destroy!(parameters);
        }
    }

    private ZVecCollectionStats ReadStats()
    {
        NativeMethods.ThrowIfError(NativeMethods.CollectionGetStats(handle, out var stats));
        try
        {
            var indexes = new List<ZVecIndexStats>(checked((int)NativeMethods.StatsGetIndexCount(stats)));
            for (nuint index = 0; index < NativeMethods.StatsGetIndexCount(stats); index++)
                indexes.Add(new ZVecIndexStats(Marshal.PtrToStringUTF8(NativeMethods.StatsGetIndexName(stats, index)) ?? string.Empty, NativeMethods.StatsGetIndexCompleteness(stats, index)));
            return new ZVecCollectionStats(NativeMethods.StatsGetDocCount(stats), indexes);
        }
        finally { NativeMethods.StatsDestroy(stats); }
    }

    private static NativeMetric ToNativeMetric(ZVecMetric metric) => metric switch
    {
        ZVecMetric.L2 => NativeMetric.L2,
        ZVecMetric.InnerProduct => NativeMetric.InnerProduct,
        ZVecMetric.Cosine => NativeMetric.Cosine,
        ZVecMetric.MipsL2 => NativeMetric.MipsL2,
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };

    private static NativeQuantization ToNativeQuantization(ZVecQuantization quantization) => quantization switch
    {
        ZVecQuantization.None => NativeQuantization.None,
        ZVecQuantization.Float16 => NativeQuantization.Float16,
        ZVecQuantization.Int8 => NativeQuantization.Int8,
        ZVecQuantization.Int4 => NativeQuantization.Int4,
        _ => throw new ArgumentOutOfRangeException(nameof(quantization))
    };

    private static NativeDataType GetNativeType(ZVecField field) => field.DataType switch
    {
        "STRING" => NativeDataType.String,
        "BOOL" => NativeDataType.Bool,
        "INT32" => NativeDataType.Int32,
        "INT64" => NativeDataType.Int64,
        "UINT32" => NativeDataType.UInt32,
        "UINT64" => NativeDataType.UInt64,
        "FLOAT" => NativeDataType.Float,
        "DOUBLE" => NativeDataType.Double,
        "VECTOR_FP32" => NativeDataType.VectorFloat32,
        _ => throw new NotSupportedException($"Unsupported ZVec data type {field.DataType}.")
    };

    private static bool IsNullable(PropertyInfo property) => !property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) is not null;

    private static void ValidateCollectionOptions(ZVecCollectionOptions options)
    {
        foreach (var (fieldName, index) in options.Indexes)
        {
            var field = ZVecSchemaMapper<TSchema>.Fields.SingleOrDefault(x => x.Name == fieldName)
                ?? throw new ArgumentException($"Index field '{fieldName}' does not exist in {typeof(TSchema).Name}.", nameof(options));
            if (field.Vector is null && index is ZVecVectorIndexOptions)
                throw new ArgumentException($"Vector index options cannot be applied to scalar field '{fieldName}'.", nameof(options));
            if (field.Vector is not null && index is not ZVecVectorIndexOptions)
                throw new ArgumentException($"Scalar index options cannot be applied to vector field '{fieldName}'.", nameof(options));
            if (index is ZVecHnswRaBitQIndexOptions && field.Vector is not { Dimensions: >= 64 and <= 4095 })
                throw new ArgumentOutOfRangeException(nameof(options), $"HNSW-RaBitQ field '{fieldName}' must have between 64 and 4095 dimensions.");
            ValidateIndexOptions(index);
        }
    }

    private static void ValidateIndexOptions(ZVecIndexOptions options)
    {
        if (options is ZVecVectorIndexOptions vector)
        {
            if (!Enum.IsDefined(vector.Metric)) throw new ArgumentOutOfRangeException(nameof(vector.Metric));
            if (!Enum.IsDefined(vector.Quantization)) throw new ArgumentOutOfRangeException(nameof(vector.Quantization));
            if (vector.EnableQuantizerRotation && vector.Quantization is not (ZVecQuantization.Int8 or ZVecQuantization.Int4))
                throw new ArgumentException("Quantizer rotation is only effective with Int8 or Int4 quantization.");
        }
        switch (options)
        {
            case ZVecHnswIndexOptions { M: <= 0 } or ZVecHnswIndexOptions { EfConstruction: <= 0 }:
                throw new ArgumentOutOfRangeException(nameof(options), "HNSW M and EfConstruction must be positive.");
            case ZVecHnswRaBitQIndexOptions { TotalBits: < 1 or > 9 } or
                ZVecHnswRaBitQIndexOptions { ClusterCount: <= 0 } or
                ZVecHnswRaBitQIndexOptions { M: < 5 or > 1024 } or
                ZVecHnswRaBitQIndexOptions { EfConstruction: < 1 or > 2048 } or
                ZVecHnswRaBitQIndexOptions { SampleCount: < 0 }:
                throw new ArgumentOutOfRangeException(nameof(options), "HNSW-RaBitQ requires TotalBits 1-9, positive ClusterCount, M 5-1024, EfConstruction 1-2048, and non-negative SampleCount.");
            case ZVecHnswRaBitQIndexOptions { Metric: ZVecMetric.MipsL2 }:
                throw new ArgumentException("HNSW-RaBitQ supports L2, InnerProduct, and Cosine metrics; MipsL2 is not supported.");
            case ZVecHnswRaBitQIndexOptions { Quantization: not ZVecQuantization.None } or ZVecHnswRaBitQIndexOptions { EnableQuantizerRotation: true }:
                throw new ArgumentException("HNSW-RaBitQ uses its mandatory RaBitQ quantizer; generic quantization and rotation options are not compatible.");
            case ZVecIvfIndexOptions { ListCount: <= 0 } or ZVecIvfIndexOptions { IterationCount: <= 0 }:
                throw new ArgumentOutOfRangeException(nameof(options), "IVF ListCount and IterationCount must be positive.");
            case ZVecDiskAnnIndexOptions { MaxDegree: <= 0 } or ZVecDiskAnnIndexOptions { ListSize: <= 0 } or ZVecDiskAnnIndexOptions { PqChunkCount: < 0 }:
                throw new ArgumentOutOfRangeException(nameof(options), "DiskANN MaxDegree and ListSize must be positive and PqChunkCount cannot be negative.");
            case ZVecFullTextIndexOptions fullText:
                if (fullText.Tokenizer is not ("standard" or "jieba" or "whitespace")) throw new ArgumentException("FTS tokenizer must be standard, jieba, or whitespace.");
                if (fullText.Filters.Any(x => x is not ("lowercase" or "ascii_folding" or "stemmer"))) throw new ArgumentException("FTS filters must be lowercase, ascii_folding, or stemmer.");
                break;
        }
    }

    private static bool QueryMatchesIndex(ZVecVectorQueryOptions query, ZVecIndexOptions? index) => (query, index) switch
    {
        (ZVecHnswQueryOptions, ZVecHnswIndexOptions) => true,
        (ZVecHnswRaBitQQueryOptions, ZVecHnswRaBitQIndexOptions) => true,
        (ZVecIvfQueryOptions, ZVecIvfIndexOptions) => true,
        (ZVecFlatQueryOptions, ZVecFlatIndexOptions) => true,
        (ZVecDiskAnnQueryOptions, ZVecDiskAnnIndexOptions) => true,
        _ => false
    };

    private static void ValidateQueryOptions(ZVecVectorQueryOptions? options)
    {
        switch (options)
        {
            case ZVecHnswQueryOptions { Ef: <= 0 }:
            case ZVecHnswRaBitQQueryOptions { Ef: < 1 or > 2048 }:
                throw new ArgumentOutOfRangeException(nameof(options), "HNSW-RaBitQ Ef must be between 1 and 2048.");
            case ZVecIvfQueryOptions { ProbeCount: <= 0 } or ZVecIvfQueryOptions { ScaleFactor: <= 0 }:
                throw new ArgumentOutOfRangeException(nameof(options), "IVF ProbeCount and ScaleFactor must be positive.");
            case ZVecFlatQueryOptions { ScaleFactor: <= 0 }:
                throw new ArgumentOutOfRangeException(nameof(options), "Flat ScaleFactor must be positive.");
            case ZVecDiskAnnQueryOptions { ListSize: <= 0 }:
                throw new ArgumentOutOfRangeException(nameof(options), "DiskANN ListSize must be positive.");
        }
    }

    private static void ValidateDocument(TSchema data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(data.Id);
        foreach (var field in ZVecSchemaMapper<TSchema>.Fields.Where(x => x.Vector is not null))
        {
            if (field.Property.GetValue(data) is not float[] vector || vector.Length != field.Vector!.Dimensions)
                throw new ArgumentException($"Vector field '{field.Name}' must contain exactly {field.Vector!.Dimensions} values.", nameof(data));
        }
    }

    private void ValidateQuery(string fieldName, ReadOnlyMemory<float> vector, int topK, ZVecVectorQueryOptions? queryOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK), "topK must be greater than zero.");
        var field = ZVecSchemaMapper<TSchema>.Fields.SingleOrDefault(x => x.Name == fieldName)
            ?? throw new ArgumentException($"Field '{fieldName}' does not exist in {typeof(TSchema).Name}.", nameof(fieldName));
        if (field.Vector is null) throw new ArgumentException($"Field '{fieldName}' is not a vector field.", nameof(fieldName));
        if (vector.Length != field.Vector.Dimensions)
            throw new ArgumentException($"Query vector for '{fieldName}' must contain exactly {field.Vector.Dimensions} values.", nameof(vector));
        ValidateQueryOptions(queryOptions);
        var index = GetIndexOptions(field, options);
        if (queryOptions is not null && !QueryMatchesIndex(queryOptions, index))
            throw new ArgumentException($"Query options '{queryOptions.GetType().Name}' do not match index '{index?.GetType().Name}' on field '{fieldName}'.", nameof(queryOptions));
    }
}
