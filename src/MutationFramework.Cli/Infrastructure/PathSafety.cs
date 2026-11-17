namespace MutationFramework.Sdk;

internal static class PathSafety
{
  internal static bool IsWithinDirectory(string path, string directory)
  {
    var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
    return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
  }

}
