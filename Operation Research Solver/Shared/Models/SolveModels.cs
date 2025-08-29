namespace Shared.Models;

public enum Algorithm
{
    PrimalSimplex,
    RevisedSimplex = 1,
    BranchAndBound = 2,
    Knapsack01 = 3,
    CuttingPlane = 4,
    // add more later...
}

public sealed class SolveRequest
{
    public Algorithm Algorithm { get; set; } = Algorithm.PrimalSimplex;
    public string ModelText { get; set; } = "";
    public SolveSettings Settings { get; set; } = new();
}

public sealed class SolveSettings
{
    public int MaxIterations { get; set; } = 5000;
    public int MaxNodes { get; set; } = 2000;
    public bool Verbose { get; set; } = true;
    public int TimeLimitSeconds { get; set; } = 60;
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
