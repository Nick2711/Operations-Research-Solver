namespace Solver.Engine.Core;

public sealed class ParseResult
{
    public LpModel Model { get; }
    public List<string> Warnings { get; } = new();
    public ParseResult(LpModel model) => Model = model;
}


