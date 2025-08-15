namespace Solver.Enginer.Engine.Core;

public sealed class Constraint
{
    public double[] Coeffs { get; } // aligned with variable order
    public Relation Relation { get; }
    public double Rhs { get; }

    public Constraint(double[] coeffs, Relation relation, double rhs)
    {
        Coeffs = coeffs;
        Relation = relation;
        Rhs = rhs;
    }
}
