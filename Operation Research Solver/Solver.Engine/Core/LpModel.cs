namespace Solver.Enginer.Engine.Core;

public sealed class LpModel
{
    public OptimizeDirection Direction { get; }
    public List<Variable> Variables { get; } = new();
    public List<Constraint> Constraints { get; } = new();

    public LpModel(OptimizeDirection dir) => Direction = dir;

    public int NumVars => Variables.Count;
    public int NumConstraints => Constraints.Count;
}
