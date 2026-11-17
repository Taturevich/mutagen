using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Security.Cryptography;
using System.Text;

namespace MutationFramework.Sdk;

internal sealed class RegexMutator : IMutator<ObjectCreationExpressionSyntax>
{
  private static readonly Dictionary<char, char> ShorthandOpposites = new()
  {
    ['d'] = 'D',
    ['D'] = 'd',
    ['w'] = 'W',
    ['W'] = 'w',
    ['s'] = 'S',
    ['S'] = 's'
  };

  public MutatorMetadata Metadata { get; } = new("regex-patterns", 1, MutationCategory.Regex,
      new HashSet<string>(["anchors", "character classes", "quantifiers", "lookarounds", "capture groups"], StringComparer.Ordinal))
  { Categories = new HashSet<MutationCategory>([MutationCategory.Regex]) };

  public IEnumerable<MutationDescription> Generate(ObjectCreationExpressionSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    if (model.GetSymbolInfo(node).Symbol is not IMethodSymbol constructor ||
        constructor.MethodKind != MethodKind.Constructor ||
        constructor.ContainingType.ToDisplayString() != "System.Text.RegularExpressions.Regex")
    {
      yield break;
    }

    var patternArgument = node.ArgumentList?.Arguments.FirstOrDefault(argument =>
        argument.NameColon?.Name.Identifier.ValueText == "pattern") ?? node.ArgumentList?.Arguments.FirstOrDefault();
    if (patternArgument is null)
    {
      yield break;
    }

    var constant = model.GetConstantValue(patternArgument.Expression);
    if (!constant.HasValue || constant.Value is not string pattern)
    {
      yield return new(Metadata, node, node.ToString(), "regex:unsupported-pattern-expression", node.ToString(),
          "Regex mutation requires a compile-time constant string pattern.");
      yield break;
    }

    string? invalidPatternMessage = null;
    try
    {
      _ = new System.Text.RegularExpressions.Regex(pattern);
    }
    catch (ArgumentException exception)
    {
      invalidPatternMessage = exception.Message;
    }
    if (invalidPatternMessage is not null)
    {
      yield return new(Metadata, node, node.ToString(), "regex:invalid-runtime-pattern", node.ToString(),
          $"The original pattern is invalid in this .NET regex runtime: {invalidPatternMessage}");
      yield break;
    }

    var emitted = new HashSet<string>(StringComparer.Ordinal);
    foreach (var (replacementPattern, token) in MutatePattern(pattern))
    {
      if (replacementPattern == pattern || !emitted.Add(replacementPattern))
      {
        continue;
      }

      try
      {
        _ = new System.Text.RegularExpressions.Regex(replacementPattern);
      }
      catch (ArgumentException)
      {
        continue;
      }

      var literal = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(replacementPattern));
      var replacement = node.ReplaceNode(patternArgument.Expression, literal).ToString();
      yield return new(Metadata, node, replacement, token + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(replacementPattern))), node.ToString());
    }
  }

  private static IEnumerable<(string Pattern, string Token)> MutatePattern(string pattern)
  {
    var emitted = new HashSet<(int Start, int Length, string Replacement)>(new RegexEditComparer());
    for (var index = 0; index < pattern.Length; index++)
    {
      var current = pattern[index];
      if (current == '[' && !IsEscaped(pattern, index) && TryFindCharacterClassEnd(pattern, index, out var classEnd))
      {
        foreach (var edit in MutateCharacterClass(pattern, index, classEnd))
        {
          if (emitted.Add((edit.Start, edit.Length, edit.Replacement)))
          {
            yield return Apply(pattern, edit.Start, edit.Length, edit.Replacement, edit.Name);
          }
        }
        index = classEnd;
        continue;
      }

      if (current == '\\' && index + 1 < pattern.Length)
      {
        var escaped = pattern[index + 1];
        if (ShorthandOpposites.TryGetValue(escaped, out var opposite))
        {
          yield return Apply(pattern, index, 2, "\\" + opposite, "regex:shorthand-negation");
          yield return Apply(pattern, index, 2, escaped.ToString(), "regex:shorthand-nullification");
          yield return Apply(pattern, index, 2, $"[\\{escaped}\\{opposite}]", "regex:shorthand-any-character");
        }
        else if (escaped is 'p' or 'P' && index + 2 < pattern.Length && pattern[index + 2] == '{')
        {
          var end = pattern.IndexOf('}', index + 3);
          if (end > index)
          {
            var flipped = escaped == 'p' ? 'P' : 'p';
            var unicode = "\\" + flipped + pattern[(index + 2)..(end + 1)];
            yield return Apply(pattern, index, end - index + 1, unicode, "regex:unicode-class-negation");
            index = end;
            continue;
          }
        }
        else if (escaped is 'A' or 'Z' or 'z' or 'b' or 'B' or 'G')
        {
          yield return Apply(pattern, index, 2, string.Empty, "regex:anchor-removal");
        }
        index++;
        continue;
      }

      if (current == '^' && index == 0 || current == '$' && index == pattern.Length - 1)
      {
        yield return Apply(pattern, index, 1, string.Empty, "regex:anchor-removal");
      }

      if (current == '(' && index + 2 < pattern.Length)
      {
        if (pattern.AsSpan(index).StartsWith("(?="))
        {
          yield return Apply(pattern, index, 3, "(?!", "regex:lookaround-negation");
        }
        else if (pattern.AsSpan(index).StartsWith("(?!"))
        {
          yield return Apply(pattern, index, 3, "(?=", "regex:lookaround-negation");
        }
        else if (pattern.AsSpan(index).StartsWith("(?<="))
        {
          yield return Apply(pattern, index, 4, "(?<!", "regex:lookaround-negation");
        }
        else if (pattern.AsSpan(index).StartsWith("(?<!"))
        {
          yield return Apply(pattern, index, 4, "(?<=", "regex:lookaround-negation");
        }
        else if (pattern[index + 1] != '?')
        {
          yield return Apply(pattern, index, 1, "(?:", "regex:group-to-non-capturing");
        }
      }

      if (current is '*' or '+' or '?' && !IsEscaped(pattern, index) &&
          !(current == '?' && index > 0 && pattern[index - 1] is '(' or '*' or '+' or '?'))
      {
        yield return Apply(pattern, index, 1, string.Empty, "regex:quantifier-removal");
        var shortRange = current switch
        {
          '?' => new[] { "{1,1}", "{0,0}", "{0,2}" },
          '*' => ["{1,}"],
          '+' => ["{0,}", "{2,}"],
          _ => []
        };
        foreach (var replacement in shortRange)
        {
          yield return Apply(pattern, index, 1, replacement, "regex:short-quantifier-range");
        }
        if (index + 1 >= pattern.Length || pattern[index + 1] != '?')
        {
          yield return Apply(pattern, index + 1, 0, "?", "regex:reluctant-quantifier");
        }
      }

      if (current == '{' && !IsEscaped(pattern, index) && TryParseRangeQuantifier(pattern, index, out var rangeEnd, out var minimum, out var maximum))
      {
        var length = rangeEnd - index + 1;
        yield return Apply(pattern, index, length, string.Empty, "regex:quantifier-removal");
        if (maximum is null)
        {
          if (minimum > 0)
          {
            yield return Apply(pattern, index, length, "{" + (minimum - 1) + ",}", "regex:quantifier-boundary");
          }
          yield return Apply(pattern, index, length, "{" + (minimum + 1) + ",}", "regex:quantifier-boundary");
        }
        else if (minimum < maximum)
        {
          if (minimum > 0)
          {
            yield return Apply(pattern, index, length, "{" + (minimum - 1) + "," + maximum + "}", "regex:quantifier-boundary");
          }
          yield return Apply(pattern, index, length, "{" + (minimum + 1) + "," + maximum + "}", "regex:quantifier-boundary");
          if (maximum - 1 >= minimum)
          {
            yield return Apply(pattern, index, length, "{" + minimum + "," + (maximum - 1) + "}", "regex:quantifier-boundary");
          }
          yield return Apply(pattern, index, length, "{" + minimum + "," + (maximum + 1) + "}", "regex:quantifier-boundary");
        }
        if (rangeEnd + 1 >= pattern.Length || pattern[rangeEnd + 1] != '?')
        {
          yield return Apply(pattern, rangeEnd + 1, 0, "?", "regex:reluctant-quantifier");
        }
        index = rangeEnd;
      }
    }
  }

  private static IEnumerable<(int Start, int Length, string Replacement, string Name)> MutateCharacterClass(string pattern, int start, int end)
  {
    var innerStart = start + 1;
    var negated = innerStart < end && pattern[innerStart] == '^';
    var contentStart = negated ? innerStart + 1 : innerStart;
    var content = pattern[contentStart..end];
    yield return (start, end - start + 1, negated ? "[" + content + "]" : "[^" + content + "]", "regex:character-class-negation");
    yield return (start, end - start + 1, "[\\w\\W]", "regex:character-class-any-character");

    var children = new List<(int Start, int Length)>();
    for (var index = contentStart; index < end;)
    {
      var length = pattern[index] == '\\' && index + 1 < end ? 2 : 1;
      if (index + 2 < end && pattern[index + 1] == '-' && pattern[index] != '\\')
      {
        length = 3;
      }
      children.Add((index, length));
      index += length;
    }
    foreach (var child in children)
    {
      if (content.Length - child.Length > 0)
      {
        yield return (child.Start, child.Length, string.Empty, "regex:character-class-child-removal");
      }
    }

    for (var index = contentStart; index + 2 < end; index++)
    {
      if (pattern[index + 1] != '-' || !char.IsAscii(pattern[index]) || !char.IsAscii(pattern[index + 2]))
      {
        continue;
      }
      var low = pattern[index];
      var high = pattern[index + 2];
      if (low + 1 < high)
      {
        yield return (index, 3, (char)(low - 1) + "-" + high, "regex:character-class-range");
        yield return (index, 3, (char)(low + 1) + "-" + high, "regex:character-class-range");
        yield return (index, 3, low + "-" + (char)(high - 1), "regex:character-class-range");
        yield return (index, 3, low + "-" + (char)(high + 1), "regex:character-class-range");
      }
    }
  }

  private static bool TryFindCharacterClassEnd(string pattern, int start, out int end)
  {
    for (var index = start + 1; index < pattern.Length; index++)
    {
      if (pattern[index] == ']' && !IsEscaped(pattern, index) && index > start + 1)
      {
        end = index;
        return true;
      }
    }
    end = -1;
    return false;
  }

  private static bool TryParseRangeQuantifier(string pattern, int start, out int end, out int minimum, out int? maximum)
  {
    end = -1;
    minimum = 0;
    maximum = null;
    var close = pattern.IndexOf('}', start + 1);
    if (close < 0 || !int.TryParse(pattern.AsSpan(start + 1, close - start - 1).ToString().Split(',')[0], out minimum))
    {
      return false;
    }
    var body = pattern[(start + 1)..close];
    if (body.Contains(',', StringComparison.Ordinal))
    {
      var bounds = body.Split(',');
      if (bounds.Length != 2 || bounds[1].Length > 0 && !int.TryParse(bounds[1], out _))
      {
        return false;
      }
      if (bounds[1].Length > 0)
      {
        maximum = int.Parse(bounds[1], System.Globalization.CultureInfo.InvariantCulture);
        if (maximum.Value < minimum)
        {
          return false;
        }
      }
    }
    else
    {
      maximum = minimum;
    }
    end = close;
    return true;
  }

  private static bool IsEscaped(string pattern, int index)
  {
    var slashes = 0;
    for (var cursor = index - 1; cursor >= 0 && pattern[cursor] == '\\'; cursor--)
    {
      slashes++;
    }
    return slashes % 2 != 0;
  }

  private static (string Pattern, string Token) Apply(string pattern, int start, int length, string replacement, string token) =>
      (pattern[..start] + replacement + pattern[(start + length)..], token);

  private sealed class RegexEditComparer : IEqualityComparer<(int Start, int Length, string Replacement)>
  {
    public bool Equals((int Start, int Length, string Replacement) x, (int Start, int Length, string Replacement) y) =>
        x.Start == y.Start && x.Length == y.Length && string.Equals(x.Replacement, y.Replacement, StringComparison.Ordinal);
    public int GetHashCode((int Start, int Length, string Replacement) value) => HashCode.Combine(value.Start, value.Length, value.Replacement);
  }
}
