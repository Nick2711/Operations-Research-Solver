using Solver.Engine.Simplex;

public interface ILastSolveCache
{
    SolverResult? LastResult { get; set; }
    string? OriginalModelText { get; set; }   // ✅ add this
}

public sealed class LastSolveCache : ILastSolveCache
{
    public SolverResult? LastResult { get; set; }
    public string? OriginalModelText { get; set; }  // ✅ add this
}
