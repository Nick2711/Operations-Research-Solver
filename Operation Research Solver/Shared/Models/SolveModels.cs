// Shared/Models/SolveModels.cs
namespace Shared.Models;

public sealed class SolveRequest
{
    public string Algorithm { get; set; } = "Primal Simplex";
    public string ModelText { get; set; } = "";
    public SolveSettings Settings { get; set; } = new();
}

public sealed class SolveSettings
{
    public int MaxIterations { get; set; } = 5000;    // for simplex/revised (hook up when you expose in engine)
    public int MaxNodes { get; set; } = 2000;         // for B&B (hook up later)
    public bool Verbose { get; set; } = true;         // show all tableaux/logs
    public int TimeLimitSeconds { get; set; } = 60;   // server-side time limit
}

public sealed class SolveResponse
{
    public bool Success { get; set; }
    public bool Unbounded { get; set; }
    public bool Infeasible { get; set; }
    public string OutputText { get; set; } = "";
    public double? Objective { get; set; }
    public string? SolutionSummary { get; set; }
    public long RuntimeMs { get; set; }
}
