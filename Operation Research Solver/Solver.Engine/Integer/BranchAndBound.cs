using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Solver.Engine.Core;
using Solver.Engine.Simplex;
using static Solver.Engine.Core.Numeric;

namespace Solver.Engine.Integer
{
    /// <summary>
    /// Branch & Bound that solves LP relaxations with the Primal Simplex (Tableau),
    /// logs FULL tableaux at every node, and branches by adding bound constraints
    /// as new rows (so you can back-trace all tables).
    /// </summary>
    public sealed class BranchAndBoundSimplexSolver : ISolver
    {
        public string Name => "Branch & Bound (Tableau)";

        private sealed class Node
        {
            public int Id { get; init; }
            public int Depth { get; init; }
            public string Path { get; init; } = "root";
            public List<(int varIndex, double? lb, double? ub)> Bounds { get; } = new();
        }

        private readonly PrimalSimplexSolver _lp = new();

        public SolverResult Solve(LpModel model)
        {
            var log = new List<string>();
            log.Add("— Branch & Bound (Tableau) — LP relaxation via Primal Simplex");

            // collect integer/binary decision vars we will branch on
            var intVars = new List<int>();
            for (int j = 0; j < model.NumVars; j++)
            {
                var s = model.Variables[j].Sign;
                if (s == SignRestriction.Int || s == SignRestriction.Bin)
                    intVars.Add(j);
            }

            if (intVars.Count == 0)
            {
                log.Add("No Int/Bin variables — solving as a plain LP.");
                var lp = _lp.Solve(model);
                return new SolverResult(lp.Success, lp.ObjectiveValue, lp.X, log.Concat(lp.Log ?? new()).ToList(), lp.Unbounded, lp.Infeasible);
            }

            int nextId = 1;
            var stack = new Stack<Node>();
            stack.Push(new Node { Id = nextId++, Depth = 0 });

            bool hasIncumbent = false;
            double bestObjMax = double.NegativeInfinity; // compare in MAX sense; controller flips for MIN
            double[]? bestX = null;
            string bestPath = "root";
            int explored = 0;

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                explored++;

                // Build child model by cloning and appending bound rows
                var child = CopyModel(model);
                foreach (var (varIndex, lb, ub) in node.Bounds)
                {
                    if (lb.HasValue)
                        child.Constraints.Add(MakeBoundConstraint(child.NumVars, varIndex, Relation.GreaterOrEqual, lb.Value));
                    if (ub.HasValue)
                        child.Constraints.Add(MakeBoundConstraint(child.NumVars, varIndex, Relation.LessOrEqual, ub.Value));
                }

                // Solve LP relaxation for this node with FULL tableau logs
                var lpRes = _lp.Solve(child);

                AppendNodeHeader(log, node, child);
                if (lpRes.Log is { Count: > 0 })
                {
                    foreach (var line in lpRes.Log)
                        log.Add(Indent(line, node.Depth));
                }

                if (!lpRes.Success)
                {
                    log.Add(Indent("prune: LP infeasible/unbounded at this node.", node.Depth));
                    continue;
                }

                double nodeBoundMax = lpRes.ObjectiveValue; // already in MAX sense
                if (hasIncumbent && nodeBoundMax <= bestObjMax + EPS)
                {
                    log.Add(Indent($"prune: bound {nodeBoundMax:0.###} ≤ incumbent {bestObjMax:0.###}.", node.Depth));
                    continue;
                }

                var x = lpRes.X ?? Array.Empty<double>();
                int jBranch = FindBranchVar(x, intVars, model);

                if (jBranch == -1)
                {
                    log.Add(Indent("integer feasible.", node.Depth));
                    if (!hasIncumbent || nodeBoundMax > bestObjMax + EPS)
                    {
                        hasIncumbent = true;
                        bestObjMax = nodeBoundMax;
                        bestX = (double[])x.Clone();
                        bestPath = node.Path;
                        log.Add(Indent($"incumbent updated (max-sense): {bestObjMax:0.###}", node.Depth));
                    }
                    continue;
                }

                double v = (jBranch < x.Length) ? x[jBranch] : 0.0;
                double fl = Math.Floor(v);
                double ce = Math.Ceiling(v);
                string vname = $"x{jBranch + 1}";
                log.Add(Indent($"branch on {vname} = {v.ToString("0.###", CultureInfo.InvariantCulture)}  →  {vname} ≤ {fl},  {vname} ≥ {ce}", node.Depth));

                // Right (≥ ceil), Left (≤ floor)
                var right = new Node
                {
                    Id = nextId++,
                    Depth = node.Depth + 1,
                    Path = $"{node.Path} ∧ ({vname} ≥ {ce})"
                };
                right.Bounds.AddRange(node.Bounds);
                right.Bounds.Add((jBranch, ce, null));

                var left = new Node
                {
                    Id = nextId++,
                    Depth = node.Depth + 1,
                    Path = $"{node.Path} ∧ ({vname} ≤ {fl})"
                };
                left.Bounds.AddRange(node.Bounds);
                left.Bounds.Add((jBranch, null, fl));

                // DFS: explore right then left (swap if you want the other order)
                stack.Push(left);
                stack.Push(right);
            }

            if (!hasIncumbent)
            {
                log.Add($"No integer solution found after exploring {explored} node(s).");
                return new SolverResult(false, 0, Array.Empty<double>(), log, infeasible: true);
            }

            log.Add($"Done. Explored {explored} node(s).");
            log.Add($"Incumbent path: {bestPath}");
            return new SolverResult(true, bestObjMax, bestX ?? Array.Empty<double>(), log);
        }

        // ---- helpers ----

        private static int FindBranchVar(double[] x, List<int> intVars, LpModel model)
        {
            int arg = -1; double best = 0.0;
            foreach (var j in intVars)
            {
                double v = (j < x.Length) ? x[j] : 0.0;
                if (model.Variables[j].Sign == SignRestriction.Bin)
                {
                    if (v < -EPS || v > 1.0 + EPS) return j; // outside [0,1]
                    double dist = Math.Min(Math.Abs(v - 0.0), Math.Abs(v - 1.0));
                    if (dist > 1e-7 && dist > best) { best = dist; arg = j; }
                }
                else
                {
                    double frac = Math.Abs(v - Math.Round(v));
                    if (frac > 1e-7 && frac > best) { best = frac; arg = j; }
                }
            }
            return arg; // -1 => all integral
        }

        private static Constraint MakeBoundConstraint(int numVars, int j, Relation rel, double rhs)
        {
            var coeffs = new double[numVars];
            coeffs[j] = 1.0;
            // Your Constraint ctor is (double[] coeffs, Relation rel, double rhs)
            return new Constraint(coeffs, rel, rhs);
        }

        private static LpModel CopyModel(LpModel src)
        {
            // LpModel requires a direction in ctor and exposes read-only properties
            var m = new LpModel(src.Direction);

            // Append variables (Variable(string name, double objCoeff, SignRestriction sign))
            for (int i = 0; i < src.Variables.Count; i++)
            {
                var v = src.Variables[i];
                m.Variables.Add(new Variable(v.Name, v.ObjectiveCoeff, v.Sign));
            }

            // Append constraints (Constraint(double[] coeffs, Relation rel, double rhs))
            for (int i = 0; i < src.Constraints.Count; i++)
            {
                var ct = src.Constraints[i];
                m.Constraints.Add(new Constraint((double[])ct.Coeffs.Clone(), ct.Relation, ct.Rhs));
            }

            return m;
        }

        private static void AppendNodeHeader(List<string> log, Node node, LpModel child)
        {
            log.Add("");
            log.Add($"=== Node #{node.Id} (depth {node.Depth}) — {node.Path} ===");

            if (node.Bounds.Count > 0)
            {
                string bounds = string.Join(", ",
                    node.Bounds.Select(b =>
                    {
                        string nm = $"x{b.varIndex + 1}";
                        if (b.lb.HasValue && b.ub.HasValue) return $"{nm} ∈ [{b.lb.Value},{b.ub.Value}]";
                        if (b.lb.HasValue) return $"{nm} ≥ {b.lb.Value}";
                        if (b.ub.HasValue) return $"{nm} ≤ {b.ub.Value}";
                        return "";
                    }));
                log.Add($"Added bounds this path: {bounds}");
            }

            log.Add($"Model at node: vars={child.NumVars}, constraints={child.Constraints.Count}");
        }

        private static string Indent(string s, int depth) => new string(' ', Math.Max(0, depth) * 2) + s;
    }
}
