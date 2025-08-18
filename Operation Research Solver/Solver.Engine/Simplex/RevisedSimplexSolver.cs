using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Solver.Engine.Core;
using Solver.Engine.IO;
using static Solver.Engine.Core.Numeric;

namespace Solver.Engine.Simplex
{
    public sealed class RevisedSimplexSolver : ISolver
    {
        public string Name => "Primal Simplex (Revised)";

        private const int MAX_ITERS = 10000;
        private static readonly ITableauPrinter Printer = new DefaultTableauPrinter();

        public SolverResult Solve(LpModel model)
        {
            var log = new List<string>();

            // Canonicalize
            var canRes = Canonicalizer.ToCanonical(model);
            var can = canRes.Canonical;

            if (canRes.Log is { Count: > 0 })
            {
                log.Add("— Canonicalization steps —");
                log.AddRange(canRes.Log);
                log.Add("");
            }

            // Phase I if required (tableau-based runner, silent)
            if (can.PhaseIRequired)
            {
                var p1 = RunPhaseI(can, log);
                if (!p1.success)
                    return new SolverResult(false, 0, Array.Empty<double>(), log, infeasible: true);
                can = p1.phaseII;
            }

            // Print canonical form once (for parity with tableau path)
            log.Add(Printer.RenderCanonical(
                can.A, can.b, can.c,
                model.NumVars,
                can.Map.ColumnNames, can.Map.RowNames));

            // Phase II (revised)
            var result = PhaseIIRevised(model, can, log);
            return result;
        }

        // ---------------- Phase II (Revised Simplex) ----------------

        private SolverResult PhaseIIRevised(LpModel model, CanonicalForm can, List<string> log)
        {
            int m = can.A.GetLength(0);
            int n = can.A.GetLength(1);
            int p = model.NumVars;

            // Basis array (columns of A that form B)
            var basis = (can.BasicIdx ?? Array.Empty<int>()).ToArray();

            int iter = 0;
            while (iter++ < MAX_ITERS)
            {
                // Build B and B^{-1}
                var B = TakeColumns(can.A, basis);
                var Binv = Invert(B);

                // x_B = B^{-1} b
                var xB = MatVec(Binv, can.b);

                // y^T = c_B^T B^{-1}
                var cB = basis.Select(j => can.c[j]).ToArray();
                var yT = RowTimesMatrix(cB, Binv); // length m

                // reduced costs r_j = c_j - y^T a_j
                var rc = new double[n];
                int enter = -1;
                double best = 0.0; // for Max, positive reduced cost is profitable
                for (int j = 0; j < n; j++)
                {
                    // skip basic cols
                    if (Array.IndexOf(basis, j) >= 0) { rc[j] = 0; continue; }

                    var a_j = GetColumn(can.A, j);
                    double rj = can.c[j] - Dot(yT, a_j);
                    rc[j] = rj;
                    if (rj > best + EPS) { best = rj; enter = j; }
                }

                if (enter == -1)
                {
                    // Optimal
                    double z = Dot(cB, xB) + can.z0;
                    var xFull = new double[n];
                    for (int i = 0; i < m; i++) xFull[basis[i]] = xB[i];
                    var xDecision = xFull.Take(Math.Min(model.NumVars, xFull.Length)).ToArray();
                    log.Add("Optimality reached (Revised).");
                    return new SolverResult(true, z, xDecision, log);
                }

                // direction d = B^{-1} a_enter
                var aEnter = GetColumn(can.A, enter);
                var d = MatVec(Binv, aEnter);

                // ratio test: theta = min_i { xB_i / d_i | d_i > 0 }
                int leave = -1;
                double theta = double.PositiveInfinity;
                for (int i = 0; i < m; i++)
                {
                    if (d[i] > EPS)
                    {
                        double t = xB[i] / d[i];
                        if (t < theta - EPS ||
                           (Math.Abs(t - theta) <= EPS && (leave == -1 || basis[i] > basis[leave])))
                        {
                            theta = t;
                            leave = i;
                        }
                    }
                }

                if (leave == -1)
                {
                    log.Add($"Unbounded (Revised): entering {NiceColName(enter, can.Map.ColumnNames, p)} has no positive direction.");
                    return new SolverResult(false, 0, Array.Empty<double>(), log, unbounded: true);
                }

                log.Add($"--- Iteration {iter} (Revised): enter {NiceColName(enter, can.Map.ColumnNames, p)}, leave {NiceRowName(leave, can.Map.RowNames)} ---");

                // Update basis: replace basis[leave] with 'enter'
                basis[leave] = enter;
                // (We rebuild B/Binv next loop; no eta updates here — simple & robust for class sizes)
            }

            log.Add("Max iterations exceeded (Revised).");
            return new SolverResult(false, 0, Array.Empty<double>(), log);
        }

        // ---------------- small shared numerics ----------------

        private static double[,] TakeColumns(double[,] A, int[] cols)
        {
            int m = A.GetLength(0);
            var B = new double[m, cols.Length];
            for (int i = 0; i < m; i++)
                for (int k = 0; k < cols.Length; k++)
                    B[i, k] = A[i, cols[k]];
            return B;
        }

        private static double[] GetColumn(double[,] A, int j)
        {
            int m = A.GetLength(0);
            var v = new double[m];
            for (int i = 0; i < m; i++) v[i] = A[i, j];
            return v;
        }

        private static double Dot(double[] a, double[] b)
        {
            double s = 0;
            for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }

        private static double[] MatVec(double[,] M, double[] v)
        {
            int m = M.GetLength(0);
            int n = M.GetLength(1);
            var r = new double[m];
            for (int i = 0; i < m; i++)
            {
                double s = 0;
                for (int j = 0; j < n; j++) s += M[i, j] * v[j];
                r[i] = s;
            }
            return r;
        }

        private static double[] RowTimesMatrix(double[] row, double[,] M)
        {
            int m = M.GetLength(0);
            int n = M.GetLength(1);
            var r = new double[n];
            for (int j = 0; j < n; j++)
            {
                double s = 0;
                for (int i = 0; i < m; i++) s += row[i] * M[i, j];
                r[j] = s;
            }
            return r;
        }

        // Gauss–Jordan inverse (OK for small classroom problems)
        private static double[,] Invert(double[,] A)
        {
            int n = A.GetLength(0);
            if (n != A.GetLength(1)) throw new InvalidOperationException("Matrix must be square.");

            var M = (double[,])A.Clone();
            var I = new double[n, n];
            for (int i = 0; i < n; i++) I[i, i] = 1.0;

            for (int col = 0; col < n; col++)
            {
                // pivot (partial)
                int piv = col;
                double best = Math.Abs(M[col, col]);
                for (int r = col + 1; r < n; r++)
                {
                    double v = Math.Abs(M[r, col]);
                    if (v > best) { best = v; piv = r; }
                }
                if (best < EPS) throw new InvalidOperationException("Singular basis matrix.");

                if (piv != col)
                {
                    SwapRows(M, col, piv);
                    SwapRows(I, col, piv);
                }

                // scale pivot row
                double p = M[col, col];
                for (int j = 0; j < n; j++) { M[col, j] /= p; I[col, j] /= p; }

                // eliminate other rows
                for (int r = 0; r < n; r++)
                {
                    if (r == col) continue;
                    double k = M[r, col];
                    if (Math.Abs(k) < EPS) continue;
                    for (int j = 0; j < n; j++)
                    {
                        M[r, j] -= k * M[col, j];
                        I[r, j] -= k * I[col, j];
                    }
                }
            }
            return I;
        }

        private static void SwapRows(double[,] M, int r1, int r2)
        {
            int n = M.GetLength(1);
            for (int j = 0; j < n; j++)
            {
                double t = M[r1, j];
                M[r1, j] = M[r2, j];
                M[r2, j] = t;
            }
        }

        private static string NiceColName(int j, string[]? colNames, int numVars)
        {
            if (colNames is { Length: > 0 } && j >= 0 && j < colNames.Length) return colNames[j];
            return j < numVars ? $"x{j + 1}" : $"s{j - numVars + 1}";
        }

        private static string NiceRowName(int i, string[]? rowNames)
            => (rowNames is { Length: > 0 } && i >= 0 && i < rowNames.Length) ? rowNames[i] : $"c{i + 1}";

        // ---------------- minimal Phase I runner (tableau, silent) ----------------

        private (bool success, CanonicalForm phaseII) RunPhaseI(CanonicalForm can, List<string> log)
        {
            int m = can.A.GetLength(0);
            int n = can.A.GetLength(1);
            int width = n + 1;

            var T = new double[m + 1, n + 1];
            for (int i = 0; i < m; i++)
            {
                for (int j = 0; j < n; j++) T[i + 1, j] = can.A[i, j];
                T[i + 1, n] = can.b[i];
            }
            for (int j = 0; j < n; j++) T[0, j] = -can.cPhaseI[j];

            var basis = (can.BasicIdx ?? Array.Empty<int>()).ToArray();
            if (!IsIdentity(T, basis)) basis = DetectIdentity(T);

            if (!IsIdentity(T, basis))
            {
                log.Add("Phase I (Revised): no identity basis found.");
                return (false, new CanonicalForm());
            }

            // z := z + sum(cb * row_b)
            for (int r = 0; r < m; r++)
            {
                double cb = can.cPhaseI[basis[r]];
                if (Math.Abs(cb) > EPS) AddScaledRow(T, 0, r + 1, cb, width);
            }

            // simplex loop (enter most negative z-row)
            int iter = 0;
            while (iter++ < MAX_ITERS)
            {
                int enter = -1; double best = 0;
                for (int j = 0; j < n; j++)
                    if (T[0, j] < best - EPS) { best = T[0, j]; enter = j; }
                if (enter == -1) break;

                int leave = -1; double minRatio = double.PositiveInfinity;
                for (int r = 0; r < m; r++)
                {
                    double a = T[r + 1, enter];
                    if (a > EPS)
                    {
                        double ratio = T[r + 1, n] / a;
                        if (ratio < minRatio - EPS ||
                           (Math.Abs(ratio - minRatio) <= EPS && (leave == -1 || basis[r] > basis[leave])))
                        {
                            minRatio = ratio; leave = r;
                        }
                    }
                }
                if (leave == -1) { log.Add("Phase I: unbounded ascent (unexpected)."); return (false, new CanonicalForm()); }

                Pivot(T, leave + 1, enter, width);
                basis[leave] = enter;
            }

            // z_I
            if (T[0, n] < -EPS) { log.Add("Phase I: infeasible (z_I < 0)."); return (false, new CanonicalForm()); }

            // pivot artificials out if still basic
            var art = new HashSet<int>(can.ArtificialIdx ?? Array.Empty<int>());
            for (int r = 0; r < m; r++)
            {
                if (!art.Contains(basis[r])) continue;
                int pcol = -1;
                for (int j = 0; j < n; j++)
                {
                    if (art.Contains(j)) continue;
                    if (Math.Abs(T[r + 1, j]) > EPS) { pcol = j; break; }
                }
                if (pcol == -1) { log.Add($"Phase I: could not remove artificial from row c{r + 1}."); return (false, new CanonicalForm()); }
                Pivot(T, r + 1, pcol, width);
                basis[r] = pcol;
            }

            // remove artificials
            var keep = Enumerable.Range(0, n).Where(j => !art.Contains(j)).ToArray();
            int n2 = keep.Length;
            var A2 = new double[m, n2];
            for (int i = 0; i < m; i++)
                for (int jj = 0; jj < n2; jj++)
                    A2[i, jj] = T[i + 1, keep[jj]];
            var b2 = new double[m];
            for (int i = 0; i < m; i++) b2[i] = T[i + 1, n];

            var pos = new Dictionary<int, int>();
            for (int jj = 0; jj < n2; jj++) pos[keep[jj]] = jj;

            var basis2 = new int[m];
            for (int i = 0; i < m; i++) basis2[i] = pos[basis[i]];

            var c2 = new double[n2];
            for (int jj = 0; jj < n2; jj++) c2[jj] = can.c[keep[jj]];

            var nonBasic2 = Enumerable.Range(0, n2).Except(basis2).ToArray();

            // names remap
            var colNames2 = (can.Map.ColumnNames is { Length: > 0 })
                ? keep.Select(j => can.Map.ColumnNames[j]).ToArray()
                : Enumerable.Range(1, n2).Select(j => j <= can.NumVarsOriginal ? $"x{j}" : $"s{j - can.NumVarsOriginal}").ToArray();
            var rowNames2 = can.Map.RowNames is { Length: > 0 } ? can.Map.RowNames : Enumerable.Range(1, m).Select(i => $"c{i}").ToArray();

            int[] slackNew = can.SlackIdx?.Where(j => pos.ContainsKey(j)).Select(j => pos[j]).ToArray() ?? Array.Empty<int>();
            int[] surplusNew = can.SurplusIdx?.Where(j => pos.ContainsKey(j)).Select(j => pos[j]).ToArray() ?? Array.Empty<int>();

            var rowToAdded2 = new int[m][];
            for (int i = 0; i < m; i++)
            {
                var list = new List<int>();
                foreach (var j in slackNew) if (Math.Abs(A2[i, j]) > EPS) list.Add(j);
                foreach (var j in surplusNew) if (Math.Abs(A2[i, j]) > EPS) list.Add(j);
                rowToAdded2[i] = list.ToArray();
            }

            var nameMap2 = new NameMap
            {
                ColumnNames = colNames2,
                RowNames = rowNames2,
                VarToColumns = can.Map.VarToColumns,
                RowToAddedColumns = rowToAdded2
            };

            var can2 = new CanonicalForm(A2, b2, c2, 0.0, basis2, nonBasic2)
            {
                PhaseIRequired = false,
                NumRows = m,
                NumCols = n2,
                NumVarsOriginal = can.NumVarsOriginal,
                NumSlack = slackNew.Length,
                NumArtificial = 0,
                SlackIdx = slackNew,
                SurplusIdx = surplusNew,
                ArtificialIdx = Array.Empty<int>(),
                Map = nameMap2
            };

            return (true, can2);
        }

        // tiny helpers for tableau Phase I
        private static void AddScaledRow(double[,] T, int target, int src, double k, int width)
        {
            for (int j = 0; j < width; j++) T[target, j] += k * T[src, j];
        }
        private static void Pivot(double[,] T, int prow, int pcol, int width)
        {
            double piv = T[prow, pcol];
            if (Math.Abs(piv) < EPS) throw new InvalidOperationException("Pivot ~ 0.");
            // scale pivot row
            for (int j = 0; j < width; j++) T[prow, j] /= piv;
            // eliminate
            int H = T.GetLength(0);
            for (int r = 0; r < H; r++)
            {
                if (r == prow) continue;
                double k = T[r, pcol];
                if (Math.Abs(k) < EPS) continue;
                for (int j = 0; j < width; j++) T[r, j] -= k * T[prow, j];
            }
        }
        private static bool IsIdentity(double[,] T, int[] basis)
        {
            int m = T.GetLength(0) - 1;
            int n = T.GetLength(1) - 1;
            if (basis == null || basis.Length != m) return false;
            for (int r = 0; r < m; r++)
            {
                int j = basis[r];
                if (j < 0 || j >= n) return false;
                if (Math.Abs(T[r + 1, j] - 1.0) > EPS) return false;
                for (int rr = 0; rr < m; rr++)
                    if (rr != r && Math.Abs(T[rr + 1, j]) > EPS) return false;
            }
            return true;
        }
        private static int[] DetectIdentity(double[,] T)
        {
            int m = T.GetLength(0) - 1;
            int n = T.GetLength(1) - 1;
            var b = Enumerable.Repeat(-1, m).ToArray();
            for (int r = 0; r < m; r++)
            {
                for (int j = 0; j < n; j++)
                {
                    if (Math.Abs(T[r + 1, j] - 1.0) > EPS) continue;
                    bool ok = true;
                    for (int rr = 0; rr < m; rr++)
                        if (rr != r && Math.Abs(T[rr + 1, j]) > EPS) { ok = false; break; }
                    if (ok) { b[r] = j; break; }
                }
            }
            return b;
        }
    }
}
