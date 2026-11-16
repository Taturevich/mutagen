using System.Reflection;
using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollection;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollector.InProcDataCollector;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.InProcDataCollector;

namespace MutationFramework.CoverageCollector;

[DataCollectorFriendlyName("MutagenPerTestCoverage")]
[DataCollectorTypeUri("https://mutagen.dev/test-coverage/1")]
public sealed class MutationCoverageCollector : InProcDataCollection
{
  public const string FriendlyName = "MutagenPerTestCoverage";
  public const string TypeUri = "https://mutagen.dev/test-coverage/1";
  public const string CoverageProperty = "Mutagen.PerTestCoverage";
  public const string ErrorProperty = "Mutagen.PerTestCoverageError";

  private IDataCollectionSink? _sink;
  private readonly ConcurrentDictionary<Assembly, Func<int[]>> _coverageReaders = new();
  private AppDomain? _testDomain;

  public void Initialize(IDataCollectionSink dataCollectionSink)
  {
    _sink = dataCollectionSink;
    _testDomain = AppDomain.CurrentDomain;
    _testDomain.AssemblyLoad += OnAssemblyLoad;
    foreach (var assembly in _testDomain.GetAssemblies())
    {
      CacheCoverageReader(assembly);
    }
  }

  public void TestSessionStart(TestSessionStartArgs testSessionStartArgs) { }
  public void TestCaseStart(TestCaseStartArgs testCaseStartArgs) { }
  public void TestSessionEnd(TestSessionEndArgs testSessionEndArgs)
  {
    if (_testDomain is not null)
    {
      _testDomain.AssemblyLoad -= OnAssemblyLoad;
      _testDomain = null;
    }
  }

  public void TestCaseEnd(TestCaseEndArgs testCaseEndArgs)
  {
    try
    {
      var ids = new SortedSet<int>();
      foreach (var readCoverage in _coverageReaders.Values)
      {
        ids.UnionWith(readCoverage());
      }

      var serialized = string.Join(",", ids.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
      _sink?.SendData(testCaseEndArgs.DataCollectionContext, CoverageProperty, serialized.Length == 0 ? ";" : serialized);
    }
    catch (Exception exception)
    {
      _sink?.SendData(testCaseEndArgs.DataCollectionContext, ErrorProperty,
          exception.ToString());
    }
  }

  private void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args) => CacheCoverageReader(args.LoadedAssembly);

  private void CacheCoverageReader(Assembly assembly)
  {
    try
    {
      var runtimeType = assembly.GetType("MutationRuntime", throwOnError: false, ignoreCase: false);
      var getCoverage = runtimeType?.GetMethod("GetCoverageData", BindingFlags.Public | BindingFlags.Static,
          binder: null, types: Type.EmptyTypes, modifiers: null);
      if (getCoverage?.ReturnType == typeof(int[]) && getCoverage.IsStatic)
      {
        _coverageReaders.TryAdd(assembly, getCoverage.CreateDelegate<Func<int[]>>());
      }
    }
    catch (NotSupportedException)
    {
    }
  }
}
