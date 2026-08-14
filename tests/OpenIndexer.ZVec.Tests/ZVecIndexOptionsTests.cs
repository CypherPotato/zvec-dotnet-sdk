using OpenIndexer.ZVec;

namespace OpenIndexer.ZVec.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ZVecIndexOptionsTests
{
    private string path = null!;

    [TestInitialize]
    public void Initialize() => path = Path.Combine(Path.GetTempPath(), "openindexer-zvec-index-tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    [TestMethod]
    public void ManagedDefaultsMatchZVec06AndInvalidCombinationsAreRejected()
    {
        Assert.AreEqual(50, new ZVecHnswIndexOptions().M);
        Assert.AreEqual(500, new ZVecHnswIndexOptions().EfConstruction);
        Assert.AreEqual(300, new ZVecHnswQueryOptions().Ef);
        Assert.AreEqual(1024, new ZVecIvfIndexOptions().ListCount);
        Assert.AreEqual(10, new ZVecIvfIndexOptions().IterationCount);
        Assert.AreEqual(100, new ZVecDiskAnnIndexOptions().MaxDegree);
        Assert.AreEqual(50, new ZVecDiskAnnIndexOptions().ListSize);
        Assert.AreEqual(300, new ZVecDiskAnnQueryOptions().ListSize);
    }

    [TestMethod]
    public async Task FlatIndexSupportsTypedQueryOptimizeAndStats()
    {
        var options = Options(new ZVecFlatIndexOptions { Metric = ZVecMetric.Cosine });
        await using var collection = await ZVecCollection<VectorDocument>.CreateOrOpenAsync(path, options);
        await InsertDocuments(collection);

        var before = await collection.GetStatsAsync();
        Assert.AreEqual(3UL, before.DocumentCount);
        Assert.AreEqual(1, before.Indexes.Count);
        Assert.AreEqual(nameof(VectorDocument.Embedding), before.Indexes[0].Name);

        var results = await collection.QueryAsync(nameof(VectorDocument.Embedding), new float[] { 1, 0, 0, 0 }, 2, new ZVecFlatQueryOptions());
        Assert.AreEqual("x", results[0].Id);
        await collection.OptimizeAsync();
        var after = await collection.GetStatsAsync();
        Assert.AreEqual(1f, after.Indexes.Single().Completeness, 0.0001f);
    }

    [TestMethod]
    public async Task HnswCustomConstructionAndQueryParametersRoundTripOnReopen()
    {
        var options = Options(new ZVecHnswIndexOptions { Metric = ZVecMetric.Cosine, M = 24, EfConstruction = 320 });
        await using (var collection = await ZVecCollection<VectorDocument>.CreateOrOpenAsync(path, options))
        {
            await InsertDocuments(collection);
            var results = await collection.QueryAsync(nameof(VectorDocument.Embedding), new float[] { 1, 0, 0, 0 }, 2, new ZVecHnswQueryOptions { Ef = 64 });
            Assert.AreEqual("x", results[0].Id);
            await collection.OptimizeAsync();
        }

        await using var reopened = await ZVecCollection<VectorDocument>.CreateOrOpenAsync(path, options);
        Assert.AreEqual(3UL, (await reopened.GetStatsAsync()).DocumentCount);
    }

    [TestMethod]
    public async Task IvfIndexAndQueryParametersAreFunctional()
    {
        var options = Options(new ZVecIvfIndexOptions { Metric = ZVecMetric.Cosine, ListCount = 2, IterationCount = 2 });
        await using var collection = await ZVecCollection<VectorDocument>.CreateOrOpenAsync(path, options);
        await InsertDocuments(collection);
        await collection.OptimizeAsync();

        var results = await collection.QueryAsync(nameof(VectorDocument.Embedding), new float[] { 1, 0, 0, 0 }, 2, new ZVecIvfQueryOptions { ProbeCount = 2 });
        Assert.AreEqual("x", results[0].Id);
    }

    [TestMethod]
    public async Task QuantizationAndScalarIndexesCanBeConfigured()
    {
        var options = Options(new ZVecFlatIndexOptions
        {
            Metric = ZVecMetric.Cosine,
            Quantization = ZVecQuantization.Int8,
            EnableQuantizerRotation = true
        });
        options.Indexes[nameof(VectorDocument.Category)] = new ZVecInvertedIndexOptions
        {
            EnableRangeOptimization = false,
            EnableExtendedWildcard = true
        };
        options.Indexes[nameof(VectorDocument.Text)] = new ZVecFullTextIndexOptions
        {
            Tokenizer = "whitespace",
            Filters = ["lowercase", "ascii_folding"]
        };

        await using (var collection = await ZVecCollection<VectorDocument>.CreateOrOpenAsync(path, options))
        {
            await InsertDocuments(collection);
            await collection.OptimizeAsync();
        }
        await using var reopened = await ZVecCollection<VectorDocument>.CreateOrOpenAsync(path, options);
        Assert.AreEqual(3UL, (await reopened.GetStatsAsync()).DocumentCount);
        var textResults = await reopened.FullTextQueryAsync(nameof(VectorDocument.Text), new ZVecFullTextQueryOptions { Match = "searchable", TopK = 10 });
        Assert.AreEqual(3, textResults.Count);
        var filtered = await reopened.QueryAsync(nameof(VectorDocument.Embedding), new float[] { 1, 0, 0, 0 }, 3, new ZVecFlatQueryOptions { Filter = "Category = 'category'" });
        Assert.AreEqual(3, filtered.Count);
    }

    [TestMethod]
    public async Task ReopeningWithDifferentPersistedSettingsFailsClearly()
    {
        await using (var collection = await ZVecCollection<VectorDocument>.CreateOrOpenAsync(path, Options(new ZVecHnswIndexOptions { M = 16, EfConstruction = 200 }))) { }
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            ZVecCollection<VectorDocument>.CreateOrOpenAsync(path, Options(new ZVecHnswIndexOptions { M = 32, EfConstruction = 400 })));
        StringAssert.Contains(exception.Message, "does not migrate");
    }

    [TestMethod]
    public async Task HnswRaBitQUsesEffectiveNativeTypeAndTypedQueryOnSupportedRuntime()
    {
        var options = RaBitQOptions();
        if (!OperatingSystem.IsLinux() ||
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64 ||
            !System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            var exception = await Assert.ThrowsExactlyAsync<ZVecCompatibilityException>(() =>
                ZVecCollection<RaBitQDocument>.CreateOrOpenAsync(path, options));
            StringAssert.Contains(exception.Message, "Linux x64");
            StringAssert.Contains(exception.Message, "NativeVersion=0.6.0");
            StringAssert.Contains(exception.Message, "Requested=HNSW-RaBitQ");
            Assert.IsFalse(Directory.Exists(path), "An incompatible runtime must be rejected before collection creation.");
            return;
        }

        await using (var collection = await ZVecCollection<RaBitQDocument>.CreateOrOpenAsync(path, options))
        {
            await collection.UpsertAsync(RaBitQDocument.Create("x", 0));
            await collection.UpsertAsync(RaBitQDocument.Create("y", 1));
            await collection.OptimizeAsync();
            var results = await collection.QueryAsync(
                nameof(RaBitQDocument.Embedding), RaBitQDocument.Vector(0), 1,
                new ZVecHnswRaBitQQueryOptions { Ef = 64, UseRefiner = true });
            Assert.AreEqual("x", results[0].Id);
        }

        await using (var reopened = await ZVecCollection<RaBitQDocument>.CreateOrOpenAsync(path, options))
            Assert.AreEqual(2UL, (await reopened.GetStatsAsync()).DocumentCount);

        var changed = RaBitQOptions(new ZVecHnswRaBitQIndexOptions
        {
            Metric = ZVecMetric.Cosine,
            TotalBits = 6,
            ClusterCount = 16,
            M = 16,
            EfConstruction = 100
        });
        var mismatch = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            ZVecCollection<RaBitQDocument>.CreateOrOpenAsync(path, changed));
        StringAssert.Contains(mismatch.Message, "does not migrate");
    }

    [TestMethod]
    public async Task HnswRaBitQRejectsInvalidDimensionsAndParametersBeforeNativeCreation()
    {
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            ZVecCollection<VectorDocument>.CreateOrOpenAsync(path, Options(new ZVecHnswRaBitQIndexOptions())));

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            ZVecCollection<RaBitQDocument>.CreateOrOpenAsync(path, RaBitQOptions(new ZVecHnswRaBitQIndexOptions { TotalBits = 10 })));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            ZVecCollection<RaBitQDocument>.CreateOrOpenAsync(path, RaBitQOptions(new ZVecHnswRaBitQIndexOptions { Metric = ZVecMetric.MipsL2 })));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            ZVecCollection<RaBitQDocument>.CreateOrOpenAsync(path, RaBitQOptions(new ZVecHnswRaBitQIndexOptions { Quantization = ZVecQuantization.Int8 })));
    }

    [TestMethod]
    public async Task QueryOptionsMustMatchThePersistedIndexType()
    {
        await using var collection = await ZVecCollection<VectorDocument>.CreateOrOpenAsync(path, Options(new ZVecFlatIndexOptions()));
        await collection.UpsertAsync(Document("x", [1, 0, 0, 0]));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => collection.QueryAsync(
            nameof(VectorDocument.Embedding), new float[] { 1, 0, 0, 0 }, 1, new ZVecHnswQueryOptions()));
    }

    [TestMethod]
    public async Task DiskAnnOptionsAreValidatedAndNativeTestRunsOnlyOnLinux()
    {
        var invalid = Options(new ZVecDiskAnnIndexOptions { ListSize = 0 });
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => ZVecCollection<VectorDocument>.CreateOrOpenAsync(path, invalid));
        if (!OperatingSystem.IsLinux() ||
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) return;

        var options = Options(new ZVecDiskAnnIndexOptions { Metric = ZVecMetric.Cosine, MaxDegree = 8, ListSize = 16 });
        await using var collection = await ZVecCollection<VectorDocument>.CreateOrOpenAsync(path, options);
        await InsertDocuments(collection);
        await collection.OptimizeAsync();
        var results = await collection.QueryAsync(nameof(VectorDocument.Embedding), new float[] { 1, 0, 0, 0 }, 1, new ZVecDiskAnnQueryOptions { ListSize = 32 });
        Assert.AreEqual("x", results[0].Id);
    }

    private static ZVecCollectionOptions Options(ZVecVectorIndexOptions vectorIndex)
    {
        var options = new ZVecCollectionOptions();
        options.Indexes[nameof(VectorDocument.Embedding)] = vectorIndex;
        return options;
    }

    private static ZVecCollectionOptions RaBitQOptions(ZVecHnswRaBitQIndexOptions? index = null)
    {
        var options = new ZVecCollectionOptions();
        options.Indexes[nameof(RaBitQDocument.Embedding)] = index ?? new ZVecHnswRaBitQIndexOptions
        {
            Metric = ZVecMetric.Cosine,
            TotalBits = 7,
            ClusterCount = 16,
            M = 16,
            EfConstruction = 100
        };
        return options;
    }

    private static async Task InsertDocuments(ZVecCollection<VectorDocument> collection)
    {
        await collection.UpsertAsync(Document("x", [1, 0, 0, 0]));
        await collection.UpsertAsync(Document("y", [0, 1, 0, 0]));
        await collection.UpsertAsync(Document("z", [0, 0, 1, 0]));
    }

    private static VectorDocument Document(string id, float[] embedding) => new()
    {
        Id = id,
        Category = "category",
        Text = "searchable text",
        Embedding = embedding
    };

    public sealed class RaBitQDocument : ZVecCollectionSchema
    {
        [ZVecVector(Dimensions = 64, Metric = ZVecMetric.Cosine)]
        public float[] Embedding { get; init; } = [];

        public static RaBitQDocument Create(string id, int axis) => new() { Id = id, Embedding = Vector(axis) };

        public static float[] Vector(int axis)
        {
            var vector = new float[64];
            vector[axis] = 1;
            return vector;
        }
    }

    public sealed class VectorDocument : ZVecCollectionSchema
    {
        public string Category { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        [ZVecVector(Dimensions = 4)] public float[] Embedding { get; init; } = [];
    }
}
