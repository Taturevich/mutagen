namespace SampleApplication;

public sealed class Calculator
{
    public int Add(int left, int right) => left + right;
    public bool IsAdult(int age) => age >= 18;
    public bool IsAboveThreshold(int value) => value > 10;
    public int Calculate(int amount, bool preferred)
    {
        if (amount > 100 && preferred) return amount * 2;
        if (amount > 100) return amount + 10;
        return amount - 5;
    }
    public int UnusedOperation(int value) => value * 3;
}
