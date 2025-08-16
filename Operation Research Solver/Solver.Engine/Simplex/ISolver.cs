using Solver.Engine.Core;

namespace Solver.Engine.Simplex;

public interface ISolver
{
    string Name { get; }
    SolverResult Solve(LpModel model);
}

public sealed class SolverResult
{
    public bool Success { get; }
    public bool Unbounded { get; }
    public bool Infeasible { get; }
    public double ObjectiveValue { get; }
    public double[] X { get; }           // values for decision vars (continuous relaxation)
    public List<string> Log { get; }     // canonical form + all iteration tables

    public SolverResult(bool success, double z, double[] x, List<string>? log = null,
                        bool unbounded = false, bool infeasible = false)
    {
        Success = success; ObjectiveValue = z; X = x;
        Unbounded = unbounded; Infeasible = infeasible;
        Log = log ?? new();
    }
}


