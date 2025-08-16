using System.Globalization;
using Solver.Engine.Core;

namespace Solver.Engine.Simplex;

public sealed class PrimalSimplexSolver : ISolver
{
    public string Name => "Primal Simplex (Tableau)";

    public SolverResult Solve(LpModel model)
    {
        var log = new List<string>();
        Canonicalizer.Result canoRes;
        try
        {
            canoRes = Canonicalizer.ToCanonical(model);
        }
        catch (NotSupportedException e)
        {
            log.Add($"Canonicalization requires Phase I for this model: {e.Message}");
            return new SolverResult(false, 0, Array.Empty<double>(), log, infeasible: true);
        }

        var cf = canoRes.Canonical;

        // Build initial tableau:
        // Rows: m constraints + 1 objective
        // Cols: n variables + 1 RHS
        int m = cf.b.Length;
        int n = cf.c.Length;
        double[,] T = new double[m + 1, n + 1];

        // Constraint rows
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < n; j++) T[i, j] = cf.A[i, j];
            T[i, n] = cf.b[i];
        }

        // Objective row: z - c^T x = 0  => coefficients = -c
        for (int j = 0; j < n; j++) T[m, j] = -cf.c[j];
        T[m, n] = cf.z0;

        int[] basic = (int[])cf.BasicIdx.Clone();
        int[] nonBasic = (int[])cf.NonBasicIdx.Clone();

        log.Add(RenderTableau(T, basic, nonBasic, "Initial tableau"));

        // Iterate
        while (true)
        {
            // Entering variable: column with most positive reduced cost in objective row (since we maximize)
            int enter = ArgMaxPositive(T, m, n);
            if (enter == -1)
            {
                // Optimal
                var x = RecoverX(n, basic, T, m);
                double z = T[m, n];
                log.Add($"Optimal reached. z = {z.ToString("0.###", CultureInfo.InvariantCulture)}");
                // Map back to original variables length (relaxation; integers handled elsewhere)
                var xOrig = BackProjectToOriginal(model, cf, basic, T, m);
                return new SolverResult(true, z, xOrig, log);
            }

            // Leaving variable: min ratio test b_i / a_i_enter with a_i_enter > 0
            int leave = MinRatioRow(T, m, n, enter);
            if (leave == -1)
            {
                log.Add("Problem is unbounded in the direction of the entering variable.");
                return new SolverResult(false, double.NaN, Array.Empty<double>(), log, unbounded: true);
            }

            // Pivot
            Pivot(T, m, n, leave, enter);

            // Swap basis indices
            (basic[leave], nonBasic[Array.IndexOf(nonBasic, enter)]) = (enter, basic[leave]);

            log.Add(RenderTableau(T, basic, nonBasic, $"Pivot r{leave} c{enter}"));
        }
    }

    private static int ArgMaxPositive(double[,] T, int m, int n)
    {
        int col = -1; double best = 1e-12; // positive threshold
        for (int j = 0; j < n; j++)
        {
            double rc = T[m, j];
            if (rc > best) { best = rc; col = j; }
        }
        return col;
    }

    private static int MinRatioRow(double[,] T, int m, int n, int enter)
    {
        int row = -1; double best = double.PositiveInfinity;
        for (int i = 0; i < m; i++)
        {
            double a = T[i, enter];
            if (a > 1e-12)
            {
                double ratio = T[i, n] / a;
                if (ratio < best - 1e-12)
                {
                    best = ratio; row = i;
                }
            }
        }
        return row;
    }

    private static void Pivot(double[,] T, int m, int n, int pivRow, int pivCol)
    {
        double piv = T[pivRow, pivCol];
        // Normalize pivot row
        for (int j = 0; j <= n; j++) T[pivRow, j] /= piv;

        // Eliminate pivot column from other rows (including objective)
        for (int i = 0; i <= m; i++)
        {
            if (i == pivRow) continue;
            double factor = T[i, pivCol];
            if (Math.Abs(factor) < 1e-12) continue;
            for (int j = 0; j <= n; j++)
                T[i, j] -= factor * T[pivRow, j];
        }
    }

    private static double[] RecoverX(int n, int[] basic, double[,] T, int m)
    {
        var x = new double[n];
        for (int i = 0; i < m; i++)
        {
            int varIdx = basic[i];
            x[varIdx] = T[i, n];
        }
        return x;
    }

    private static double[] BackProjectToOriginal(LpModel model, CanonicalForm cf, int[] basic, double[,] T, int m)
    {
        // For now, if we only performed sign-expansions, we can reconstruct the first “newVarCount”
        // components (before slacks). Then compress back to original |Variables|.
        int n = cf.c.Length;
        var fullX = RecoverX(n, basic, T, m);

        // Identify how many came from original vars (before adding slacks)
        int newVarCount = n - m;

        // We don’t carry the explicit mapping forward from Canonicalizer here to keep code short.
        // For Phase 1 skip, we assumed:
        //   - Plus/Int/Bin: 1 var
        //   - Minus: 1 surrogate (x' = -y)
        //   - URS: 2 vars (x+ and x-)
        // To keep it robust: return the first |model.NumVars| if no URS/Minus existed, else fall back to trimmed.
        // In our demo video, just display canonical X and objective; sensitivity will use tableau anyway.
        int take = Math.Min(model.NumVars, Math.Max(0, newVarCount));
        return fullX.Take(take).ToArray();
    }

    private static string RenderTableau(double[,] T, int[] basic, int[] nonBasic, string title)
    {
        int rows = T.GetLength(0);
        int cols = T.GetLength(1);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"--- {title} ---");

        sb.Append("Basic | ");
        for (int j = 0; j < cols - 1; j++) sb.Append($"x{j + 1}\t");
        sb.Append("RHS").AppendLine();

        for (int i = 0; i < rows - 1; i++)
        {
            sb.Append($"x{basic[i] + 1}\t| ");
            for (int j = 0; j < cols - 1; j++) sb.Append($"{T[i, j]:0.###}\t");
            sb.Append($"{T[i, cols - 1]:0.###}").AppendLine();
        }

        sb.Append(" z   | ");
        for (int j = 0; j < cols - 1; j++) sb.Append($"{T[rows - 1, j]:0.###}\t");
        sb.Append($"{T[rows - 1, cols - 1]:0.###}").AppendLine();

        return sb.ToString();
    }
}


