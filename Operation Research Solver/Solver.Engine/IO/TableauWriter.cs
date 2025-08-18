using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Solver.Engine.Core;
using static Solver.Engine.Core.Numeric;

namespace Solver.Engine.IO
{
    public interface ITableauPrinter
    {
        // Extended signatures accept optional column/row names for accurate labeling (x/s/r/a)
        string RenderCanonical(double[,] A, double[] b, double[] c, int numVarsOriginal,
                               string[]? columnNames = null, string[]? rowNames = null);

        string Render(string title,
                      double[,] T, int m, int n,
                      int[] basis,
                      int numVarsOriginal,
                      int enterCol,
                      double[] theta,
                      string[]? columnNames = null,
                      string[]? rowNames = null);
    }

    /// <summary>
    /// Default printer used by simplex-style solvers.
    /// If names are supplied, they are used verbatim (so artificials show as a#, surplus as r#).
    /// Otherwise it falls back to x1..x_p then s1..s_m.
    /// </summary>
    public sealed class DefaultTableauPrinter : ITableauPrinter
    {
        private static string F(double v)
        {
            if (Math.Abs(v) < EPS) v = 0.0;
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public string RenderCanonical(double[,] A, double[] b, double[] c, int numVarsOriginal,
                                      string[]? columnNames = null, string[]? rowNames = null)
        {
            int m = A.GetLength(0);
            int n = A.GetLength(1);

            var names = columnNames ?? Enumerable.Range(1, n)
                .Select(j => j <= numVarsOriginal ? $"x{j}" : $"s{j - numVarsOriginal}")
                .ToArray();

            var rnames = rowNames ?? Enumerable.Range(1, m).Select(i => $"c{i}").ToArray();

            var sb = new StringBuilder();
            sb.AppendLine("Canonical form");

            // z - c^T x = 0
            sb.Append('z');
            for (int j = 0; j < n; j++)
            {
                double cj = -c[j];
                if (Math.Abs(cj) < EPS) continue;
                sb.Append(' ').Append(cj < 0 ? "- " : "+ ");
                sb.Append(VarTerm(Math.Abs(cj), names[j]));
            }
            sb.Append(" = 0").AppendLine();

            // constraints (c1..cm)
            for (int i = 0; i < m; i++)
            {
                var row = new StringBuilder();
                bool first = true;
                for (int j = 0; j < n; j++)
                {
                    double a = A[i, j];
                    if (Math.Abs(a) < EPS) continue;
                    row.Append(first ? "" : " ").Append(a < 0 ? "- " : "+ ");
                    row.Append(VarTerm(Math.Abs(a), names[j]));
                    first = false;
                }

                var label = (i < rnames.Length) ? rnames[i] : $"c{i + 1}";
                sb.Append("  ").Append(label).Append(": ");
                if (row.Length == 0) sb.Append('0'); else sb.Append(row);
                sb.Append(" = ").Append(F(b[i])).AppendLine();
            }
            return sb.ToString();
        }

        public string Render(string title,
                             double[,] T, int m, int n,
                             int[] basis,
                             int numVarsOriginal,
                             int enterCol,
                             double[] theta,
                             string[]? columnNames = null,
                             string[]? rowNames = null)
        {
            var names = columnNames ?? Enumerable.Range(1, n)
                .Select(j => j <= numVarsOriginal ? $"x{j}" : $"s{j - numVarsOriginal}")
                .ToArray();

            var rnames = rowNames ?? Enumerable.Range(1, m).Select(i => $"c{i}").ToArray();

            var sb = new StringBuilder();
            sb.AppendLine(title);

            // headers
            sb.Append("Basic |");
            for (int j = 0; j < n; j++) sb.Append(' ').Append(names[j]).Append('\t');
            sb.Append("RHS\tθ").AppendLine();

            // z-row
            sb.Append(" z    |");
            for (int j = 0; j < n; j++) sb.Append(F(T[0, j])).Append('\t');
            sb.Append(F(T[0, n])).Append('\t').AppendLine();
            sb.AppendLine(new string('-', 60));

            // constraints (c1..cm)
            for (int i = 0; i < m; i++)
            {
                var brow = (i < rnames.Length) ? rnames[i] : $"c{i + 1}";
                sb.Append($"{brow,5} |");

                for (int j = 0; j < n; j++) sb.Append(F(T[i + 1, j])).Append('\t');
                sb.Append(F(T[i + 1, n])).Append('\t');

                if (enterCol >= 0)
                {
                    double th = theta[i];
                    sb.Append(double.IsNaN(th) ? "" : F(th));
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string VarTerm(double coef, string name)
            => Math.Abs(coef - 1.0) < EPS ? name : coef.ToString("0.###", CultureInfo.InvariantCulture) + name;
    }
}
