using MutationFramework.Sdk;
var result = await MutationEngine.RunAsync(new MutationOptions { ProjectPath = "samples/SampleApplication/SampleApplication.csproj", TestProjects = ["samples/SampleApplication.Tests/SampleApplication.Tests.csproj"], MaxWorkers = 4, OutputDirectory = "mutation-results-sdk" });
Console.WriteLine($"Killed: {result.KilledCount}; Survived: {result.SurvivedCount}; Score: {result.MutationScore}");
