using Solver.Engine.Core;

namespace Solver.Enginer.Engine.Simplex;

public sealed class RevisedSimplexSolverStub : ISolver
{
    public string Name => "Revised Primal Simplex (Stub)";

    public SolverResult Solve(LpModel model)
        => new(false, 0, Array.Empty<double>(), new List<string> { "Revised Simplex not yet implemented." });
}
