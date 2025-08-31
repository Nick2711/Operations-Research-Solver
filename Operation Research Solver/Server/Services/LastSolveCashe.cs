using Solver.Engine.Simplex;

public interface ILastSolveCache
{
    SolverResult? LastResult { get; set; }
}

public sealed class LastSolveCache : ILastSolveCache
{
    public SolverResult? LastResult { get; set; }
}
