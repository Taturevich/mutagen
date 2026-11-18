using NUnit.Framework;
namespace SampleApplication.Tests;

[TestFixture]
public sealed class CalculatorTests
{
  private readonly Calculator _calculator = new();
  [Test]
  public void Add_ReturnsCorrectSum()
  {
    var marker = Environment.GetEnvironmentVariable("MUTAGEN_COVERAGE_BASELINE_MARKER");
    if (!string.IsNullOrWhiteSpace(marker) &&
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MUTATION_ACTIVE_ID")) &&
        (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MUTATION_PROBE_FILE")) ||
         Environment.GetEnvironmentVariable("MUTATION_PER_TEST_COVERAGE") == "1"))
    {
      File.AppendAllText(marker, "baseline" + Environment.NewLine);
    }

    Assert.That(_calculator.Add(2, 3), Is.EqualTo(5));
  }
  [TestCase(17, false)]
  [TestCase(18, true)]
  [TestCase(25, true)]
  public void IsAdult_HandlesAgeBoundary(int age, bool expected) => Assert.That(_calculator.IsAdult(age), Is.EqualTo(expected));
  [Test] public void IsAboveThreshold_AcceptsHigherValue() => Assert.That(_calculator.IsAboveThreshold(11), Is.True);
  [Test] public void ThrowingOperand_StillEntersCoverageSite() => Assert.Throws<InvalidOperationException>(() => _calculator.IsAboveThreshold(() => throw new InvalidOperationException("expected")));
  [Test]
  public void CheckedArithmetic_PreservesOverflowBehavior()
  {
    Assert.That(_calculator.CheckedAdd(4, 5), Is.EqualTo(9));
    Assert.Throws<OverflowException>(() => _calculator.CheckedAdd(int.MaxValue, 1));
  }
  [Test]
  public void ShortCircuit_PreservesOrderAndSingleEvaluation()
  {
    int firstCalls = 0, secondCalls = 0;
    Assert.That(_calculator.Both(() => { firstCalls++; return false; }, () => { secondCalls++; return true; }), Is.False);
    Assert.That(firstCalls, Is.EqualTo(1));
    Assert.That(secondCalls, Is.Zero);
    Assert.That(_calculator.Both(() => { firstCalls++; return true; }, () => { secondCalls++; return true; }), Is.True);
    Assert.That(firstCalls, Is.EqualTo(2));
    Assert.That(secondCalls, Is.EqualTo(1));
  }
  [Test] public void StringAddition_IsPreservedAndExcludedFromMutation() => Assert.That(_calculator.AppendMark("value"), Is.EqualTo("value!"));
  [Test] public void UserDefinedOperator_IsPreservedAndExcludedFromMutation() => Assert.That(_calculator.Combine(new Score(1), new Score(2)), Is.EqualTo(new Score(0)));
  [Test] public void DynamicOperator_IsPreservedAndExcludedFromMutation() => Assert.That((int)_calculator.DynamicSum(2, 3), Is.EqualTo(5));
  [Test]
  public void NullableLiftedComparison_PreservesNullAndValueResults()
  {
    Assert.That(_calculator.IsAboveThreshold((int?)null), Is.False);
    Assert.That(_calculator.IsAboveThreshold(11), Is.True);
  }
  [Test] public void ExpressionTree_IsPreservedAndExcludedFromMutation() => Assert.That(_calculator.ThresholdExpression().Compile()(11), Is.True);
  [Test] public void Calculate_HandlesPreferredCustomer() => Assert.That(_calculator.Calculate(150, true), Is.EqualTo(300));
  [Test] public void Calculate_HandlesRegularCustomer() => Assert.That(_calculator.Calculate(150, false), Is.EqualTo(160));
  [Test] public void Calculate_HandlesSmallAmount() => Assert.That(_calculator.Calculate(50, false), Is.EqualTo(45));
}

[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class AsyncLifecycleTests
{
  private static readonly int StaticInitialization = 42;
  private static int _fixtureReady;
  private readonly Calculator _calculator = new();

  [OneTimeSetUp]
  public void FixtureSetUp() => Interlocked.Exchange(ref _fixtureReady, 1);

  [SetUp]
  public void SetUp() => Assert.That(Volatile.Read(ref _fixtureReady), Is.EqualTo(1));

  [TearDown]
  public void TearDown() => Assert.That(Volatile.Read(ref _fixtureReady), Is.EqualTo(1));

  [OneTimeTearDown]
  public void FixtureTearDown() => Interlocked.Exchange(ref _fixtureReady, 0);

  [TestCase(6, 9, 15)]
  [TestCase(10, 12, 22)]
  public async Task AddAsync_RunsWithFixtureAndStaticInitialization(int left, int right, int expected)
  {
    await Task.Yield();
    Assert.That(StaticInitialization, Is.EqualTo(42));
    Assert.That(_calculator.Add(left, right), Is.EqualTo(expected));
  }
}
