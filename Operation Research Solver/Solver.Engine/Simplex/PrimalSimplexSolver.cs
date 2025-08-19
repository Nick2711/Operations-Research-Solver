using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Solver.Engine.Core;

namespace Solver.Engine.Simplex
{
    public sealed class PrimalSimplexSolver : ISolver
    {
        public string Name => "Primal Simplex (Tableau)";

        private const double EPS = 1e-9;
        private const int MAX_ITERS = 10000;

        public SolverResult Solve(LpModel model)
        {
            var log = new List<string>();

            // 1) Canonicalize
            var canRes = Canonicalizer.ToCanonical(model);
            var can = canRes.Canonical;

            int m = can.A.GetLength(0);     // constraints
            int n = can.A.GetLength(1);     // total cols (decisions + slacks)
            int p = model.NumVars;          // decision variables count
            int width = n + 1;              // + RHS

            // 2) Build tableau (m+1) x (n+1)
            var T = new double[m + 1, n + 1];
            for (int i = 0; i < m; i++)
            {
                for (int j = 0; j < n; j++) T[i + 1, j] = can.A[i, j];
                T[i + 1, n] = can.b[i];
            }
            for (int j = 0; j < n; j++) T[0, j] = -can.c[j];
            T[0, n] = can.z0;

            // 3) Basis (verify identity; else detect)
            var basis = (can.BasicIdx ?? Array.Empty<int>()).ToArray();
            if (!IsValidIdentityBasis(T, basis, m, n)) basis = DetectIdentityBasis(T, m, n);
            if (!IsValidIdentityBasis(T, basis, m, n))
            {
                log.Add("No valid initial identity basis; Phase I required.");
                return new SolverResult(false, 0, Array.Empty<double>(), log, infeasible: true);
            }
            var basicSet = new HashSet<int>(basis);

            // 4) Canonicalize z-row: z := z + Σ c_b * row_b
            for (int r = 0; r < m; r++)
            {
                double cb = can.c[basis[r]];
                if (Math.Abs(cb) > EPS) AddScaledRow(T, 0, r + 1, cb, width);
            }

            // 5) Optional: print canonical form like your sheet
            log.Add(RenderCanonicalForm(can.A, can.b, can.c, p));

            // INITIAL DISPLAY (t-1): compute entering+theta for display before first pivot
            int enter = FindEntering(T, basicSet, n);
            var thetaDisp = ComputeThetaColumn(T, enter, m, n);
            log.Add(RenderTableauWithTheta("t-1", T, m, n, basis, p, enter, thetaDisp));

            int iter = 0;
            while (iter++ < MAX_ITERS)
            {
                // Actual entering for this iteration
                enter = FindEntering(T, basicSet, n);
                if (enter == -1)
                {
                    // Optimal
                    double z = T[0, n];
                    var xFull = ExtractPrimal(T, m, n, basis);
                    var xDecision = xFull.Take(Math.Min(model.NumVars, xFull.Length)).ToArray();
                    log.Add("Optimality reached.");
                    return new SolverResult(true, z, xDecision, log);
                }

                // Leaving: standard ratio test with a_ij > 0
                int leave = -1;
                double minRatio = double.PositiveInfinity;
                for (int r = 0; r < m; r++)
                {
                    double a = T[r + 1, enter];
                    if (a > EPS)
                    {
                        double ratio = T[r + 1, n] / a;
                        if (ratio < minRatio - EPS ||
                            (Math.Abs(ratio - minRatio) <= EPS && (leave == -1 || basis[r] > basis[leave])))
                        {
                            minRatio = ratio;
                            leave = r;
                        }
                    }
                }
                if (leave == -1)
                {
                    log.Add($"Unbounded: entering x{enter + 1} has no positive entries.");
                    return new SolverResult(false, 0, Array.Empty<double>(), log, unbounded: true);
                }

                // Pivot
                Pivot(T, leave + 1, enter, width);
                basicSet.Remove(basis[leave]);
                basis[leave] = enter;
                basicSet.Add(enter);

                // AFTER-PIVOT DISPLAY (your second block shows the new tableau with next θ)
                int nextEnter = FindEntering(T, basicSet, n);
                var thetaNext = ComputeThetaColumn(T, nextEnter, m, n);
                log.Add(RenderTableauWithTheta("t-1", T, m, n, basis, p, nextEnter, thetaNext));
            }

            log.Add("Max iterations exceeded.");
            return new SolverResult(false, 0, Array.Empty<double>(), log);
        }


        // ===== helpers =====
        private static int FindEntering(double[,] T, HashSet<int> basicSet, int n)
        {
            int enter = -1;
            double best = 0.0;
            for (int j = 0; j < n; j++)
            {
                if (basicSet.Contains(j)) continue;
                double rc = T[0, j];
                if (rc < best - EPS) { best = rc; enter = j; }
            }
            return enter;
        }

        private static double[] ComputeThetaColumn(double[,] T, int enter, int m, int n)
        {
            var theta = new double[m];
            if (enter < 0) return theta; // all zeros when optimal (matches your final block)
            for (int r = 0; r < m; r++)
            {
                double a = T[r + 1, enter];
                if (Math.Abs(a) < EPS) theta[r] = double.NaN;    // print blank
                else theta[r] = T[r + 1, n] / a;                 // allow negatives, like your -16
            }
            return theta;
        }

        private static string RenderCanonicalForm(double[,] A, double[] b, double[] c, int numVars)
        {
            var I = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("Canonical form");
            // z - c^T x = 0
            sb.Append("z");
            for (int j = 0; j < numVars; j++)
            {
                double cj = -c[j];
                sb.Append(" ").Append(SignTerm(cj)).Append(VarTerm(Math.Abs(cj), $"x{j + 1}", I));
            }
            sb.Append(" = 0").AppendLine();

            // constraints with slacks s1..sm appended
            int m = A.GetLength(0);
            int n = A.GetLength(1);
            for (int i = 0; i < m; i++)
            {
                var row = new StringBuilder();
                bool first = true;
                for (int j = 0; j < numVars; j++)
                {
                    double a = A[i, j];
                    if (Math.Abs(a) < EPS) continue;
                    row.Append(first ? "" : " ").Append(SignTerm(a)).Append(VarTerm(Math.Abs(a), $"x{j + 1}", I));
                    first = false;
                }
                // + s_i
                row.Append(first ? "" : " ").Append("+ ").Append($"s{i + 1}");
                row.Append(" = ").Append(b[i].ToString("0.###", I));
                sb.Append("  ");                       // spacing
                sb.Append($"c{i + 1}: ");              // label
                sb.AppendLine(row.ToString());

            }
            return sb.ToString();
        }

        private static string SignTerm(double v) => (v < -EPS) ? "- " : "+ ";
        private static string VarTerm(double coef, string name, System.Globalization.CultureInfo I)
        {
            if (Math.Abs(coef - 1.0) < EPS) return name;
            return coef.ToString("0.###", I) + name;
        }

        private static string F(double v)
        {
            // avoid -0; keep 0.### formatting
            if (Math.Abs(v) < EPS) v = 0.0;
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string RenderTableauWithTheta(
            string title, double[,] T, int m, int n, int[] basis, int numVars, int enterCol, double[] theta)
        {
            var I = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine(title);

            // headers: x1..x_numVars, then s1..s_m, RHS, theta
            sb.Append("Basic |");
            for (int j = 0; j < numVars; j++) sb.Append($" x{j + 1}\t");
            for (int j = 0; j < m; j++) sb.Append($" s{j + 1}\t");
            sb.Append("RHS\tθ");
            sb.AppendLine();

            // z-row first
            sb.Append(" z    |");
            for (int j = 0; j < numVars; j++) sb.Append(F(T[0, j])).Append('\t');
            for (int j = 0; j < m; j++) sb.Append(F(T[0, numVars + j])).Append('\t');
            sb.Append(F(T[0, n])).Append("\t"); // RHS of z
            sb.AppendLine();
            sb.AppendLine(new string('-', 60));

            // constraint rows
            for (int i = 0; i < m; i++)
            {
                string bcol = (i < basis.Length && basis[i] >= 0)
                    ? ((basis[i] < numVars) ? $"x{basis[i] + 1}" : $"s{basis[i] - numVars + 1}")
                    : " ? ";

                sb.Append($"{($"c{i + 1}"),5} |");

                // decision columns
                for (int j = 0; j < numVars; j++) sb.Append(F(T[i + 1, j])).Append('\t');

                // slack columns
                for (int j = 0; j < m; j++) sb.Append(F(T[i + 1, numVars + j])).Append('\t');

                // RHS
                sb.Append(F(T[i + 1, n])).Append('\t');

                // theta (RHS / a_·,enter); blank if NaN or no entering col
                if (enterCol >= 0)
                {
                    double th = theta[i];
                    sb.Append(double.IsNaN(th) ? "" : F(th));
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static bool IsValidIdentityBasis(double[,] T, int[] basis, int m, int n)
        {
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

        private static int[] DetectIdentityBasis(double[,] T, int m, int n)
        {
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

        private static void AddScaledRow(double[,] T, int target, int src, double factor, int width)
        {
            for (int j = 0; j < width; j++) T[target, j] += factor * T[src, j];
        }

        private static void ScaleRow(double[,] T, int row, double factor, int width)
        {
            for (int j = 0; j < width; j++) T[row, j] *= factor;
        }

        private static void Pivot(double[,] T, int prow, int pcol, int width)
        {
            double piv = T[prow, pcol];
            if (Math.Abs(piv) < EPS) throw new InvalidOperationException("Pivot is ~0.");

            ScaleRow(T, prow, 1.0 / piv, width);
            int height = T.GetLength(0);
            for (int r = 0; r < height; r++)
            {
                if (r == prow) continue;
                double k = T[r, pcol];
                if (Math.Abs(k) > EPS) AddScaledRow(T, r, prow, -k, width);
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

        private static string RenderTableau(string title, double[,] T, int m, int n, int[] basis)
        {
            var I = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine(title);

            // header
            sb.Append("Basic |");
            for (int j = 0; j < n; j++) sb.Append($" x{(j + 1).ToString(I),-3}\t");
            sb.Append("RHS");
            sb.AppendLine();

            // z-row first
            sb.Append(" z    |");
            for (int j = 0; j < n; j++) sb.Append(T[0, j].ToString("0.###", I)).Append('\t');
            sb.Append(T[0, n].ToString("0.###", I));
            sb.AppendLine();
            sb.AppendLine(new string('-', 60));

            // constraints
            for (int i = 0; i < m; i++)
            {
                string bcol = (i < basis.Length && basis[i] >= 0) ? $"x{basis[i] + 1}" : " ? ";
                sb.Append($"{bcol,5} |");
                for (int j = 0; j < n; j++) sb.Append(T[i + 1, j].ToString("0.###", I)).Append('\t');
                sb.Append(T[i + 1, n].ToString("0.###", I));
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
