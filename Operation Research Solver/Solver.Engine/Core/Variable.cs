namespace Solver.Enginer.Engine.Core;

public sealed class Variable
{
    public string Name { get; }
    public double ObjectiveCoeff { get; set; }
    public SignRestriction Sign { get; set; }

    public Variable(string name, double c, SignRestriction sign)
    {
        Name = name;
        ObjectiveCoeff = c;
        Sign = sign;
    }
}
