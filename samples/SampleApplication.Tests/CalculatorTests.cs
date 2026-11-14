using NUnit.Framework;
using SampleApplication;
namespace SampleApplication.Tests;
[TestFixture]
public sealed class CalculatorTests
{
    private readonly Calculator _calculator = new();
    [Test] public void Add_ReturnsCorrectSum() => Assert.That(_calculator.Add(2, 3), Is.EqualTo(5));
    [TestCase(17, false)] [TestCase(18, true)] [TestCase(25, true)]
    public void IsAdult_HandlesAgeBoundary(int age, bool expected) => Assert.That(_calculator.IsAdult(age), Is.EqualTo(expected));
    [Test] public void IsAboveThreshold_AcceptsHigherValue() => Assert.That(_calculator.IsAboveThreshold(11), Is.True);
    [Test] public void Calculate_HandlesPreferredCustomer() => Assert.That(_calculator.Calculate(150, true), Is.EqualTo(300));
    [Test] public void Calculate_HandlesRegularCustomer() => Assert.That(_calculator.Calculate(150, false), Is.EqualTo(160));
    [Test] public void Calculate_HandlesSmallAmount() => Assert.That(_calculator.Calculate(50, false), Is.EqualTo(45));
}
