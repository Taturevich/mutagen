namespace ScaleSample;
public static class ThresholdOperations
{
    public static bool Evaluate(int operation, int value) => (operation / 20) switch
    {
        0 => ThresholdPart0.Evaluate(operation % 20, value),
        1 => ThresholdPart1.Evaluate(operation % 20, value),
        2 => ThresholdPart2.Evaluate(operation % 20, value),
        3 => ThresholdPart3.Evaluate(operation % 20, value),
        4 => ThresholdPart4.Evaluate(operation % 20, value),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
}
public static class ThresholdPart0
{
    public static bool Evaluate(int operation, int value) => operation switch
    {
        0 => value > 0,
        1 => value > 1,
        2 => value > 2,
        3 => value > 3,
        4 => value > 4,
        5 => value > 5,
        6 => value > 6,
        7 => value > 7,
        8 => value > 8,
        9 => value > 9,
        10 => value > 10,
        11 => value > 11,
        12 => value > 12,
        13 => value > 13,
        14 => value > 14,
        15 => value > 15,
        16 => value > 16,
        17 => value > 17,
        18 => value > 18,
        19 => value > 19,
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
}
public static class ThresholdPart1
{
    public static bool Evaluate(int operation, int value) => operation switch
    {
        0 => value > 20,
        1 => value > 21,
        2 => value > 22,
        3 => value > 23,
        4 => value > 24,
        5 => value > 25,
        6 => value > 26,
        7 => value > 27,
        8 => value > 28,
        9 => value > 29,
        10 => value > 30,
        11 => value > 31,
        12 => value > 32,
        13 => value > 33,
        14 => value > 34,
        15 => value > 35,
        16 => value > 36,
        17 => value > 37,
        18 => value > 38,
        19 => value > 39,
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
}
public static class ThresholdPart2
{
    public static bool Evaluate(int operation, int value) => operation switch
    {
        0 => value > 40,
        1 => value > 41,
        2 => value > 42,
        3 => value > 43,
        4 => value > 44,
        5 => value > 45,
        6 => value > 46,
        7 => value > 47,
        8 => value > 48,
        9 => value > 49,
        10 => value > 50,
        11 => value > 51,
        12 => value > 52,
        13 => value > 53,
        14 => value > 54,
        15 => value > 55,
        16 => value > 56,
        17 => value > 57,
        18 => value > 58,
        19 => value > 59,
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
}
public static class ThresholdPart3
{
    public static bool Evaluate(int operation, int value) => operation switch
    {
        0 => value > 60,
        1 => value > 61,
        2 => value > 62,
        3 => value > 63,
        4 => value > 64,
        5 => value > 65,
        6 => value > 66,
        7 => value > 67,
        8 => value > 68,
        9 => value > 69,
        10 => value > 70,
        11 => value > 71,
        12 => value > 72,
        13 => value > 73,
        14 => value > 74,
        15 => value > 75,
        16 => value > 76,
        17 => value > 77,
        18 => value > 78,
        19 => value > 79,
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
}
public static class ThresholdPart4
{
    public static bool Evaluate(int operation, int value) => operation switch
    {
        0 => value > 80,
        1 => value > 81,
        2 => value > 82,
        3 => value > 83,
        4 => value > 84,
        5 => value > 85,
        6 => value > 86,
        7 => value > 87,
        8 => value > 88,
        9 => value > 89,
        10 => value > 90,
        11 => value > 91,
        12 => value > 92,
        13 => value > 93,
        14 => value > 94,
        15 => value > 95,
        16 => value > 96,
        17 => value > 97,
        18 => value > 98,
        19 => value > 99,
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
}
