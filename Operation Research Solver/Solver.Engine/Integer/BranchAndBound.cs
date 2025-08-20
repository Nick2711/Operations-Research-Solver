using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Solver.Engine.Core;
using Solver.Engine.Simplex;

namespace Solver.Engine.Integer
{
    public sealed class BranchAndBoundSimplexSolver : ISolver
    {
        public string Name => "Branch & Bound (Tableau)";

        private const double EPS = 1e-9;
        private const double INT_TOL = 1e-6;

        // 🔁 Use Dual Simplex for every LP relaxation (root + all nodes)
        private readonly DualSimplexSolver _dual = new();

        // NodeState: Holds information for each node during the Branch & Bound search
        private sealed class NodeState
        {
            public int Id { get; }
            public int Depth { get; }
            public string Path { get; }
            public List<(int j, double? lb, double? ub)> Bounds { get; }

            public NodeState(int id, int depth, string path, List<(int j, double? lb, double? ub)> bounds)
            {
                Id = id;
                Depth = depth;
                Path = path;
                Bounds = bounds;
            }
        }

        // Candidate: Holds information about a candidate solution
        private sealed class Candidate
        {
            public int NodeId { get; }
            public int Depth { get; }
            public string Path { get; }
            public double Objective { get; }   // user objective (correct sign)
            public double[] X { get; }
            public bool IsIntegral { get; }

            public Candidate(int nodeId, int depth, string path, double objective, double[] x, bool isIntegral)
            {
                NodeId = nodeId;
                Depth = depth;
                Path = path;
                Objective = objective;
                X = x;
                IsIntegral = isIntegral;
            }
        }

        // BranchOutcome: Used to track the branch outcome for each node
        private sealed class BranchOutcome
        {
            public int ParentId;
            public string ParentPath = "";
            public string VarName = "";
            public double Xval, Floor, Ceil;
            public int LeftId, RightId;          // left: ≤ floor, right: ≥ ceil
            public double? LeftZ, RightZ;        // user objective
            public string LeftStatus = "", RightStatus = ""; // infeasible / fractional / integral
        }

        public SolverResult Solve(LpModel model)
        {
            var log = new List<string>();
            log.Add("== Branch & Bound (Tableau) ==");
            log.Add($"Direction: {model.Direction} | Vars: {model.NumVars} | Cons: {model.NumConstraints}");
            log.Add("LP relaxations solved with: Dual Simplex");

            var intIdx = DetectIntegerIndices(model).ToArray();
            if (intIdx.Length == 0)
            {
                log.Add("No integer/binary variables detected. Solving as pure LP (Dual Simplex).");
                var pure = _dual.Solve(model);
                pure.Log.Insert(0, "== Root LP (no integer vars) ==");
                MergeInto(log, pure.Log);
                // Return INTERNAL objective (consistent with other solvers)
                return new SolverResult(pure.Success, pure.ObjectiveValue, pure.X, log, pure.Unbounded, pure.Infeasible);
            }

            log.Add($"Integer variable indices (1-based): {string.Join(", ", intIdx.Select(i => (i + 1).ToString()))}");

            // Root relaxation
            log.Add("\n== Root LP Relaxation (Dual) ==");
            var rootLP = _dual.Solve(model);
            PrefixInto(log, rootLP.Log, "- ");

            if (rootLP.Infeasible)
            {
                log.Add("Root LP infeasible. No solution.");
                return new SolverResult(false, 0, Array.Empty<double>(), log, infeasible: true);
            }
            if (rootLP.Unbounded)
            {
                log.Add("Root LP unbounded. Cannot proceed with B&B.");
                return new SolverResult(false, 0, Array.Empty<double>(), log, unbounded: true);
            }

            // Incumbent (best integer so far), stored as USER objective
            var hasIncumbent = false;
            double incumbentZ = double.NaN;
            double[] incumbentX = Array.Empty<double>();
            string incumbentPath = "";

            var candidates = new List<Candidate>();

            // If the LP relaxation already satisfies integrality
            if (IsIntegral(rootLP.X, intIdx))
            {
                var rootZ = UserObjective(model.Direction, rootLP.ObjectiveValue);
                hasIncumbent = true;
                incumbentZ = rootZ;
                incumbentX = RoundToIntegers(rootLP.X, intIdx);
                incumbentPath = "root";
                candidates.Add(new Candidate(0, 0, "root", rootZ, rootLP.X, true));

                log.Add($"\nIntegral at root → Objective {F(rootZ)}");
                log.Add("\n== Candidates Summary ==");
                foreach (var line in RenderCandidates(candidates, hasIncumbent, incumbentZ, incumbentPath))
                    log.Add(line);

                log.Add($"\n== Best Integer Solution ==\nObjective: {F(incumbentZ)}");
                log.Add($"Backtrace: {incumbentPath}");

                // Display decision variables for the best candidate
                log.Add("\n== Decision Variables (Best Candidate) ==");
                for (int i = 0; i < incumbentX.Length; i++)
                {
                    var varName = model.Variables[i].Name ?? $"x{i + 1}";
                    log.Add($"{varName} = {F(incumbentX[i])}");
                }

                // Return INTERNAL objective
                return new SolverResult(true, InternalObjective(model.Direction, incumbentZ), incumbentX, log);
            }

            // DFS stack
            int nextId = 1;
            var stack = new Stack<NodeState>();
            stack.Push(new NodeState(0, 0, "root", new List<(int j, double? lb, double? ub)>()));

            // For "branch outcome" summaries
            var branchByParent = new Dictionary<int, BranchOutcome>();
            var parentOf = new Dictionary<int, int>(); // childId -> parentId

            while (stack.Count > 0)
            {
                var node = stack.Pop();

                // Build a child LP with all branch rows (bounds)
                var childModel = CloneModel(model);
                foreach (var (j, lb, ub) in node.Bounds)
                {
                    if (lb.HasValue) AddBoundRow(childModel, j, Relation.GreaterOrEqual, lb.Value); // x_j ≥ lb
                    if (ub.HasValue) AddBoundRow(childModel, j, Relation.LessOrEqual, ub.Value); // x_j ≤ ub
                }

                log.Add($"\n== Node {node.Id} (depth {node.Depth}) :: {node.Path} ==");
                if (node.Bounds.Count > 0)
                {
                    var added = node.Bounds.Select(b =>
                    {
                        var nm = childModel.Variables[b.j].Name ?? $"x{b.j + 1}";
                        if (b.lb.HasValue && b.ub.HasValue) return $"{nm} ∈ [{F(b.lb.Value)},{F(b.ub.Value)}]";
                        if (b.lb.HasValue) return $"{nm} ≥ {F(b.lb.Value)}";
                        if (b.ub.HasValue) return $"{nm} ≤ {F(b.ub.Value)}";
                        return "";
                    });
                    log.Add("Added bound rows: " + string.Join(", ", added));
                }

                // 🔁 Solve LP relaxation at node with Dual Simplex
                var res = _dual.Solve(childModel);
                PrefixInto(log, res.Log, "- ");

                if (res.Infeasible) { log.Add("→ Pruned (infeasible)."); continue; }
                if (res.Unbounded) { log.Add("→ Pruned (unbounded relaxation)."); continue; }

                // Convert to user objective before logging/pruning
                var nodeZ = UserObjective(childModel.Direction, res.ObjectiveValue);
                bool integralHere = IsIntegral(res.X, intIdx);

                candidates.Add(new Candidate(node.Id, node.Depth, node.Path, nodeZ, res.X, integralHere));

                // If this node is part of a branch pair, store its outcome and print summary when both sides are known.
                if (parentOf.TryGetValue(node.Id, out var pId) && branchByParent.TryGetValue(pId, out var br))
                {
                    string status = res.Infeasible ? "infeasible" : (integralHere ? "integral" : "fractional");
                    if (node.Id == br.LeftId) { br.LeftZ = nodeZ; br.LeftStatus = status; }
                    if (node.Id == br.RightId) { br.RightZ = nodeZ; br.RightStatus = status; }

                    if (br.LeftStatus != "" && br.RightStatus != "")
                    {
                        log.Add($"\n-- Branch outcome @ Node {pId} ({br.ParentPath}) on {br.VarName} = {F(br.Xval)} --");
                        log.Add($"   Left  ({br.VarName} ≤ {F(br.Floor)}): {br.LeftStatus}" + (br.LeftZ.HasValue ? $"  z={F(br.LeftZ.Value)}" : ""));
                        log.Add($"   Right ({br.VarName} ≥ {F(br.Ceil)}):  {br.RightStatus}" + (br.RightZ.HasValue ? $"  z={F(br.RightZ.Value)}" : ""));

                        string bestSide = "?";
                        if (childModel.Direction == OptimizeDirection.Min)
                        {
                            if (br.LeftZ.HasValue && br.RightZ.HasValue)
                                bestSide = br.LeftZ.Value <= br.RightZ.Value ? "Left" : "Right";
                            else if (br.LeftZ.HasValue) bestSide = "Left";
                            else if (br.RightZ.HasValue) bestSide = "Right";
                        }
                        else
                        {
                            if (br.LeftZ.HasValue && br.RightZ.HasValue)
                                bestSide = br.LeftZ.Value >= br.RightZ.Value ? "Left" : "Right";
                            else if (br.LeftZ.HasValue) bestSide = "Left";
                            else if (br.RightZ.HasValue) bestSide = "Right";
                        }
                        log.Add($"   ⇒ Best side ({childModel.Direction}): {bestSide}");
                    }
                }

                // Prune by incumbent bound using USER objective
                if (hasIncumbent)
                {
                    if (childModel.Direction == OptimizeDirection.Max && nodeZ <= incumbentZ + EPS)
                    { log.Add($"→ Pruned by bound (max): node z={F(nodeZ)} ≤ incumbent {F(incumbentZ)}."); continue; }

                    if (childModel.Direction == OptimizeDirection.Min && nodeZ >= incumbentZ - EPS)
                    { log.Add($"→ Pruned by bound (min): node z={F(nodeZ)} ≥ incumbent {F(incumbentZ)}."); continue; }
                }

                if (integralHere)
                {
                    var xi = RoundToIntegers(res.X, intIdx);

                    bool improves =
                        !hasIncumbent ||
                        (childModel.Direction == OptimizeDirection.Max && nodeZ > incumbentZ + EPS) ||
                        (childModel.Direction == OptimizeDirection.Min && nodeZ < incumbentZ - EPS);

                    if (improves)
                    {
                        hasIncumbent = true;
                        incumbentZ = nodeZ;              // store user objective
                        incumbentX = xi;
                        incumbentPath = node.Path;
                        log.Add($"→ New incumbent at node {node.Id}: z = {F(incumbentZ)} | path: {incumbentPath}");
                    }
                    else
                    {
                        log.Add("→ Integral but not improving incumbent.");
                    }
                    continue; // do not branch further from integral
                }

                // Choose fractional var
                int jBranch = ChooseBranchVar(res.X, intIdx);
                if (jBranch < 0)
                {
                    log.Add("→ No fractional var found unexpectedly. Skipping branch.");
                    continue;
                }

                double xj = res.X[jBranch];
                double flo = Math.Floor(xj + 1e-12);
                double cei = Math.Ceiling(xj - 1e-12);
                var varName = childModel.Variables[jBranch].Name ?? $"x{jBranch + 1}";

                log.Add($"Branching on {varName} = {F(xj)} → ≤ {F(flo)} OR ≥ {F(cei)}");

                // Right (≥ ceil) pushed first so Left is explored first (DFS)
                var rightBounds = CopyBounds(node.Bounds);
                rightBounds.Add((jBranch, lb: cei, ub: (double?)null));
                int rightId = nextId++;
                stack.Push(new NodeState(rightId, node.Depth + 1, node.Path + $" → {varName} ≥ {F(cei)}", rightBounds));

                var leftBounds = CopyBounds(node.Bounds);
                leftBounds.Add((jBranch, lb: (double?)null, ub: flo));
                int leftId = nextId++;
                stack.Push(new NodeState(leftId, node.Depth + 1, node.Path + $" → {varName} ≤ {F(flo)}", leftBounds));

                // Track the pair for the "branch outcome" summary later
                branchByParent[node.Id] = new BranchOutcome
                {
                    ParentId = node.Id,
                    ParentPath = node.Path,
                    VarName = varName,
                    Xval = xj,
                    Floor = flo,
                    Ceil = cei,
                    LeftId = leftId,
                    RightId = rightId
                };
                parentOf[leftId] = node.Id;
                parentOf[rightId] = node.Id;
            }

            // Wrap up
            log.Add("\n== Candidates Summary ==");
            foreach (var line in RenderCandidates(candidates, hasIncumbent, incumbentZ, incumbentPath))
                log.Add(line);

            if (!hasIncumbent)
            {
                log.Add("No integer feasible solution found.");
                return new SolverResult(false, 0, Array.Empty<double>(), log);
            }

            log.Add($"\n== Best Integer Solution ==\nObjective: {F(incumbentZ)}");
            log.Add($"Backtrace: {incumbentPath}");

            // Display decision variables for the best candidate
            log.Add("\n== Decision Variables (Best Candidate) ==");
            for (int i = 0; i < incumbentX.Length; i++)
            {
                var varName = model.Variables[i].Name ?? $"x{i + 1}";
                log.Add($"{varName} = {F(incumbentX[i])}");
            }

            // Return INTERNAL objective
            return new SolverResult(true, InternalObjective(model.Direction, incumbentZ), incumbentX, log);
        }

        // ----------------------- helpers -----------------------
        private static IEnumerable<int> DetectIntegerIndices(LpModel model)
        {
            for (int j = 0; j < model.NumVars; j++)
            {
                var s = model.Variables[j].Sign;
                if (s == SignRestriction.Int || s == SignRestriction.Bin)
                    yield return j;
            }
        }

        private static LpModel CloneModel(LpModel src)
        {
            var m = new LpModel(src.Direction);
            foreach (var v in src.Variables)
                m.Variables.Add(new Variable(v.Name, v.ObjectiveCoeff, v.Sign));
            foreach (var c in src.Constraints)
                m.Constraints.Add(new Constraint((double[])c.Coeffs.Clone(), c.Relation, c.Rhs));
            return m;
        }

        private static void AddBoundRow(LpModel m, int j, Relation rel, double rhs)
        {
            var row = new double[m.NumVars];
            row[j] = 1.0; // coefficient on x_j
            m.Constraints.Add(new Constraint(row, rel, rhs));
        }

        private static List<(int j, double? lb, double? ub)> CopyBounds(List<(int j, double? lb, double? ub)> b)
            => b.Select(t => (t.j, t.lb, t.ub)).ToList();

        private static bool IsIntegral(double[] x, int[] intIdx)
        {
            foreach (var j in intIdx)
            {
                double frac = Math.Abs(x[j] - Math.Round(x[j]));
                if (frac > INT_TOL) return false;
            }
            return true;
        }

        private static double[] RoundToIntegers(double[] x, int[] intIdx)
        {
            var y = (double[])x.Clone();
            foreach (var j in intIdx) y[j] = Math.Round(y[j]);
            return y;
        }

        /// <summary>Most fractional among the integer vars.</summary>
        private static int ChooseBranchVar(double[] x, int[] intIdx)
        {
            int best = -1;
            double bestFrac = -1.0;
            foreach (var j in intIdx)
            {
                double fj = Math.Abs(x[j] - Math.Round(x[j]));
                if (fj <= INT_TOL) continue;
                if (fj > bestFrac) { bestFrac = fj; best = j; }
            }
            return best;
        }

        private static void PrefixInto(List<string> target, IEnumerable<string> lines, string prefix)
        {
            foreach (var line in lines) target.Add(prefix + line);
        }

        private static void MergeInto(List<string> target, IEnumerable<string> lines)
        {
            foreach (var line in lines) target.Add(line);
        }

        private static IEnumerable<string> RenderCandidates(List<Candidate> candidates, bool hasIncumbent, double incumbentZ, string incumbentPath)
        {
            if (candidates.Count == 0) yield break;

            foreach (var c in candidates.OrderBy(c => c.NodeId))
            {
                var status = c.IsIntegral ? "INTEGRAL" : "fractional";
                yield return $"[Node {c.NodeId} | depth {c.Depth}] {status} z={F(c.Objective)} :: {c.Path}";
            }

            if (hasIncumbent)
                yield return $"Best: z={F(incumbentZ)} via {incumbentPath}";
        }

        private static string RenderSolutionVector(LpModel model, double[] x)
        {
            var parts = new List<string>();
            for (int k = 0; k < x.Length; k++)
            {
                var name = model.Variables[k].Name ?? $"x{k + 1}";
                parts.Add($"{name}={F(x[k])}");
            }
            return "x* (rounded ints on int/bin vars): [" + string.Join(", ", parts) + "]";
        }

        // Convert solver's internal objective to the user's real objective
        // (The simplex flips MIN → MAX; this flips back for reporting & pruning.)
        private static double UserObjective(OptimizeDirection dir, double solverObj)
            => dir == OptimizeDirection.Max ? solverObj : -solverObj;

        // Convert USER objective back to INTERNAL sign for returning SolverResult
        private static double InternalObjective(OptimizeDirection dir, double userObj)
            => dir == OptimizeDirection.Max ? userObj : -userObj;

        private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
