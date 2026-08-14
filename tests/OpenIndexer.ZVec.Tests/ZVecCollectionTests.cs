using OpenIndexer.ZVec;

namespace OpenIndexer.ZVec.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ZVecCollectionTests
{
    private string path = null!;

    [TestInitialize]
    public void Initialize()
    {
        path = Path.Combine(Path.GetTempPath(), "openindexer-zvec-tests", Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    [TestMethod]
    public void SchemaAttributesExposeNamedConfiguration()
    {
        var embedding = typeof(TestDocument).GetProperty(nameof(TestDocument.Embedding))!;
        var vector = (ZVecVectorAttribute)Attribute.GetCustomAttribute(embedding, typeof(ZVecVectorAttribute))!;
        Assert.AreEqual(4, vector.Dimensions);
        Assert.AreEqual(ZVecMetric.Cosine, vector.Metric);
        Assert.IsNotNull(Attribute.GetCustomAttribute(typeof(TestDocument).GetProperty(nameof(TestDocument.Category))!, typeof(ZVecIndexedAttribute)));
    }

    [TestMethod]
    public async Task CreateReopenUpsertGetUpdateDeleteAndListUsePersistentBackend()
    {
        await using (var collection = await ZVecCollection<TestDocument>.CreateOrOpenAsync(path))
        {
            await collection.UpsertAsync(Document("one", "first", "a", [1, 0, 0, 0]));
            await collection.UpsertAsync(Document("two", "second", "b", [0, 1, 0, 0]));
            await collection.UpsertAsync(Document("one", "updated", "a", [1, 0, 0, 0]));

            var item = await collection.GetAsync("one");
            Assert.IsNotNull(item);
            Assert.AreEqual("updated", item.Title);
            Assert.IsNull(await collection.GetAsync("missing"));

            var listed = new List<TestDocument>();
            await foreach (var value in collection.ListAsync()) listed.Add(value);
            CollectionAssert.AreEquivalent(new[] { "one", "two" }, listed.Select(x => x.Id).ToArray());

            await collection.DeleteAsync("two");
            await collection.DeleteAsync("two");
            Assert.IsNull(await collection.GetAsync("two"));
        }

        await using var reopened = await ZVecCollection<TestDocument>.CreateOrOpenAsync(path);
        Assert.AreEqual("updated", (await reopened.GetAsync("one"))?.Title);
    }

    [TestMethod]
    public async Task ScalarFieldsRoundTripThroughNativeDocuments()
    {
        await using var collection = await ZVecCollection<ScalarDocument>.CreateOrOpenAsync(path);
        await collection.UpsertAsync(new ScalarDocument
        {
            Id = "scalars",
            Text = "value",
            Boolean = true,
            Int32 = -32,
            Int64 = -64,
            UInt32 = 32,
            UInt64 = 64,
            Single = 1.25f,
            Double = 2.5
        });

        var value = await collection.GetAsync("scalars");
        Assert.IsNotNull(value);
        Assert.AreEqual("value", value.Text);
        Assert.IsTrue(value.Boolean);
        Assert.AreEqual(-32, value.Int32);
        Assert.AreEqual(-64L, value.Int64);
        Assert.AreEqual(32U, value.UInt32);
        Assert.AreEqual(64UL, value.UInt64);
        Assert.AreEqual(1.25f, value.Single);
        Assert.AreEqual(2.5, value.Double);
    }

    [TestMethod]
    public async Task SingleAndBatchQueriesReturnCorrectNearestDocuments()
    {
        await using var collection = await ZVecCollection<TestDocument>.CreateOrOpenAsync(path);
        await collection.UpsertAsync(Document("x", "X", "left", [1, 0, 0, 0]));
        await collection.UpsertAsync(Document("y", "Y", "right", [0, 1, 0, 0]));
        await collection.UpsertAsync(Document("z", "Z", "other", [0, 0, 1, 0]));

        var single = await collection.QueryAsync(nameof(TestDocument.Embedding), new float[] { 1, 0, 0, 0 }, topK: 2);
        Assert.AreEqual("x", single[0].Id);
        Assert.IsInstanceOfType<TestDocument>(single[0].Data);

        var batch = await collection.QueryAsync(nameof(TestDocument.Embedding), new ReadOnlyMemory<float>[] {
            new float[] { 1, 0, 0, 0 }, new float[] { 0, 1, 0, 0 }
        }, topK: 1);
        Assert.AreEqual(2, batch.Count);
        Assert.AreEqual("x", batch.Single(x => x.QueryIndex == 0).Id);
        Assert.AreEqual("y", batch.Single(x => x.QueryIndex == 1).Id);
    }

    [TestMethod]
    public async Task CosineMetricAndArticle768ExampleFlowAreApplied()
    {
        await using (var collection = await ZVecCollection<TestDocument>.CreateOrOpenAsync(path))
        {
            await collection.UpsertAsync(Document("cosine", "cosine", "indexed", [100, 0, 0, 0]));
            await collection.UpsertAsync(Document("l2", "l2", "indexed", [1, 1, 0, 0]));
            var results = await collection.QueryAsync(nameof(TestDocument.Embedding), new float[] { 1, 0, 0, 0 }, topK: 2);
            Assert.AreEqual("cosine", results[0].Id, "Cosine must rank the collinear vector above the Euclidean-nearest vector.");
        }

        Directory.Delete(path, true);
        await using var articles = await ZVecCollection<Article>.CreateOrOpenAsync(path);
        var embedding = new float[768];
        embedding[0] = 1;
        await articles.UpsertAsync(new Article
        {
            Id = "article-1",
            Title = "Bindings",
            Content = "ZVec C# bindings",
            Category = "dotnet",
            Embedding = embedding
        });
        var articleResults = await articles.QueryAsync(nameof(Article.Embedding), embedding, topK: 20);
        Assert.AreEqual("article-1", articleResults[0].Id);
        Assert.AreEqual("ZVec C# bindings", ((Article)articleResults[0].Data).Content);
    }

    [TestMethod]
    public async Task NativeErrorsArePropagatedClearly()
    {
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "not-a-collection.txt"), "invalid");
        var exception = await Assert.ThrowsExactlyAsync<ZVecException>(() => ZVecCollection<TestDocument>.CreateOrOpenAsync(path));
        Assert.AreNotEqual(ZVecErrorCode.Ok, exception.ErrorCode);
        Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Message));
    }

    [TestMethod]
    public async Task ValidatesArgumentsDimensionsCancellationAndDisposal()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => ZVecCollection<TestDocument>.CreateOrOpenAsync(path, cancelled.Token));

        var collection = await ZVecCollection<TestDocument>.CreateOrOpenAsync(path);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await collection.UpsertAsync(Document("bad", "bad", "bad", [1, 2])));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => collection.QueryAsync(nameof(TestDocument.Embedding), new float[] { 1, 2 }, 1));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => collection.QueryAsync(nameof(TestDocument.Embedding), new float[] { 1, 0, 0, 0 }, 0));

        collection.Dispose();
        collection.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await collection.GetAsync("bad"));

        var asynchronous = await ZVecCollection<TestDocument>.CreateOrOpenAsync(path);
        await asynchronous.DisposeAsync();
        await asynchronous.DisposeAsync();
    }

    [TestMethod]
    public async Task ListHonorsCancellationBetweenItemsAndExampleFlowSupportsTopK20()
    {
        await using var collection = await ZVecCollection<TestDocument>.CreateOrOpenAsync(path);
        await collection.UpsertAsync(Document("one", "one", "a", [1, 0, 0, 0]));
        await collection.UpsertAsync(Document("two", "two", "b", [0, 1, 0, 0]));

        using var cancellation = new CancellationTokenSource();
        var seen = 0;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in collection.ListAsync(cancellation.Token))
            {
                seen++;
                cancellation.Cancel();
            }
        });
        Assert.AreEqual(1, seen);

        var results = await collection.QueryAsync(nameof(TestDocument.Embedding), new float[] { 1, 0, 0, 0 }, topK: 20);
        Assert.IsTrue(results.Count > 0);
    }

    private static TestDocument Document(string id, string title, string category, float[] embedding) => new()
    {
        Id = id,
        Title = title,
        Category = category,
        Embedding = embedding
    };

    public sealed class TestDocument : ZVecCollectionSchema
    {
        public string Title { get; init; } = string.Empty;
        [ZVecIndexed] public string Category { get; init; } = string.Empty;
        [ZVecVector(Dimensions = 4, Metric = ZVecMetric.Cosine)] public float[] Embedding { get; init; } = [];
    }

    public sealed class ScalarDocument : ZVecCollectionSchema
    {
        public string Text { get; init; } = string.Empty;
        public bool Boolean { get; init; }
        public int Int32 { get; init; }
        public long Int64 { get; init; }
        public uint UInt32 { get; init; }
        public ulong UInt64 { get; init; }
        public float Single { get; init; }
        public double Double { get; init; }
    }
}
