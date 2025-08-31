using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Operation_Research_Solver.Server.Services;
using Solver.Engine.IO;
using Solver.Engine.Simplex;
using Solver.Engine.Core;

namespace Operation_Research_Solver.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")] // /api/sensitivity
    public sealed class SensitivityController : ControllerBase
    {
        private readonly ILastSolveCache _cache;

        public SensitivityController(ILastSolveCache cache) => _cache = cache;

        // ---------- DTOs ----------
        public sealed class NonbasicOptionDto
        {
            public int index { get; set; }
            public string label { get; set; } = "";
            public double reducedCost { get; set; }
        }

        public sealed class RangeNonbasicResponse
        {
            public int index { get; set; }
            public string label { get; set; } = "";

            // Keep the "k" property many UIs already bind to (0-based index)
            public int k { get; set; }

            public double currentCoefficient { get; set; }
            public double reducedCost { get; set; }
            public double yDotNcol { get; set; }

            // Both sides so UI can always render
            public double allowableIncrease { get; set; }
            public double allowableDecrease { get; set; }

            // Bounds that preserve the basis (use Min/MaxValue to mean -∞/+∞)
            public double coefficientLowerBound { get; set; }
            public double coefficientUpperBound { get; set; }

            // Flags to help the UI render “unbounded” or ±∞
            public bool increaseUnbounded { get; set; }
            public bool decreaseUnbounded { get; set; }
            public bool lowerIsNegInf { get; set; }
            public bool upperIsPosInf { get; set; }

            public string explanation { get; set; } = "";
        }

        // ---------- List nonbasic ----------
        // GET /api/sensitivity/nonbasic-list
        [HttpGet("nonbasic-list")]
        public IActionResult NonbasicList()
        {
            if (string.IsNullOrWhiteSpace(_cache.LastModelText))
                return BadRequest("No model text in memory. Solve a model first.");

            var (isMax, c, labels, A, senses) = ParseModel(_cache.LastModelText);
            if (A == null || A.Length == 0)
                return BadRequest("Model parse failed.");

            // Shadow prices from solving the dual of the CURRENT text
            var y = SolveDualForShadowPrices(_cache.LastModelText, out _);
            if (y == null || y.Length == 0)
                return BadRequest("Could not obtain shadow prices for current model.");

            int m = A.GetLength(0);
            int n = A.GetLength(1);
            if (y.Length != m)
                return BadRequest("Shadow prices length mismatch.");

            const double eps = 1e-9;
            var list = new List<NonbasicOptionDto>();

            // r_j = c_j - y^T a_j  (pad c_j = 0 if j >= c.Length)
            for (int j = 0; j < n; j++)
            {
                double yDotAj = 0.0;
                for (int i = 0; i < m; i++)
                    yDotAj += y[i] * A[i, j];

                double cj = j < c.Length ? c[j] : 0.0;
                double rj = cj - yDotAj;

                if (Math.Abs(rj) > eps)  // non-basic (basic vars have |rj| ~ 0)
                {
                    list.Add(new NonbasicOptionDto
                    {
                        index = j,
                        label = j < labels.Length ? labels[j] : $"x{j + 1}",
                        reducedCost = Math.Round(rj, 6)
                    });
                }
            }

            return Ok(list);
        }

        // ---------- Range for a chosen nonbasic var ----------
        // GET /api/sensitivity/range-nonbasic/{index}
        [HttpGet("range-nonbasic/{index:int}")]
        public IActionResult RangeNonbasic(int index)
        {
            if (string.IsNullOrWhiteSpace(_cache.LastModelText))
                return BadRequest("No model text in memory. Solve a model first.");

            var (isMax, c, labels, A, senses) = ParseModel(_cache.LastModelText);
            if (A == null || A.Length == 0 || c == null)
                return BadRequest("Model parse failed.");

            int m = A.GetLength(0);
            int n = A.GetLength(1);
            if (index < 0 || index >= n)
                return BadRequest($"Variable index out of range. There are {n} variables (0-based).");

            var y = SolveDualForShadowPrices(_cache.LastModelText, out var canonicalNote);
            if (y == null || y.Length != m)
                return BadRequest("Could not obtain shadow prices for current model.");

            // y^T a_j
            double yDot = 0.0;
            for (int i = 0; i < m; i++)
                yDot += y[i] * A[i, index];

            double cj = index < c.Length ? c[index] : 0.0;
            double rj = cj - yDot;
            const double eps = 1e-9;

            // Outputs
            double allowInc, allowDec, lo, hi;
            bool incUnb, decUnb, loInf, hiInf;
            string explanation;

            if (isMax)
            {
                // MAX (Ax ≤ b, x ≥ 0): keep r_j ≤ 0
                allowInc = Math.Max(0.0, -rj);    // Δ+ = −rj
                allowDec = double.MaxValue;        // unbounded decrease
                incUnb = false;
                decUnb = true;

                lo = double.MinValue;              // (-∞, y^T a_j]
                hi = cj + allowInc;                // y^T a_j
                loInf = true;
                hiInf = false;

                string alt = Math.Abs(rj) <= eps
                    ? " (alternate optimum: allowed increase is 0, decrease unbounded)"
                    : "";

                explanation =
                    $"Because x{index + 1} is non-basic in a maximization, keeping r_j ≤ 0 preserves the current basis. " +
                    $"Here c_j = {cj:0.######}, r_j = {rj:0.######}, so y^T a_j = c_j − r_j = {yDot:0.######}. " +
                    $"You may increase c_j by at most −r_j = {(-rj):0.######} (up to {hi:0.######}); any decrease keeps optimality.{alt} {canonicalNote}";
            }
            else
            {
                // MIN (Ax ≥ b, x ≥ 0): keep r_j ≥ 0
                allowDec = Math.Max(0.0, rj);      // Δ− = rj
                allowInc = double.MaxValue;        // unbounded increase
                incUnb = true;
                decUnb = false;

                lo = cj - allowDec;                // y^T a_j
                hi = double.MaxValue;              // [y^T a_j, +∞)
                loInf = false;
                hiInf = true;

                string alt = Math.Abs(rj) <= eps
                    ? " (alternate optimum: allowed decrease is 0, increase unbounded)"
                    : "";

                explanation =
                    $"Because x{index + 1} is non-basic in a minimization, keeping r_j ≥ 0 preserves the current basis. " +
                    $"Here c_j = {cj:0.######}, r_j = {rj:0.######}, so y^T a_j = c_j − r_j = {yDot:0.######}. " +
                    $"You may decrease c_j by at most r_j = {rj:0.######} (down to {lo:0.######}); any increase keeps optimality.{alt} {canonicalNote}";
            }

            var resp = new RangeNonbasicResponse
            {
                index = index,
                label = index < labels.Length ? labels[index] : $"x{index + 1}",
                k = index,

                currentCoefficient = Math.Round(cj, 6),
                reducedCost = Math.Round(rj, 6),
                yDotNcol = Math.Round(yDot, 6),

                allowableIncrease = double.IsPositiveInfinity(allowInc) ? double.MaxValue : Math.Round(allowInc, 6),
                allowableDecrease = double.IsPositiveInfinity(allowDec) ? double.MaxValue : Math.Round(allowDec, 6),

                coefficientLowerBound = double.IsNegativeInfinity(lo) ? double.MinValue : Math.Round(lo, 6),
                coefficientUpperBound = double.IsPositiveInfinity(hi) ? double.MaxValue : Math.Round(hi, 6),

                increaseUnbounded = incUnb,
                decreaseUnbounded = decUnb,
                lowerIsNegInf = loInf,
                upperIsPosInf = hiInf,

                explanation = explanation
            };

            return Ok(resp);
        }

        // ---------- Shadow prices ----------
        // GET /api/sensitivity/shadow-prices
        [HttpGet("shadow-prices")]
        public ActionResult<object> GetShadowPrices()
        {
            if (string.IsNullOrWhiteSpace(_cache.LastModelText))
                return BadRequest("No model text in memory. Edit or solve a model first.");

            var y = SolveDualForShadowPrices(_cache.LastModelText, out _);
            if (y == null || y.Length == 0)
                return BadRequest("Could not compute shadow prices for the current model text.");

            int m = y.Length;
            var rhs = ExtractRhsForDisplay(_cache.LastModelText, m);

            var output = new List<object>(m);
            for (int k = 0; k < m; k++)
            {
                output.Add(new
                {
                    Constraint = $"c{k + 1}",
                    RHS = (k < rhs.Length ? rhs[k] : double.NaN),
                    ShadowPrice = Math.Round(y[k], 6)
                });
            }
            return Ok(new { success = true, output });
        }

        // ---------- helpers ----------

        private static (bool isMax, double[] c, string[] labels, double[,] A, string[] senses) ParseModel(string modelText)
        {
            var lines = modelText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                                 .Select(l => l.Trim())
                                 .Where(l => !string.IsNullOrWhiteSpace(l))
                                 .ToList();

            // objective
            var first = lines.FirstOrDefault(l => !l.StartsWith("#"));
            if (first == null)
                return (true, Array.Empty<double>(), Array.Empty<string>(), new double[0, 0], Array.Empty<string>());

            var objParts = first.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            bool isMax = objParts[0].Equals("max", StringComparison.OrdinalIgnoreCase);
            bool isMin = objParts[0].Equals("min", StringComparison.OrdinalIgnoreCase);

            var c = objParts.Skip(1).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            var labels = Enumerable.Range(1, c.Length).Select(i => $"x{i}").ToArray();

            // constraints: lines until restrictions
            var cons = new List<string>();
            var opAscii = new Regex(@"(<=|>=|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");
            var opUni = new Regex(@"(≤|≥|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");

            foreach (var ln in lines.Skip(1))
            {
                if (ln.StartsWith("#")) continue;
                var toks = ln.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                bool looksRestrictions = toks.All(t =>
                    t.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("int", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("urs", StringComparison.OrdinalIgnoreCase) ||
                    t == "+" || t == "-");
                if (looksRestrictions) break;

                if (opAscii.IsMatch(ln) || opUni.IsMatch(ln)) cons.Add(ln);
            }

            int m = cons.Count;
            int n = c.Length;
            var A = new double[m, n];
            var senses = new string[m];

            for (int i = 0; i < m; i++)
            {
                var ln = cons[i];
                var mm = opAscii.Match(ln);
                if (!mm.Success) mm = opUni.Match(ln);

                string opTok = mm.Groups[1].Value;
                senses[i] = opTok switch { "≤" => "<=", "≥" => ">=", _ => opTok };

                var coeffText = ln.Substring(0, mm.Index).Trim();
                var coeffs = coeffText.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (coeffs.Length != n)
                    throw new InvalidOperationException($"Constraint {i + 1} has {coeffs.Length} coeffs but objective has {n} variables.");

                for (int j = 0; j < n; j++)
                    A[i, j] = double.Parse(coeffs[j], CultureInfo.InvariantCulture);
            }

            return (isMax || !isMin, c, labels, A, senses);
        }

        // Extract the first m RHS values from lines containing <=, >=, =, ≤, ≥ (for display only)
        private static double[] ExtractRhsForDisplay(string? modelText, int m)
        {
            var vals = Enumerable.Repeat(double.NaN, Math.Max(m, 0)).ToArray();
            if (string.IsNullOrWhiteSpace(modelText) || m <= 0) return vals;

            var lines = modelText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
            var opAscii = new Regex(@"(<=|>=|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");
            var opUni = new Regex(@"(≤|≥|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");

            // indices of (up to m) constraint lines
            var idxs = lines
                .Select((text, idx) => new { text, idx })
                .Where(t => !string.IsNullOrWhiteSpace(t.text) && !t.text.TrimStart().StartsWith("#"))
                .Where(t =>
                {
                    var toks = t.text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    bool looksRestrictions = toks.Length > 0 && toks.All(tok =>
                        tok.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                        tok.Equals("int", StringComparison.OrdinalIgnoreCase) ||
                        tok.Equals("urs", StringComparison.OrdinalIgnoreCase) ||
                        tok == "+" || tok == "-");
                    return !looksRestrictions;
                })
                .Where(t => opAscii.IsMatch(t.text) || opUni.IsMatch(t.text))
                .Select(t => t.idx)
                .Take(m)
                .ToList();

            for (int k = 0; k < Math.Min(m, idxs.Count); k++)
            {
                string ln = lines[idxs[k]];
                var m1 = opAscii.Match(ln);
                if (!m1.Success) m1 = opUni.Match(ln);
                if (m1.Success && double.TryParse(m1.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var rhsVal))
                    vals[k] = rhsVal;
            }
            return vals;
        }

        // Build and solve the dual; return y (shadow prices). Works for canonical forms; otherwise a best-effort.
        private static double[] SolveDualForShadowPrices(string modelText, out string note)
        {
            note = "";
            var (isMax, c, labels, A, senses) = ParseModel(modelText);
            int m = A.GetLength(0);
            int n = A.GetLength(1);

            bool allLe = senses.All(s => s == "<=");
            bool allGe = senses.All(s => s == ">=");
            if (!((isMax && allLe) || (!isMax && allGe)))
            {
                note =
                    "Note: The model is not strictly canonical (max with all ≤ or min with all ≥). " +
                    "Shadow prices are computed from the dual solve as a best-effort approximation.";
            }

            // Parse RHS for dual objective
            var lines = modelText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                                 .Select(l => l.Trim())
                                 .Where(l => !string.IsNullOrWhiteSpace(l))
                                 .ToList();

            var cons = new List<(string ln, double rhs)>();
            var opAscii = new Regex(@"(<=|>=|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");
            var opUni = new Regex(@"(≤|≥|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");

            foreach (var ln in lines.Skip(1))
            {
                if (ln.StartsWith("#")) continue;
                var toks = ln.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                bool looksRestrictions = toks.All(t =>
                    t.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("int", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("urs", StringComparison.OrdinalIgnoreCase) ||
                    t == "+" || t == "-");
                if (looksRestrictions) break;

                var mm = opAscii.Match(ln);
                if (!mm.Success) mm = opUni.Match(ln);
                if (mm.Success)
                {
                    double rhs = double.Parse(mm.Groups[2].Value, CultureInfo.InvariantCulture);
                    cons.Add((ln, rhs));
                }
            }
            var b = cons.Select(t => t.rhs).ToArray(); // length m

            // Build A^T
            var At = new double[n, m];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                    At[j, i] = A[i, j];

            // Build dual text
            var sb = new StringBuilder();
            if (isMax && allLe)
            {
                // Primal: max, Ax <= b, x >= 0  -> Dual: min, A^T y >= c, y >= 0
                sb.Append("min ");
                for (int i = 0; i < m; i++)
                    sb.Append((i == 0 ? "" : " ") + b[i].ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();

                for (int j = 0; j < n; j++)
                {
                    for (int i = 0; i < m; i++)
                        sb.Append((i == 0 ? "" : " ") + At[j, i].ToString(CultureInfo.InvariantCulture));
                    sb.Append(" >= ");
                    sb.AppendLine(c[j].ToString(CultureInfo.InvariantCulture));
                }

                sb.AppendLine(string.Join(' ', Enumerable.Repeat("+", m)));  // y ≥ 0
            }
            else
            {
                // Treat as: min with Ax >= b, x >= 0 -> Dual: max, A^T y <= c, y >= 0
                sb.Append("max ");
                for (int i = 0; i < m; i++)
                    sb.Append((i == 0 ? "" : " ") + b[i].ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();

                for (int j = 0; j < n; j++)
                {
                    for (int i = 0; i < m; i++)
                        sb.Append((i == 0 ? "" : " ") + At[j, i].ToString(CultureInfo.InvariantCulture));
                    sb.Append(" <= ");
                    sb.AppendLine(c[j].ToString(CultureInfo.InvariantCulture));
                }

                sb.AppendLine(string.Join(' ', Enumerable.Repeat("+", m)));  // y ≥ 0
            }

            string dualText = sb.ToString();

            // Solve the dual; DualSimplex is a robust default
            var dualModel = ModelParser.Parse(dualText);
            ISolver solver = new DualSimplexSolver();
            var dualResult = solver.Solve(dualModel);

            if (!dualResult.Success || dualResult.X == null || dualResult.X.Length != m)
                return Array.Empty<double>();

            // dualResult.X are the y's
            return dualResult.X;
        }
    }
}
