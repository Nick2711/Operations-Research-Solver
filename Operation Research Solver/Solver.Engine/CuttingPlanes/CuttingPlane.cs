using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Solver.Engine.Core;
using Solver.Engine.Integer;
using Solver.Engine.Simplex;

namespace Solver.Engine.CuttingPlanes
{
    /// <summary>
    /// Gomory fractional cutting plane solver with spreadsheet-style logs.
    /// Native mode: MAX, INT/BIN vars (x ≥ 0), all constraints ≤ with RHS ≥ 0.
    /// If the model is not in that shape, we fall back to Branch & Bound Simplex.
    /// </summary>
    public sealed class CuttingPlaneSolver : ISolver
    {
        public string Name => "Gomory Cutting Plane (fractional)";

        private const double Eps = 1e-9;
        private const double IntTol = 1e-6;
        private const int MaxSimplexIter = 10_000;
        private const int MaxCuts = 200;

        public SolverResult Solve(LpModel model)
        {
            if (!IsPureIntegerNonnegLE(model, out string whyNot))
            {
                var bb = new BranchAndBoundSimplexSolver();
                var log = new List<string>
                {
                    "Cutting Plane: model is not pure IP with (≤, x≥0).",
                    "Reason: " + (whyNot.Length == 0 ? "N/A" : whyNot),
                    $"Falling back to: {bb.Name}"
                };
                var res = bb.Solve(model);
                if (res.Log is not null) log.AddRange(res.Log);
                return new SolverResult(res.Success, res.ObjectiveValue, res.X, log, res.Unbounded, res.Infeasible);
            }

            int n = model.NumVars;
            var c = model.Variables.Select(v => v.ObjectiveCoeff).ToArray(); // MAX

            // Build A,b from model constraints
            var A = new List<double[]>();
            var b = new List<double>();
            foreach (var con in model.Constraints) { A.Add((double[])con.Coeffs.Clone()); b.Add(con.Rhs); }

            // Add x≤1 for BIN variables so 0-1 is respected in relaxation
            int binAdded = 0;
            for (int j = 0; j < n; j++)
            {
                if (model.Variables[j].Sign == SignRestriction.Bin)
                {
                    var row = new double[n];
                    row[j] = 1.0;
                    A.Add(row);
                    b.Add(1.0);
                    binAdded++;
                }
            }

            var logCP = new List<string>
            {
                "═══════════════════════════════════════════════════════════════════════",
                "  Cutting Plane Algorithm — Gomory (fractional)",
                $"  Vars: n={n}   Constraints: m={A.Count}   (x ≥ 0; all INT/BIN)",
                (binAdded > 0 ? $"  Note: added {binAdded} binary bounds x≤1 to LP relaxation" : null),
                "═══════════════════════════════════════════════════════════════════════"
            }.Where(s => s != null).ToList()!;

            var tab = Tableau.Build(A, b, c);
            int tableNo = 1;
            var snap = new StringBuilder();
            Tableau.PrintTable(ref tab, n, "Canonical Form", "(z)", snap, showTheta: false);
            logCP.Add(snap.ToString()); snap.Clear();

            // Solve the LP relaxation (primal simplex with logs)
            logCP.Add("───────────────────────────────────────────────────────────────────────");
            logCP.Add("Initial (LP Relaxation)");
            if (!Tableau.PrimalSimplexSnapshots(ref tab, n, ref tableNo, logCP, MaxSimplexIter))
                return new SolverResult(false, 0, new double[n], logCP);

            // Now iterate cuts
            int cuts = 0;
            while (true)
            {
                // Read current z and primal x
                double z = tab.Z;
                var x = Tableau.ReadPrimalSolution(tab, n);

                // Already integral?
                bool allInt = true;
                for (int i = 0; i < n; i++)
                {
                    double v = x[i];
                    double d = Math.Min(v - Math.Floor(v), Math.Ceiling(v) - v);
                    if (d > IntTol) { allInt = false; break; }
                }
                if (allInt)
                {
                    logCP.Add("───────────────────────────────────────────────────────────────────────");
                    logCP.Add("All integer variables integral — stop.");
                    logCP.Add($"Objective (z) = {FmtFrac(z)}");
                    for (int i = 0; i < n; i++) logCP.Add($"x{i + 1} = {FmtFrac(x[i])}");
                    return new SolverResult(true, z, x, logCP);
                }

                // Choose Gomory row with your rules
                string reason;
                int row = Tableau.ChooseGomoryRowSmart(tab, n, out reason);
                if (row < 0)
                {
                    var rounded = Array.ConvertAll(x, v => Math.Round(v));
                    double approxZ = 0; for (int i = 0; i < n; i++) approxZ += c[i] * rounded[i];
                    logCP.Add("No fractional basic row to cut; returning rounded feasible.");
                    return new SolverResult(true, approxZ, rounded, logCP);
                }

                // Build cut from the whole row (all nonbasic columns, including slacks)
                var (fracAllCols, f0) = Tableau.BuildGomoryCut(tab, row);
                int basicCol = tab.Basis[row];
                string cutOn = basicCol < n ? $"x{basicCol + 1}" : $"s{basicCol - n + 1}";

                logCP.Add("");
                logCP.Add($"Cutting Plane: Cut {cuts + 1}");
                logCP.Add($"Cut {cutOn} ({reason})");

                // Slide-style derivation lines
                foreach (var line in Tableau.FormatGomoryDerivationLines(tab, row))
                    logCP.Add(line);

                // Final inequality exactly like the slides
                logCP.Add(Tableau.FormatFinalInequality(tab, row, fracAllCols, f0));
                logCP.Add("Add as a new constraint row and re-solve (dual simplex).");

                // Extend tableau with the cut (add a new row with slack)
                Tableau.AddLeqCut(ref tab, fracAllCols, f0);

                // Dual simplex (since we create a negative RHS row)
                if (!Tableau.DualSimplexSnapshots(ref tab, n, ref tableNo, logCP, MaxSimplexIter))
                {
                    logCP.Add("Dual simplex failed/limit: stopping.");
                    var bestX = Tableau.ReadPrimalSolution(tab, n);
                    return new SolverResult(true, tab.Z, bestX, logCP);
                }

                cuts++;
                if (cuts >= MaxCuts)
                {
                    logCP.Add($"Reached cut limit ({MaxCuts}).");
                    var bestX = Tableau.ReadPrimalSolution(tab, n);
                    return new SolverResult(true, tab.Z, bestX, logCP);
                }
            }
        }

        // ───────────────────────── helpers & checks ─────────────────────────

        private static bool IsPureIntegerNonnegLE(LpModel m, out string whyNot)
        {
            var sb = new StringBuilder();
            if (m.Direction != OptimizeDirection.Max) sb.Append("Not MAX; ");
            if (m.Variables.Any(v => v.Sign != SignRestriction.Int && v.Sign != SignRestriction.Bin)) sb.Append("Vars not all INT/BIN; ");
            if (m.Variables.Any(v => v.Sign == SignRestriction.Minus || v.Sign == SignRestriction.Urs)) sb.Append("Some vars negative/free; ");
            if (m.Constraints.Any(c => c.Relation != Relation.LessOrEqual)) sb.Append("Non-≤ constraint exists; ");
            if (m.Constraints.Any(c => c.Rhs < -Eps)) sb.Append("Negative RHS; ");
            whyNot = sb.ToString();
            return whyNot.Length == 0;
        }

        private static string FmtFrac(double x) => Fraction.FormatMixed(x);

        // ───────────────────────── internal tableau ─────────────────────────

        private struct Tableau
        {
            public double[,] A;  // (m+1) x (n+slack+1); last column = RHS, last row = z
            public int M, N, Slack;
            public int[] Basis;
            public double Z;

            public static Tableau Build(List<double[]> Arows, List<double> b, double[] c)
            {
                int m = Arows.Count, n = c.Length, cols = n + m + 1;
                var T = new double[m + 1, cols];

                for (int i = 0; i < m; i++)
                {
                    for (int j = 0; j < n; j++) T[i, j] = Arows[i][j];
                    T[i, n + i] = 1.0;                 // slack
                    T[i, cols - 1] = b[i];             // RHS
                }
                for (int j = 0; j < n; j++) T[m, j] = -c[j]; // MAX: z-row = -c
                T[m, cols - 1] = 0.0;

                var bas = new int[m];
                for (int i = 0; i < m; i++) bas[i] = n + i;

                return new Tableau { A = T, M = m, N = n, Slack = m, Basis = bas, Z = 0.0 };
            }

            public static void PrintTable(ref Tableau T, int n, string title, string zLabel, StringBuilder sb, bool showTheta, int? enterCol = null)
            {
                int cols = T.N + T.Slack + 1;
                var head = new List<string>();
                for (int j = 0; j < T.N; j++) head.Add($"x{j + 1}");
                for (int j = 0; j < T.Slack; j++) head.Add($"s{j + 1}");
                head.Add("rhs");

                sb.AppendLine(title);
                sb.AppendLine("\t" + string.Join("\t", head));

                // --- z row ON TOP ---
                var zrow = new List<string>();
                for (int j = 0; j < cols; j++) zrow.Add(FmtFrac(T.A[T.M, j]));
                sb.AppendLine(zLabel + "\t" + string.Join("\t", zrow));
                if (showTheta && enterCol.HasValue)
                {
                    var thetaRow = new string[cols];
                    for (int j = 0; j < cols; j++) thetaRow[j] = (j == enterCol.Value) ? "↑" : "";
                    sb.AppendLine("     \t" + string.Join("\t", thetaRow));
                }

                // then constraint rows
                for (int i = 0; i < T.M; i++)
                {
                    var row = new List<string>();
                    for (int j = 0; j < cols; j++)
                    {
                        double v = T.A[i, j];
                        row.Add(FmtFrac(v));
                    }
                    string lead = VarName(T, T.Basis[i]);
                    sb.AppendLine(lead.PadRight(3) + "\t" + string.Join("\t", row));
                }

                sb.AppendLine();
            }

            public static bool PrimalSimplexSnapshots(ref Tableau T, int n, ref int tableNo, List<string> log, int maxIter)
            {
                int cols = T.N + T.Slack + 1, iter = 0;

                while (true)
                {
                    if (++iter > maxIter) { log.Add("Primal simplex: iteration limit."); return false; }

                    // MAX with z-row = -c: enter the MOST NEGATIVE reduced cost
                    int enter = -1; double mostNeg = 0.0;
                    for (int j = 0; j < cols - 1; j++)
                    {
                        double rc = T.A[T.M, j];
                        if (rc < mostNeg - Eps) { mostNeg = rc; enter = j; }
                    }
                    if (enter == -1) // optimal
                    {
                        T.Z = T.A[T.M, cols - 1];
                        var snap = new StringBuilder();
                        PrintTable(ref T, n, $"Table {tableNo++} — Optimal (LP Relax)", "(z*)", snap, false);
                        log.Add(snap.ToString());
                        return true;
                    }

                    // Ratio test (positive pivots only)
                    int leave = -1; double bestTheta = double.PositiveInfinity;
                    for (int i = 0; i < T.M; i++)
                    {
                        double a = T.A[i, enter];
                        if (a > Eps)
                        {
                            double theta = T.A[i, cols - 1] / a;
                            if (theta < bestTheta - Eps) { bestTheta = theta; leave = i; }
                        }
                    }
                    if (leave == -1) { log.Add("Primal simplex: Unbounded."); return false; }

                    Pivot(ref T, leave, enter);

                    var sb = new StringBuilder();
                    PrintTable(ref T, n, $"Table {tableNo++} — Primal simplex (enter c{enter + 1}, leave r{leave + 1})", "(z)", sb, true, enter);
                    log.Add(sb.ToString());
                }
            }

            public static bool DualSimplexSnapshots(ref Tableau T, int n, ref int tableNo, List<string> log, int maxIter)
            {
                int cols = T.N + T.Slack + 1, iter = 0;

                while (true)
                {
                    if (++iter > maxIter) { log.Add("Dual simplex: iteration limit."); return false; }

                    // choose most negative RHS (leaving row)
                    int leave = -1; double mostNegRhs = -Eps;
                    for (int i = 0; i < T.M; i++)
                    {
                        double rhs = T.A[i, cols - 1];
                        if (rhs < mostNegRhs) { mostNegRhs = rhs; leave = i; }
                    }
                    if (leave == -1)
                    {
                        T.Z = T.A[T.M, cols - 1];
                        var snap = new StringBuilder();
                        PrintTable(ref T, n, $"Table {tableNo++} — Dual simplex optimal", "(z*)", snap, false);
                        log.Add(snap.ToString());
                        return true;
                    }

                    // choose entering column by minimum ratio of |z_j|/|a_ij| for a_ij < 0 (classic dual rule)
                    int enter = -1; double bestRatio = double.PositiveInfinity;
                    for (int j = 0; j < cols - 1; j++)
                    {
                        double a = T.A[leave, j];
                        if (a < -Eps)
                        {
                            double ratio = Math.Abs(T.A[T.M, j] / a);
                            if (ratio < bestRatio - Eps) { bestRatio = ratio; enter = j; }
                        }
                    }
                    if (enter == -1) { log.Add("Dual simplex: infeasible (no entering col)."); return false; }

                    Pivot(ref T, leave, enter);

                    var sb = new StringBuilder();
                    PrintTable(ref T, n, $"Table {tableNo++} — Dual simplex (enter c{enter + 1}, leave r{leave + 1})", "(z)", sb, false);
                    log.Add(sb.ToString());
                }
            }

            /// <summary>
            /// Add Gomory cut row:  -∑ f_j * col_j + s_new = -f0
            /// where f_j = frac(a_ij) for all nonbasic columns j ≠ basic(row).
            /// </summary>
            public static void AddLeqCut(ref Tableau T, double[] fracAllCols, double f0)
            {
                int oldRows = T.M;                     // number of constraint rows
                int oldSlack = T.Slack;
                int oldColsNoRhs = T.N + oldSlack;     // index of last coeff column in OLD matrix
                int newColsNoRhs = oldColsNoRhs + 1;   // we insert one new slack column
                int newRhsCol = newColsNoRhs;        // RHS column index in the NEW matrix

                var Anew = new double[oldRows + 2, newColsNoRhs + 1];

                // Copy old rows (coeffs) and RHS into the new RHS column
                for (int i = 0; i < oldRows; i++)
                {
                    for (int j = 0; j < oldColsNoRhs; j++)
                        Anew[i, j] = T.A[i, j];
                    Anew[i, newRhsCol] = T.A[i, oldColsNoRhs];
                }

                // New cut row
                int r = oldRows;
                for (int j = 0; j < oldColsNoRhs; j++)
                    Anew[r, j] = -fracAllCols[j];
                Anew[r, oldColsNoRhs] = 1.0;   // new slack
                Anew[r, newRhsCol] = -f0;   // RHS

                // z row
                int oldZ = oldRows, newZ = oldRows + 1;
                for (int j = 0; j < oldColsNoRhs; j++)
                    Anew[newZ, j] = T.A[oldZ, j];
                Anew[newZ, oldColsNoRhs] = 0.0;
                Anew[newZ, newRhsCol] = T.A[oldZ, oldColsNoRhs];

                // Commit
                T.A = Anew;
                T.M = oldRows + 1;
                T.Slack = oldSlack + 1;

                var bas = new int[T.M];
                Array.Copy(T.Basis, bas, T.Basis.Length);
                bas[T.M - 1] = T.N + T.Slack - 1;
                T.Basis = bas;
            }

            public static (double[] fracAll, double rhsFrac) BuildGomoryCut(Tableau T, int row)
            {
                int colsNoRhs = T.N + T.Slack;
                int basic = T.Basis[row];

                var frac = new double[colsNoRhs];

                for (int j = 0; j < colsNoRhs; j++)
                {
                    if (j == basic) { frac[j] = 0.0; continue; }
                    double a = T.A[row, j];
                    double fj = a - Math.Floor(a + Eps);
                    if (fj < Eps || 1.0 - fj < Eps) fj = 0.0;
                    frac[j] = fj;
                }

                double rhs = T.A[row, colsNoRhs];
                double f0 = rhs - Math.Floor(rhs + Eps);
                if (f0 < Eps || 1.0 - f0 < Eps) f0 = 0.0;

                return (frac, f0);
            }

            public static void Pivot(ref Tableau T, int leave, int enter)
            {
                int cols = T.N + T.Slack + 1;
                double piv = T.A[leave, enter];
                if (Math.Abs(piv) < Eps) piv = Eps;

                for (int j = 0; j < cols; j++) T.A[leave, j] /= piv;

                for (int i = 0; i <= T.M; i++)
                {
                    if (i == leave) continue;
                    double mult = T.A[i, enter];
                    if (Math.Abs(mult) < Eps) continue;
                    for (int j = 0; j < cols; j++) T.A[i, j] -= mult * T.A[leave, j];
                }

                T.Basis[leave] = enter;
            }

            public static double[] ReadPrimalSolution(Tableau T, int n)
            {
                int cols = T.N + T.Slack + 1;
                var x = new double[n];
                for (int i = 0; i < T.M; i++)
                {
                    int bc = T.Basis[i];
                    if (bc < n)
                    {
                        x[bc] = T.A[i, cols - 1];
                    }
                }
                return x;
            }

            public static int ChooseGomoryRowSmart(Tableau T, int n, out string reason)
            {
                int cols = T.N + T.Slack + 1;
                int bestRow = -1; double bestDist = double.PositiveInfinity;
                for (int i = 0; i < T.M; i++)
                {
                    int bc = T.Basis[i];
                    if (bc >= n) continue;

                    double rhs = T.A[i, cols - 1];
                    double f = rhs - Math.Floor(rhs + Eps);
                    if (f < Eps || 1.0 - f < Eps) continue;

                    double dist = Math.Abs(f - 0.5);
                    if (dist < bestDist - Eps || (Math.Abs(dist - bestDist) <= Eps && i < bestRow))
                    {
                        bestDist = dist; bestRow = i;
                    }
                }

                if (bestRow >= 0)
                {
                    reason = "choose RHS fractional part closest to 0.5 (ties → lowest row)";
                    return bestRow;
                }

                for (int i = 0; i < T.M; i++)
                {
                    double rhs = T.A[i, cols - 1];
                    double f = rhs - Math.Floor(rhs + Eps);
                    if (f >= Eps && 1.0 - f >= Eps) { reason = "fallback: any fractional RHS"; return i; }
                }

                reason = "none";
                return -1;
            }

            // ───────────────────── formatting helpers (slide-style) ─────────────────────

            private static string VarName(Tableau T, int j)
                => j < T.N ? $"x{j + 1}" : $"s{j - T.N + 1}";

            private static string Term(double coeff, string name, bool first)
            {
                if (Math.Abs(coeff) < Eps) return "";
                string sign = first ? (coeff < 0 ? "-" : "") : (coeff < 0 ? " - " : " + ");
                double abs = Math.Abs(coeff);
                return $"{sign}{FmtFrac(abs)} {name}";
            }

            private static string TermsSum(IEnumerable<(double c, string name)> items)
            {
                var sb = new StringBuilder();
                bool first = true;
                foreach (var (c, name) in items)
                {
                    if (Math.Abs(c) < Eps) continue;
                    sb.Append(Term(c, name, first));
                    first = false;
                }
                return sb.Length == 0 ? "0" : sb.ToString();
            }

            public static IEnumerable<string> FormatGomoryDerivationLines(Tableau T, int row)
            {
                int colsNoRhs = T.N + T.Slack;
                int basic = T.Basis[row];

                var terms = new List<(double a, double k, double f, string name)>();
                for (int j = 0; j < colsNoRhs; j++)
                {
                    if (j == basic) continue;
                    double a = T.A[row, j];
                    double fj = a - Math.Floor(a + Eps); if (fj < Eps || 1 - fj < Eps) fj = 0.0;
                    double kj = a - fj;
                    terms.Add((a, kj, fj, VarName(T, j)));
                }
                double rhs = T.A[row, colsNoRhs];
                double f0 = rhs - Math.Floor(rhs + Eps); if (f0 < Eps || 1 - f0 < Eps) f0 = 0.0;
                double k0 = rhs - f0;

                string basicName = VarName(T, basic);

                var t1 = terms.Select(t => (t.a, t.name));
                string L1 = $"{basicName} {TermsSum(t1.Select(x => (x.a, x.name)))} = {FmtFrac(rhs)}";
                string L2 = $"{basicName} {TermsSum(terms.SelectMany(t => new[] { (t.k, t.name), (t.f, t.name) }))} = {FmtFrac(k0)} + {FmtFrac(f0)}";
                var leftInts = terms.Select(t => (t.k, t.name));
                var rightFracs = terms.Select(t => (-t.f, t.name));
                string L3Lhs = $"{basicName} {TermsSum(leftInts)} - {FmtFrac(k0)}";
                string L3Rhs = $"{TermsSum(rightFracs)} + {FmtFrac(f0)}";
                string L3 = $"{L3Lhs} = {L3Rhs}";

                return new[] { L1, L2, L3 };
            }

            public static string FormatFinalInequality(Tableau T, int row, double[] fracAllCols, double f0)
            {
                int colsNoRhs = T.N + T.Slack;
                var items = new List<(double c, string name)>();
                for (int j = 0; j < colsNoRhs; j++)
                {
                    if (j == T.Basis[row]) continue;
                    double f = fracAllCols[j];
                    if (f < Eps) continue;
                    items.Add((-f, VarName(T, j)));
                }

                string lhs = TermsSum(items);
                if (lhs == "0") lhs = "0";
                lhs = lhs == "0" ? $"{FmtFrac(f0)}" : $"{lhs} + {FmtFrac(f0)}";
                return $"{lhs} ≤ 0";
            }
        }
    }

    /// <summary>
    /// Mixed fraction formatting (like slides).
    /// </summary>
    internal static class Fraction
    {
        public static string FormatMixed(double v, double eps = 1e-9, int maxDen = 64)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return v.ToString(CultureInfo.InvariantCulture);
            if (Math.Abs(v) < eps) return "0";

            int sign = v < 0 ? -1 : 1;
            v = Math.Abs(v);

            int whole = (int)Math.Floor(v + eps);
            double frac = v - whole;
            if (frac < eps) return (sign * whole).ToString();

            (int num, int den) = ToFraction(frac, maxDen);
            if (whole == 0) return $"{(sign < 0 ? "-" : "")}{num}/{den}";
            return $"{(sign < 0 ? "-" : "")}{whole} {num}/{den}";
        }

        private static (int, int) ToFraction(double x, int maxDen = 64, double eps = 1e-9)
        {
            int h1 = 1, h0 = 0, k1 = 0, k0 = 1;
            double b = x;
            do
            {
                int a = (int)Math.Floor(b);
                int h2 = a * h1 + h0;
                int k2 = a * k1 + k0;

                if (k2 > maxDen) break;

                h0 = h1; h1 = h2; k0 = k1; k1 = k2;
                double frac = b - a;
                if (frac < eps) break;
                b = 1.0 / frac;
            } while (true);

            return (h1, k1 == 0 ? 1 : k1);
        }
    }
}
