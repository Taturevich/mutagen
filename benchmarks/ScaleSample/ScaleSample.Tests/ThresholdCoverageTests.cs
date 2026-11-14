using NUnit.Framework;
using ScaleSample;
namespace ScaleSample.Tests;
[TestFixture]
public sealed class ThresholdCoverageTests
{
    private static IEnumerable<TestCaseData> Pairs => Enumerable.Range(0, 100).Select(i => new TestCaseData(i));
    [TestCaseSource(nameof(Pairs))]
    public void EachCaseCoversTwoAdjacentOperations(int index)
    {
        Assert.That(ThresholdOperations.Evaluate(index, 1000), Is.True);
        Assert.That(ThresholdOperations.Evaluate((index + 1) % 100, 1000), Is.True);
    }
}
