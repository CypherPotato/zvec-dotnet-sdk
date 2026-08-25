using HNSW.Net;
using OpenIndexer.ZVec;

namespace OpenIndexer.ZVec.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ZVecScoreCalibrationTests
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
    public async Task CosineQueryScoreMatchesOneMinusCosineDistance()
    {
        var baseVector = CreateVector(0.6f, 0.8f);
        var orthogonal = CreateVector(-0.8f, 0.6f);
        var opposite = CreateVector(-0.6f, -0.8f);

        await using var collection = await ZVecCollection<CalibrationDocument>.CreateOrOpenAsync(path);
        await collection.UpsertAsync(new CalibrationDocument { Id = "base", Embedding = baseVector });
        await collection.UpsertAsync(new CalibrationDocument { Id = "orthogonal", Embedding = orthogonal });
        await collection.UpsertAsync(new CalibrationDocument { Id = "opposite", Embedding = opposite });

        var results = await collection.QueryAsync(nameof(CalibrationDocument.Embedding), baseVector, topK: 3);
        var scoresById = results.ToDictionary(result => result.Id, result => result.Score);

        // The migration maps ZVec scores to cosine similarity via (1 - score); these assertions
        // pin the semantics of the native score against 1 - HNSW.Net.CosineDistance.SIMD.
        AssertExpectedSimilarity("base", baseVector);
        AssertExpectedSimilarity("orthogonal", orthogonal);
        AssertExpectedSimilarity("opposite", opposite);

        void AssertExpectedSimilarity(string id, float[] embedding)
        {
            Assert.IsTrue(scoresById.TryGetValue(id, out var score), $"Missing score for '{id}'.");
            float expectedSimilarity = 1f - CosineDistance.SIMD(baseVector, embedding);
            float actualSimilarity = 1f - score;
            Assert.AreEqual(expectedSimilarity, actualSimilarity, 0.001f,
                $"ZVec score for '{id}' ({score}) does not map to cosine similarity via (1 - score).");
        }
    }

    private static float[] CreateVector(float x, float y)
    {
        var vector = new float[768];
        vector[0] = x;
        vector[1] = y;
        return vector;
    }

    public sealed class CalibrationDocument : ZVecCollectionSchema
    {
        [ZVecVector(Dimensions = 768, Metric = ZVecMetric.Cosine)] public float[] Embedding { get; init; } = [];
    }
}
