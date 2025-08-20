using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Solver.Engine.Core;
using Solver.Engine.Simplex;
using Solver.Engine.IO;

namespace Solver.Engine.Integer
{
    /// <summary>
    /// Branch & Bound (Tableau) with c8−c9 injection and Dual Simplex re-optimization at each node.
    /// Primal / Revised / Dual solvers are untouched; Dual is only used for the initial root solve (via tableau too).
    /// </summary>
    public sealed class BranchAndBoundSimplexSolver : ISolver
    {
        public string Name => "Branch & Bound (Tableau)";

        private const double EPS = 1e-9;
        private const double INT_TOL = 1e-6;

        // -----------------------------
        // Node bookkeeping
        // -----------------------------
        private sealed class NodeState
        {
            public int Id { get; }
            public int? ParentId { get; }
            public int Depth { get; }
            public string Path { get; }
            public List<(int j, double? lb, double? ub)> Bounds { get; }

            public NodeState(int id, int? parentId, int depth, string path, List<(int j, double? lb, double? ub)> bounds)
            {
                Id = id;
                ParentId = parentId;
                Depth = depth;
                Path = path;
                Bounds = bounds;
            }
        }

        // -----------------------------
        // Compact tableau state we carry across nodes
        // -----------------------------
        private sealed class TableauState
        {
            public CanonicalForm Can = new();      // Phase II mapping
            public double[,] T = new double[0, 0]; // (m+1) x (n+1) tableau (z row at index 0, RHS last col)
            public int[] Basis = Array.Empty<int>();
            public int M;                          // constraints
            public int N;                          // columns (no RHS)
            public int P;                          // # original decision vars
            public string[] ColNames = Array.Empty<string>();
            public string[] RowNames = Array.Empty<string>();
        }

        // -----------------------------
        // PUBLIC ENTRY
        // -----------------------------
        public SolverResult Solve(LpModel model)
        {
            var log = new List<string>();
            log.Add("== Branch & Bound (Tableau) ==");
            log.Add($"Direction: {model.Direction} | Vars: {model.NumVars} | Cons: {model.NumConstraints}");
            log.Add("LP relaxations re-optimized with: Dual Simplex");
            log.Add("Branching uses c8−c9 injection (z-row untouched), then Dual pivots.");

            // Build a reusable Phase II tableau for the root (so we can inject bounds fast).
            var rootState = PhaseIThenPhaseII(model, log);

            // Solve root relaxation (from tableau)
            var rootDual = DualOptimizeFromHere(rootState);
            if (!rootDual.success && rootDual.infeasible)
            {
                log.Add("Root LP is infeasible → problem infeasible.");
                return new SolverResult(false, 0, Array.Empty<double>(), log, infeasible: true);
            }
            if (!rootDual.success && rootDual.unbounded)
            {
                log.Add("Root LP is unbounded → MILP is unbounded or infeasible.");
                return new SolverResult(false, 0, Array.Empty<double>(), log, unbounded: true);
            }

            double rootZ = rootDual.z;
            double[] rootX = rootDual.xDecision;

            // Integrality set
            var intIdx0 = ExtractIntegerIndices(model);
            var intIdx = intIdx0.Length == 0
                ? Enumerable.Range(0, Math.Min(model.NumVars, rootX.Length)).ToArray()
                : intIdx0;

            // If root already integral, done.
            if (IsIntegral(rootX, intIdx))
            {
                log.Add("Root LP solution is already integer → DONE.");
                return new SolverResult(true, rootZ, rootX, log);
            }

            // Incumbent
            double incumbentValue = IsMaximization(model.Direction)
                ? double.NegativeInfinity : double.PositiveInfinity;
            double[] incumbentX = Array.Empty<double>();
            bool haveIncumbent = false;

            // DFS
            var stack = new Stack<NodeState>();
            int nextId = 1;
            var rootNode = new NodeState(id: 0, parentId: null, depth: 0, path: "root",
                                         bounds: new List<(int j, double? lb, double? ub)>());
            stack.Push(rootNode);

            // Store solved tableau at each node so children can inject further
            var nodeTableaux = new Dictionary<int, TableauState>();
            var rootSolved = CloneTableau(rootState); // already dual-optimal
            nodeTableaux[rootNode.Id] = rootSolved;

            bool WorseThanIncumbent(double nodeBound)
            {
                if (!haveIncumbent) return false;
                return IsMaximization(model.Direction)
                    ? nodeBound <= incumbentValue - 1e-12
                    : nodeBound >= incumbentValue + 1e-12;
            }

            while (stack.Count > 0)
            {
                var node = stack.Pop();

                // Base tableau = parent's solved tableau
                TableauState parentState = node.ParentId == null
                    ? nodeTableaux[0]
                    : nodeTableaux[node.ParentId.Value];

                var work = CloneTableau(parentState);

                // Inject each accumulated bound with c8−c9 (one new row/col per bound), then dual re-opt
                foreach (var (j, lb, ub) in node.Bounds)
                {
                    if (lb.HasValue) InjectBranchRow(work, j, isUpper: false, bound: Math.Ceiling(lb.Value - 1e-12));
                    if (ub.HasValue) InjectBranchRow(work, j, isUpper: true, bound: Math.Floor(ub.Value + 1e-12));
                }

                var re = DualOptimizeFromHere(work);
                if (!re.success && re.infeasible)
                {
                    log.Add($"Prune node {node.Path}: infeasible after injection.");
                    continue;
                }
                if (!re.success && re.unbounded)
                {
                    log.Add($"Node {node.Path}: unbounded relaxation (rare with finite bounds) → prune.");
                    continue;
                }

                var z = re.z;
                var x = re.xDecision;

                // Bounding prune
                if (WorseThanIncumbent(z))
                {
                    log.Add($"Prune node {node.Path}: bound {Fmt(z)} cannot beat incumbent {Fmt(incumbentValue)}.");
                    continue;
                }

                // Integer? update incumbent
                if (IsIntegral(x, intIdx))
                {
                    if (!haveIncumbent || Better(model.Direction, z, incumbentValue))
                    {
                        haveIncumbent = true;
                        incumbentValue = z;
                        incumbentX = x;
                        log.Add($"New incumbent at node {node.Path}: z = {Fmt(z)}.");
                    }
                    continue;
                }

                // Branch
                int jStar = ChooseFractional(x, intIdx, out double v);
                double floorV = Math.Floor(v);
                double ceilV = Math.Ceiling(v);

                // Left: x_j ≤ ⌊v⌋, Right: x_j ≥ ⌈v⌉
                var left = new List<(int j, double? lb, double? ub)>(node.Bounds) { (jStar, null, floorV) };
                var right = new List<(int j, double? lb, double? ub)>(node.Bounds) { (jStar, ceilV, null) };

                var leftNode = new NodeState(nextId++, node.Id, node.Depth + 1, node.Path + ".L", left);
                var rightNode = new NodeState(nextId++, node.Id, node.Depth + 1, node.Path + ".R", right);

                // Save this node's solved tableau for children to branch from
                nodeTableaux[node.Id] = work;

                // DFS order: explore left first
                stack.Push(rightNode);
                stack.Push(leftNode);
            }

            if (!haveIncumbent)
            {
                log.Add("Search finished with no incumbent. Likely infeasible after branching.");
                return new SolverResult(false, 0, Array.Empty<double>(), log, infeasible: true);
            }

            return new SolverResult(true, incumbentValue, incumbentX, log);
        }

        // -----------------------------
        // Helpers: logging / finish
        // -----------------------------
        private static string Fmt(double v) => v.ToString("0.########", CultureInfo.InvariantCulture);

        private static bool Better(OptimizeDirection dir, double a, double b)
            => IsMaximization(dir) ? a > b + 1e-12 : a < b - 1e-12;

        private static bool IsMaximization(OptimizeDirection dir)
        {
            // Works regardless of exact enum member names
            var s = dir.ToString();
            return s.IndexOf("max", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // -----------------------------
        // Helpers: integrality, branching choice
        // -----------------------------
        private static bool IsIntegral(double[] x, int[] intIdx)
        {
            foreach (var j in intIdx)
            {
                if (j < 0 || j >= x.Length) continue;
                if (Math.Abs(x[j] - Math.Round(x[j])) > INT_TOL) return false;
            }
            return true;
        }

        private static int ChooseFractional(double[] x, int[] intIdx, out double value)
        {
            // Heuristic: largest fractional part
            int arg = -1;
            double best = -1.0;
            double vBest = 0.0;

            foreach (var j in intIdx)
            {
                if (j < 0 || j >= x.Length) continue;
                double v = x[j];
                double frac = Math.Abs(v - Math.Round(v));
                if (frac > INT_TOL && frac > best)
                {
                    best = frac; arg = j; vBest = v;
                }
            }

            if (arg == -1)
            {
                foreach (var j in intIdx)
                {
                    if (j < 0 || j >= x.Length) continue;
                    double v = x[j];
                    if (Math.Abs(v - Math.Round(v)) > INT_TOL) { arg = j; vBest = v; break; }
                }
            }
            if (arg == -1) { arg = 0; vBest = x.Length > 0 ? x[0] : 0.0; }
            value = vBest;
            return arg;
        }

        private static int[] ExtractIntegerIndices(LpModel model)
        {
            // Try common property names via reflection; else default to all decision vars
            var t = model.GetType();
            int[] probe(string name)
            {
                var p = t.GetProperty(name);
                if (p == null) return Array.Empty<int>();
                var val = p.GetValue(model);
                if (val is IEnumerable<int> seq) return seq.ToArray();
                return Array.Empty<int>();
            }
            var names = new[] { "IntegerVarIndices", "IntIndices", "IntegerIndices", "IntegerVars", "IntegerVariables" };
            foreach (var n in names)
            {
                var r = probe(n);
                if (r.Length > 0) return r.Select(k => Math.Max(0, k - 1)).ToArray(); // handle 1-based storage
            }
            return Array.Empty<int>();
        }

        // -----------------------------
        // Build a Phase II tableau for the root (handles Phase I if needed)
        // -----------------------------
        private TableauState PhaseIThenPhaseII(LpModel model, List<string> log)
        {
            var canRes = Canonicalizer.ToCanonical(model);
            var can = canRes.Canonical;

            if (can.PhaseIRequired)
            {
                // ---- Phase I (primal) ----
                int m = can.NumRows, n = can.NumCols, width = n + 1;
                var T1 = new double[m + 1, n + 1];

                // Fill constraints
                for (int i = 0; i < m; i++)
                {
                    for (int j = 0; j < n; j++) T1[i + 1, j] = can.A[i, j];
                    T1[i + 1, n] = can.b[i];
                }
                // Phase I objective = - sum(artificials)
                for (int j = 0; j < n; j++) T1[0, j] = -can.cPhaseI[j];

                // Basis detection (identity columns)
                var basis = (can.BasicIdx ?? Array.Empty<int>()).ToArray();
                if (!IsValidIdentityBasis(T1, basis, m, n)) basis = DetectIdentityBasis(T1, m, n);
                if (!IsValidIdentityBasis(T1, basis, m, n))
                    throw new Exception("Phase I: cannot detect a valid identity basis.");

                // Canonicalize z-row for Phase I
                CanonicalizeZRow(T1, basis, can.cPhaseI, m, n, width);

                // Primal simplex (enter: most negative rc; leave: min ratio)
                int it = 0;
                while (it++ < 10000)
                {
                    int enter = -1; double minRC = -1e-18;
                    for (int j = 0; j < n; j++)
                    {
                        double rc = T1[0, j];
                        if (rc < minRC) { minRC = rc; enter = j; }
                    }
                    if (enter == -1) break; // optimal for Phase I

                    int leave = -1; double theta = double.PositiveInfinity;
                    for (int r = 0; r < m; r++)
                    {
                        double a = T1[r + 1, enter];
                        if (a > EPS)
                        {
                            double t = T1[r + 1, n] / a;
                            if (t >= 0 && t < theta) { theta = t; leave = r; }
                        }
                    }
                    if (leave == -1) throw new Exception("Phase I: unbounded ascent.");

                    Pivot(T1, leave + 1, enter, width);
                    basis[leave] = enter;
                }

                if (Math.Abs(T1[0, n]) > 1e-7)
                    throw new Exception("Phase I: infeasible (artificial sum not zero).");

                // Drop artificial columns
                var keep = Enumerable.Range(0, n).Where(j => !can.ArtificialIdx.Contains(j)).ToArray();
                int n2 = keep.Length;
                var T2 = new double[m + 1, n2 + 1];

                for (int i = 0; i < m; i++)
                {
                    for (int jj = 0; jj < n2; jj++) T2[i + 1, jj] = T1[i + 1, keep[jj]];
                    T2[i + 1, n2] = T1[i + 1, n];
                }

                // Phase II c
                var c2 = new double[n2];
                for (int jj = 0; jj < n2; jj++) c2[jj] = can.c[keep[jj]];

                // Map basis into new columns
                var pos = new Dictionary<int, int>();
                for (int jj = 0; jj < n2; jj++) pos[keep[jj]] = jj;

                var basis2 = new int[m];
                for (int i = 0; i < m; i++)
                {
                    int jOld = basis[i];
                    basis2[i] = pos.ContainsKey(jOld) ? pos[jOld] : -1;
                }
                var fixedBasis = IsValidIdentityBasis(T2, basis2, m, n2) ? basis2 : DetectIdentityBasis(T2, m, n2);
                if (!IsValidIdentityBasis(T2, fixedBasis, m, n2))
                    throw new Exception("Phase I: failed to reconstruct Phase II basis.");

                // Phase II z-row = -c
                for (int jj = 0; jj < n2; jj++) T2[0, jj] = -c2[jj];

                CanonicalizeZRow(T2, fixedBasis, c2, m, n2, n2 + 1);

                // Build CanonicalForm with BOTH basic and nonbasic indices
                var nonBasic2 = Enumerable.Range(0, n2).Except(fixedBasis).ToArray();

                return new TableauState
                {
                    Can = new CanonicalForm(
                        A: ExtractA(T2, m, n2),
                        b: Extractb(T2, m, n2),
                        c: c2,
                        z0: 0.0,
                        basicIdx: fixedBasis,
                        nonBasicIdx: nonBasic2)
                    {
                        NumRows = m,
                        NumCols = n2,
                        NumVarsOriginal = can.NumVarsOriginal,
                        Map = can.Map
                    },
                    T = T2,
                    Basis = fixedBasis,
                    M = m,
                    N = n2,
                    P = can.NumVarsOriginal,
                    ColNames = can.Map.ColumnNames,
                    RowNames = can.Map.RowNames
                };
            }
            else
            {
                // ---- Phase II directly ----
                int m = can.NumRows, n = can.NumCols, width = n + 1;
                var T = new double[m + 1, n + 1];
                for (int i = 0; i < m; i++)
                {
                    for (int j = 0; j < n; j++) T[i + 1, j] = can.A[i, j];
                    T[i + 1, n] = can.b[i];
                }
                for (int j = 0; j < n; j++) T[0, j] = -can.c[j];

                var basis = (can.BasicIdx ?? Array.Empty<int>()).ToArray();
                if (!IsValidIdentityBasis(T, basis, m, n)) basis = DetectIdentityBasis(T, m, n);
                if (!IsValidIdentityBasis(T, basis, m, n))
                    throw new Exception("Phase II: cannot detect a valid identity basis.");

                CanonicalizeZRow(T, basis, can.c, m, n, width);

                var nonBasic = Enumerable.Range(0, n).Except(basis).ToArray();

                return new TableauState
                {
                    Can = new CanonicalForm(
                        A: ExtractA(T, m, n),
                        b: Extractb(T, m, n),
                        c: can.c,
                        z0: 0.0,
                        basicIdx: basis,
                        nonBasicIdx: nonBasic)
                    {
                        NumRows = m,
                        NumCols = n,
                        NumVarsOriginal = can.NumVarsOriginal,
                        Map = can.Map
                    },
                    T = T,
                    Basis = basis,
                    M = m,
                    N = n,
                    P = can.NumVarsOriginal,
                    ColNames = can.Map.ColumnNames,
                    RowNames = can.Map.RowNames
                };
            }
        }

        // -----------------------------
        // DUAL SIMPLEX (from current tableau)
        // -----------------------------
        private static (bool success, bool infeasible, bool unbounded, double z, double[] xDecision)
        DualOptimizeFromHere(TableauState S)
        {
            int m = S.M, n = S.N, width = n + 1;
            var T = S.T;
            var basis = S.Basis.ToArray();
            var basicSet = new HashSet<int>(basis);

            int it = 0;
            while (it++ < 10000)
            {
                int leave = FindLeavingRowDual(T, m, n);
                if (leave == -1)
                {
                    double z = T[0, n];
                    var xFull = ExtractPrimal(T, m, n, basis);
                    var xDecision = xFull.Take(Math.Min(S.P, xFull.Length)).ToArray();
                    return (true, false, false, z, xDecision);
                }

                int enter = FindEnteringColDual(T, leave, basicSet, n);
                if (enter == -1)
                {
                    return (false, true, false, 0.0, Array.Empty<double>()); // infeasible
                }

                Pivot(T, leave + 1, enter, width);
                basicSet.Remove(basis[leave]);
                basis[leave] = enter;
                basicSet.Add(enter);
            }
            return (false, false, false, 0.0, Array.Empty<double>()); // max iters
        }

        private static int FindLeavingRowDual(double[,] T, int m, int n)
        {
            int leave = -1; double mostNeg = -1e-18;
            for (int r = 0; r < m; r++)
            {
                double b = T[r + 1, n];
                if (b < mostNeg) { mostNeg = b; leave = r; }
            }
            return leave;
        }

        private static int FindEnteringColDual(double[,] T, int leave, HashSet<int> basicSet, int n)
        {
            int enter = -1;
            double best = double.PositiveInfinity;
            for (int j = 0; j < n; j++)
            {
                if (basicSet.Contains(j)) continue;
                double a = T[leave + 1, j];
                if (a < -EPS)
                {
                    double z = T[0, j];
                    double ratio = z / a; // minimize
                    if (ratio < best) { best = ratio; enter = j; }
                }
            }
            return enter;
        }

        // -----------------------------
        // c8 − c9 injection
        // -----------------------------
        private static void InjectBranchRow(TableauState S, int j, bool isUpper, double bound)
        {
            // Make x_j basic → "c8"
            MakeVariableBasic(S, j);
            int m = S.M, n = S.N;

            int c8row = Array.FindIndex(S.Basis, b => b == j);
            if (c8row < 0) throw new Exception("x_j is not basic after pivot (internal).");
            int c8 = c8row + 1; // tableau row index

            // Add new slack column and a new constraint row (at bottom)
            int newN = n + 1;
            var T2 = new double[m + 2, newN + 1];

            // Copy all old rows (z-row plus existing constraints)
            for (int r = 0; r < m + 1; r++)
            {
                for (int c = 0; c < n; c++) T2[r, c] = S.T[r, c];
                T2[r, newN] = S.T[r, n]; // RHS
            }
            // z-row coefficient for the brand new slack = 0 (keeps reduced costs untouched)
            T2[0, n] = 0.0;

            // New row = (c8 − c9)
            // Start with c8
            for (int c = 0; c < n; c++) T2[m + 1, c] = S.T[c8, c];
            T2[m + 1, newN] = S.T[c8, n]; // RHS

            // Subtract c9:
            // For x_j ≤ U:    c9: x_j + s_new = U
            // For x_j ≥ L:    c9: x_j − s_new = L
            T2[m + 1, j] -= 1.0;                          // subtract x_j
            T2[m + 1, n] -= (isUpper ? +1.0 : -1.0);      // subtract (±s_new) at new slack column index n
            T2[m + 1, newN] -= bound;                        // subtract RHS(U/L)

            // If RHS > 0, flip entire new row so RHS becomes negative → perfect for Dual Simplex
            if (T2[m + 1, newN] > 0)
                for (int c = 0; c < newN + 1; c++) T2[m + 1, c] = -T2[m + 1, c];

            // Update basis: new row is basic at the new slack column (index n)
            var basis2 = new int[m + 1];
            Array.Copy(S.Basis, basis2, m);
            basis2[m] = n;

            // Commit
            S.T = T2;
            S.Basis = basis2;
            S.M = m + 1;
            S.N = newN;
        }

        private static void MakeVariableBasic(TableauState S, int j)
        {
            int m = S.M, n = S.N, width = n + 1;
            int row = Array.FindIndex(S.Basis, b => b == j);
            if (row >= 0) return; // already basic

            // Find a pivot row with nonzero in column j
            int prow = -1;
            for (int r = 0; r < m; r++)
            {
                if (Math.Abs(S.T[r + 1, j]) > EPS) { prow = r + 1; break; }
            }
            if (prow == -1) throw new Exception($"Column x{j + 1} has no pivot candidate (all zeros).");

            Pivot(S.T, prow, j, width);
            S.Basis[prow - 1] = j;
        }

        // -----------------------------
        // Row ops / tableau ops
        // -----------------------------
        private static void Pivot(double[,] T, int prow, int pcol, int width)
        {
            double piv = T[prow, pcol];
            if (Math.Abs(piv) < 1e-16) throw new Exception("Pivot on ~zero.");

            // scale pivot row to make pivot = 1
            double inv = 1.0 / piv;
            for (int c = 0; c < width; c++) T[prow, c] *= inv;

            // eliminate pcol from all other rows (including z-row)
            int rows = T.GetLength(0);
            for (int r = 0; r < rows; r++)
            {
                if (r == prow) continue;
                double factor = T[r, pcol];
                if (Math.Abs(factor) < 1e-16) continue;
                for (int c = 0; c < width; c++)
                    T[r, c] -= factor * T[prow, c];
            }
        }

        private static void AddScaledRow(double[,] T, int targetRow, int srcRow, double scale, int width)
        {
            if (Math.Abs(scale) < 1e-16) return;
            for (int c = 0; c < width; c++) T[targetRow, c] += scale * T[srcRow, c];
        }

        private static bool IsValidIdentityBasis(double[,] T, int[] basis, int m, int n)
        {
            if (basis.Length != m) return false;
            for (int r = 0; r < m; r++)
            {
                int j = basis[r];
                if (j < 0 || j >= n) return false;
                if (Math.Abs(T[r + 1, j] - 1.0) > 1e-9) return false;
                for (int rr = 0; rr < m; rr++)
                {
                    if (rr == r) continue;
                    if (Math.Abs(T[rr + 1, j]) > 1e-9) return false;
                }
            }
            return true;
        }

        private static int[] DetectIdentityBasis(double[,] T, int m, int n)
        {
            var basis = Enumerable.Repeat(-1, m).ToArray();
            var used = new HashSet<int>();
            for (int r = 0; r < m; r++)
            {
                for (int j = 0; j < n; j++)
                {
                    if (used.Contains(j)) continue;
                    if (Math.Abs(T[r + 1, j] - 1.0) > 1e-9) continue;
                    bool ok = true;
                    for (int rr = 0; rr < m; rr++)
                    {
                        if (rr == r) continue;
                        if (Math.Abs(T[rr + 1, j]) > 1e-9) { ok = false; break; }
                    }
                    if (ok) { basis[r] = j; used.Add(j); break; }
                }
                if (basis[r] == -1) return Array.Empty<int>();
            }
            return basis;
        }

        private static void CanonicalizeZRow(double[,] T, int[] basis, double[] c, int m, int n, int width)
        {
            for (int r = 0; r < m; r++)
            {
                double cb = c[basis[r]];
                if (Math.Abs(cb) > EPS) AddScaledRow(T, 0, r + 1, cb, width);
            }
        }

        private static double[] ExtractPrimal(double[,] T, int m, int n, int[] basis)
        {
            var x = new double[n];
            for (int r = 0; r < m; r++)
            {
                int j = basis[r];
                if (j >= 0 && j < n) x[j] = T[r + 1, n];
            }
            return x;
        }

        private static double[,] ExtractA(double[,] T, int m, int n)
        {
            var A = new double[m, n];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                    A[i, j] = T[i + 1, j];
            return A;
        }

        private static double[] Extractb(double[,] T, int m, int n)
        {
            var b = new double[m];
            for (int i = 0; i < m; i++) b[i] = T[i + 1, n];
            return b;
        }

        private static TableauState CloneTableau(TableauState s)
        {
            var T2 = (double[,])s.T.Clone();
            var basis2 = (int[])s.Basis.Clone();
            return new TableauState
            {
                Can = s.Can,
                T = T2,
                Basis = basis2,
                M = s.M,
                N = s.N,
                P = s.P,
                ColNames = s.ColNames,
                RowNames = s.RowNames
            };
        }
    }
}
