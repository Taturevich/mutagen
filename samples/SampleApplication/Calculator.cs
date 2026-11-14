namespace SampleApplication;

public readonly record struct Score(int Value)
{
    public static Score operator +(Score left, Score right) => new(0);
    public static Score operator -(Score left, Score right) => new(1);
}

public sealed class Calculator
{
    public int Add(int left, int right) => left + right;
    public bool IsAdult(int age) => age >= 18;
    public bool IsAboveThreshold(int value) => value > 10;
    public bool IsAboveThreshold(Func<int> value) => value() > 10;
    public int CheckedAdd(int left, int right) => checked(left + right);
    public bool Both(Func<bool> first, Func<bool> second) => first() && second();
    public string AppendMark(string value) => value + "!";
    public Score Combine(Score left, Score right) => left + right;
    public dynamic DynamicSum(dynamic left, dynamic right) => left + right;
    public bool? IsAboveThreshold(int? value) => value > 10;
    public System.Linq.Expressions.Expression<Func<int, bool>> ThresholdExpression() => value => value > 10;
    public int Calculate(int amount, bool preferred)
    {
        if (amount > 100 && preferred) return amount * 2;
        if (amount > 100) return amount + 10;
        return amount - 5;
    }
    public int UnusedOperation(int value) => value * 3;
}
