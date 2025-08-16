using System.Globalization;

namespace Solver.Engine.IO;

public static class OutputWriter
{
    public static void WriteAll(string path, IEnumerable<string> blocks)
    {
        using var sw = new StreamWriter(path);
        foreach (var b in blocks)
        {
            // already formatted to 3 decimals in solver logs
            sw.WriteLine(b);
        }
    }
}


