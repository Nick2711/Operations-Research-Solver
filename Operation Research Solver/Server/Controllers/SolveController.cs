using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.AspNetCore.Mvc;
using Shared.Models;
using Solver.Engine.IO;
using Solver.Engine.Simplex;
using Solver.Engine.Core;
using Solver.Engine.Integer;
using Solver.Engine.CuttingPlanes;
using System.Text.RegularExpressions;
using System.Globalization;
using Operation_Research_Solver.Server.Services;

namespace Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")] // => /api/solve
    public sealed class SolveController : ControllerBase
    {
        private readonly ILastSolveCache _cache;
        private static string? _lastModelText;   // cached raw LP text for follow-up edits

        public SolveController(ILastSolveCache cache) => _cache = cache;

        // ===================== MAIN SOLVE =====================
        [HttpPost]
        public ActionResult<SolveResponse> Post([FromBody] SolveRequest req, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            if (req is null)
                return BadRequest(new SolveResponse { OutputText = "Error: Request body is null." });

            if (string.IsNullOrWhiteSpace(req.ModelText))
                return BadRequest(new SolveResponse { OutputText = "Error: ModelText is empty." });

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (req.Settings != null && req.Settings.TimeLimitSeconds > 0)
                cts.CancelAfter(TimeSpan.FromSeconds(req.Settings.TimeLimitSeconds));
            var token = cts.Token;

            try
            {
                // Parse
                var model = ModelParser.Parse(req.ModelText);
                if (model == null)
                {
                    sw.Stop();
                    return Ok(new SolveResponse
                    {
                        Success = false,
                        OutputText = "Failed to parse model.",
                        RuntimeMs = sw.ElapsedMilliseconds
                    });
                }

                // Pick solver
                ISolver solver = req.Algorithm switch
                {
                    Algorithm.Knapsack01 => new Knapsack01Solver(),
                    Algorithm.BranchAndBound => new BranchAndBoundSimplexSolver(),
                    Algorithm.CuttingPlane => new CuttingPlaneSolver(),
                    Algorithm.RevisedSimplex => new RevisedSimplexSolver(),
                    Algorithm.PrimalSimplex => new PrimalSimplexSolver(),
                    _ => new PrimalSimplexSolver()
                };

                if (model.Direction == OptimizeDirection.Min &&
                    req.Algorithm != Algorithm.BranchAndBound &&
                    req.Algorithm != Algorithm.Knapsack01)
                {
                    solver = new DualSimplexSolver();
                }

                token.ThrowIfCancellationRequested();

                var result = solver.Solve(model);

                // cache
                _cache.LastResult = result;
                _lastModelText = req.ModelText;

                token.ThrowIfCancellationRequested();

                // Build output
                var outText = new StringBuilder();
                outText.AppendLine($"Algorithm: {solver.Name}");

                if (result.Log != null)
                {
                    var linesToShow = (req.Settings?.Verbose ?? true)
                        ? result.Log
                        : result.Log.Take(100);
                    foreach (var l in linesToShow) outText.AppendLine(l);
                }

                double userObjective = result.Success
                    ? (model.Direction == OptimizeDirection.Max ? result.ObjectiveValue : -result.ObjectiveValue)
                    : 0.0;

                outText.AppendLine();
                if (result.Success)
                {
                    outText.AppendLine($"SUCCESS — Objective: {userObjective.ToString("0.###", CultureInfo.InvariantCulture)}");
                    if (result.X is { Length: > 0 })
                    {
                        var sol = string.Join(", ", result.X.Select((v, i) =>
                            $"x{i + 1}={(double.IsNaN(v) ? "NaN" : v.ToString("0.###", CultureInfo.InvariantCulture))}"));
                        outText.AppendLine("Solution: " + sol);
                    }
                }
                else
                {
                    outText.AppendLine(result.Unbounded ? "FAILED — Unbounded."
                                   : result.Infeasible ? "FAILED — Infeasible."
                                   : "FAILED — Unknown.");
                }

                sw.Stop();

                return Ok(new SolveResponse
                {
                    Success = result.Success,
                    Unbounded = result.Unbounded,
                    Infeasible = result.Infeasible,
                    Objective = result.Success ? Math.Round(userObjective, 3) : (double?)null,
                    SolutionSummary = result.Success && result.X is { Length: > 0 }
                        ? string.Join(", ", result.X.Select((v, i) => $"x{i + 1}={v.ToString("0.###", CultureInfo.InvariantCulture)}"))
                        : null,
                    OutputText = outText.ToString(),
                    RuntimeMs = sw.ElapsedMilliseconds
                });
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return StatusCode(408, new SolveResponse { OutputText = "Timed out.", RuntimeMs = sw.ElapsedMilliseconds });
            }
            catch (Exception ex)
            {
                sw.Stop();
                string normalized;
                try { normalized = string.Join("\n", ModelParser.DebugNormalizeToLines(req.ModelText)); }
                catch { normalized = "(failed to normalize)"; }

                var msg = new StringBuilder();
                msg.AppendLine($"Error: {ex.Message}");
                msg.AppendLine();
                msg.AppendLine("--- Normalized lines ---");
                msg.AppendLine(normalized);

                return BadRequest(new SolveResponse
                {
                    Success = false,
                    OutputText = msg.ToString(),
                    Objective = null,
                    SolutionSummary = null,
                    RuntimeMs = sw.ElapsedMilliseconds
                });
            }
        }

        // ===================== CHANGE RHS (re-solve) =====================
        public record ChangeRhsRequest(int ConstraintIndex, double NewRhs);

        [HttpPost("change-rhs")]
        public IActionResult ChangeRhs([FromBody] ChangeRhsRequest req)
        {
            var rawLines = _lastModelText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
            if (rawLines.Count < 2) return BadRequest("Model text too short.");

            var opRegexAscii = new Regex(@"(<=|>=|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");
            var opRegexUnicode = new Regex(@"(≤|≥|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");

            var constraintLineIndices = rawLines
                .Select((text, idx) => new { text, idx })
                .Where(t => !string.IsNullOrWhiteSpace(t.text) && !t.text.TrimStart().StartsWith("#"))
                .Where(t =>
                {
                    var tokens = t.text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    bool looksLikeRestrictions = tokens.All(tok =>
                        tok.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                        tok.Equals("int", StringComparison.OrdinalIgnoreCase) ||
                        tok.Equals("urs", StringComparison.OrdinalIgnoreCase) ||
                        tok == "+" || tok == "-");
                    return !looksLikeRestrictions;
                })
                .Where(t => opRegexAscii.IsMatch(t.text) || opRegexUnicode.IsMatch(t.text))
                .Select(t => t.idx)
                .ToList();

            if (constraintLineIndices.Count == 0)
                return BadRequest("No constraint lines with an operator were detected in the model.");

            if (req.ConstraintIndex < 0 || req.ConstraintIndex >= constraintLineIndices.Count)
                return BadRequest($"Constraint index out of range. Found {constraintLineIndices.Count} constraint(s), 0-based.");

            int lineIdx = constraintLineIndices[req.ConstraintIndex];
            string line = rawLines[lineIdx];

            var m = opRegexAscii.Match(line);
            if (!m.Success) m = opRegexUnicode.Match(line);
            if (!m.Success)
                return BadRequest($"Could not find operator/RHS on constraint line:\n{line}");

            string opToken = m.Groups[1].Value;
            bool hadSpace = m.Value.Contains(" ");
            string newRhsStr = req.NewRhs.ToString(CultureInfo.InvariantCulture);

            line = line.Remove(m.Index, m.Length)
                       .Insert(m.Index, hadSpace ? $"{opToken} {newRhsStr}" : $"{opToken}{newRhsStr}");

            rawLines[lineIdx] = line;

            var newText = string.Join("\n", rawLines);

            var model = ModelParser.Parse(newText);
            ISolver solver = new PrimalSimplexSolver();
            var result = solver.Solve(model);

            _lastModelText = newText;
            _cache.LastResult = result;

            var output = string.Join("\n", result.Log) +
                         $"\n\nThe new objective value is: {result.ObjectiveValue:0.####}";

            return Content(output, "text/plain");
        }

        // ===================== ADD CONSTRAINT (re-solve) =====================
        public record AddConstraintRequest(string ConstraintText);

        [HttpPost("add-constraint")]
        public IActionResult AddConstraint([FromBody] AddConstraintRequest req)
        {
            if (string.IsNullOrWhiteSpace(_lastModelText))
                return BadRequest("No model text in memory. Solve a model first.");

            if (string.IsNullOrWhiteSpace(req.ConstraintText))
                return BadRequest("Constraint text is empty.");

            var lines = _lastModelText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
            if (lines.Count < 2) return BadRequest("Model text too short.");

            var objLine = lines[0].Trim();
            var objParts = objLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (objParts.Length < 2) return BadRequest("Objective line invalid.");
            int numVars = objParts.Length - 1;

            var raw = req.ConstraintText.Trim();
            if (raw.StartsWith("#")) return BadRequest("Constraint cannot be a comment.");

            var m = Regex.Match(raw, @"(<=|>=|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");
            if (!m.Success) m = Regex.Match(raw, @"(≤|≥|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");
            if (!m.Success) return BadRequest("Could not find operator/RHS in the new constraint.");

            string opToken = m.Groups[1].Value;
            string rhsText = m.Groups[2].Value;

            string coeffsRegion = raw.Substring(0, m.Index).Trim();
            var coeffTokens = coeffsRegion.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            if (coeffTokens.Length != numVars)
                return BadRequest($"Expected {numVars} coefficients but found {coeffTokens.Length}.");

            foreach (var t in coeffTokens)
                if (!double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                    return BadRequest($"Coefficient '{t}' is not a number.");

            if (!double.TryParse(rhsText, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                return BadRequest($"RHS '{rhsText}' is not a number.");

            var normalizedConstraint = $"{string.Join(' ', coeffTokens)} {opToken} {rhsText}";

            int restrictionsIdx = -1;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var text = lines[i].Trim();
                if (string.IsNullOrEmpty(text)) continue;

                var toks = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                bool looksLikeRestrictions = toks.All(tok =>
                    tok.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    tok.Equals("int", StringComparison.OrdinalIgnoreCase) ||
                    tok.Equals("urs", StringComparison.OrdinalIgnoreCase) ||
                    tok == "+" || tok == "-");

                if (looksLikeRestrictions)
                {
                    restrictionsIdx = i;
                    break;
                }
            }

            if (restrictionsIdx == -1)
                return BadRequest("Could not locate variable restrictions line to insert before.");

            lines.Insert(restrictionsIdx, normalizedConstraint);

            var newText = string.Join("\n", lines);

            var model = ModelParser.Parse(newText);
            ISolver solver = new PrimalSimplexSolver();
            var result = solver.Solve(model);

            _lastModelText = newText;
            _cache.LastResult = result;

            var output = string.Join("\n", result.Log) +
                         $"\n\nThe new objective value is: {result.ObjectiveValue:0.####}";

            return Content(output, "text/plain");
        }

        // ===================== APPLY DUALITY =====================
        public record ApplyDualityResponse(string DualModelText, string OutputText, string Summary);

        [HttpPost("apply-duality")]
        public IActionResult ApplyDuality()
        {
            if (string.IsNullOrWhiteSpace(_lastModelText))
                return BadRequest("No model text in memory. Solve a model first.");

            var lines = _lastModelText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                                      .Where(l => !string.IsNullOrWhiteSpace(l))
                                      .Select(l => l.Trim())
                                      .ToList();
            if (lines.Count < 2) return BadRequest("Model text too short.");

            var objParts = lines[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (objParts.Length < 2) return BadRequest("Invalid objective line.");

            bool isMax = objParts[0].Equals("max", StringComparison.OrdinalIgnoreCase);
            bool isMin = objParts[0].Equals("min", StringComparison.OrdinalIgnoreCase);
            if (!isMax && !isMin) return BadRequest("Objective must start with 'max' or 'min'.");

            var c = objParts.Skip(1)
                            .Select(s => double.Parse(s, CultureInfo.InvariantCulture))
                            .ToArray();
            int n = c.Length;

            var opAscii = new Regex(@"(<=|>=|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");
            var opUni = new Regex(@"(≤|≥|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");

            var constraintLines = new List<string>();
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

                if (opAscii.IsMatch(ln) || opUni.IsMatch(ln))
                    constraintLines.Add(ln);
            }

            int m = constraintLines.Count;
            if (m == 0) return BadRequest("No constraints detected.");

            var A = new double[m, n];
            var rhs = new double[m];
            var sense = new string[m];

            for (int i = 0; i < m; i++)
            {
                string ln = constraintLines[i];
                var m1 = opAscii.Match(ln);
                if (!m1.Success) m1 = opUni.Match(ln);
                if (!m1.Success) return BadRequest($"Could not parse operator/RHS in: {ln}");

                string opTok = m1.Groups[1].Value;
                string rhsTok = m1.Groups[2].Value;
                rhs[i] = double.Parse(rhsTok, CultureInfo.InvariantCulture);
                sense[i] = opTok switch { "≤" => "<=", "≥" => ">=", _ => opTok };

                var coeffText = ln.Substring(0, m1.Index).Trim();
                var coeffs = coeffText.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (coeffs.Length != n)
                    return BadRequest($"Constraint {i} has {coeffs.Length} coeffs but objective has {n} variables.");

                for (int j = 0; j < n; j++)
                    A[i, j] = double.Parse(coeffs[j], CultureInfo.InvariantCulture);
            }

            bool allLe = sense.All(s => s == "<=");
            bool allGe = sense.All(s => s == ">=");
            if (!((isMax && allLe) || (isMin && allGe)))
            {
                return BadRequest(
                    "Apply Duality currently supports:\n" +
                    "• max with all constraints <= (x ≥ 0)\n" +
                    "• min with all constraints >= (x ≥ 0)\n" +
                    "Please convert to a supported standard form first."
                );
            }

            var At = new double[n, m];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                    At[j, i] = A[i, j];

            var sb = new StringBuilder();
            if (isMax && allLe)
            {
                sb.Append("min ");
                for (int i = 0; i < m; i++)
                    sb.Append((i == 0 ? "" : " ") + rhs[i].ToString(CultureInfo.InvariantCulture));
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
                sb.Append("max ");
                for (int i = 0; i < m; i++)
                    sb.Append((i == 0 ? "" : " ") + rhs[i].ToString(CultureInfo.InvariantCulture));
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

            var dualModel = ModelParser.Parse(dualText);
            ISolver solver = (isMax && allLe) ? new DualSimplexSolver() : new DualSimplexSolver();

            var result = solver.Solve(dualModel);

            var outText = new StringBuilder();
            outText.AppendLine($"Algorithm: {solver.Name}");
            if (result.Log != null)
                foreach (var line in result.Log) outText.AppendLine(line);

            if (result.Success)
            {
                outText.AppendLine();
                var invert = result.ObjectiveValue * -1;
                outText.AppendLine($"SUCCESS — Objective: {invert.ToString("0.###", CultureInfo.InvariantCulture)}");
                if (result.X is { Length: > 0 })
                {
                    var sol = string.Join(", ", result.X.Select((v, i) =>
                        $"y{i + 1}={(double.IsNaN(v) ? "NaN" : v.ToString("0.###", CultureInfo.InvariantCulture))}"));
                    outText.AppendLine("Solution: " + sol);
                }
            }
            else
            {
                outText.AppendLine(result.Unbounded ? "FAILED — Unbounded."
                                 : result.Infeasible ? "FAILED — Infeasible."
                                 : "FAILED — Unknown.");
            }

            if (_cache?.LastResult?.Success == true)
            {
                var primalZ = _cache.LastResult.ObjectiveValue;
                var dualZ = result.ObjectiveValue * -1;
                outText.AppendLine();
                outText.AppendLine($"Duality Strength (LP): primal z = {primalZ:0.###}, dual = {dualZ:0.###} " +
                                   (Math.Abs(primalZ - dualZ) < 1e-6 ? "This lp has a strong duality" : "This lp has a weak duality"));
            }

            var finalText =
                "DUAL MODEL (auto-generated):\n" +
                dualText +
                "\n" +
                outText.ToString();

            return Content(finalText, "text/plain");
        }

        // ===================== RANGE OF RHS =====================
        /*[HttpGet("rhs-ranges")]
        public IActionResult GetRhsRanges()
        {
            if (_cache?.LastResult == null || !_cache.LastResult.Success)
                return BadRequest("No solved model in memory.");

            var sens = _cache.LastResult.Sensitivity;
            if (sens == null)
                return BadRequest("Sensitivity details missing. Solve with Primal Simplex.");

            var BInv = sens.BInv;          // m x m
            var xB = sens.b;               // length m (basic RHS)
            var y = sens.ShadowPrices;     // duals

            int m = xB.Length;
            if (BInv == null || BInv.GetLength(0) != m || BInv.GetLength(1) != m)
                return BadRequest("Internal error: BInv shape mismatch.");

            // Try to read current RHS numbers from cached model text for display
            double[] rhsDisplay = new double[m];
            for (int i = 0; i < m; i++) rhsDisplay[i] = double.NaN;

            if (!string.IsNullOrWhiteSpace(_lastModelText))
            {
                var lines = _lastModelText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
                var opAscii = new Regex(@"(<=|>=|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");
                var opUni = new Regex(@"(≤|≥|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");

                var constraintIdxs = lines
                    .Select((text, idx) => new { text, idx })
                    .Where(t => !string.IsNullOrWhiteSpace(t.text) && !t.text.TrimStart().StartsWith("#"))
                    .Where(t =>
                    {
                        var toks = t.text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        bool looksRestr = toks.All(tok =>
                            tok.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                            tok.Equals("int", StringComparison.OrdinalIgnoreCase) ||
                            tok.Equals("urs", StringComparison.OrdinalIgnoreCase) ||
                            tok == "+" || tok == "-");
                        return !looksRestr;
                    })
                    .Where(t => opAscii.IsMatch(t.text) || opUni.IsMatch(t.text))
                    .Select(t => t.idx)
                    .ToList();

                for (int k = 0; k < Math.Min(m, constraintIdxs.Count); k++)
                {
                    string ln = lines[constraintIdxs[k]];
                    var m1 = opAscii.Match(ln);
                    if (!m1.Success) m1 = opUni.Match(ln);
                    if (m1.Success && double.TryParse(m1.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var rhsVal))
                        rhsDisplay[k] = rhsVal;
                }
            }

            var inv = CultureInfo.InvariantCulture;
            var results = new List<object>();

            for (int k = 0; k < m; k++)
            {
                double low = double.NegativeInfinity; // lower bound for Δ
                double high = double.PositiveInfinity; // upper bound for Δ

                for (int j = 0; j < m; j++)
                {
                    double w = BInv[j, k];
                    if (Math.Abs(w) < 1e-12) continue;

                    double bound = -xB[j] / w;
                    if (w > 0)
                    {
                        if (bound > low) low = bound;   // Δ >= bound
                    }
                    else
                    {
                        if (bound < high) high = bound; // Δ <= bound
                    }
                }

                // Convert to strings (no Infinity/NaN in JSON)
                string decOut = double.IsNegativeInfinity(low)
                    ? "unbounded"
                    : Math.Max(0.0, -low).ToString("0.###", inv);

                string incOut = double.IsPositiveInfinity(high)
                    ? "unbounded"
                    : Math.Max(0.0, high).ToString("0.###", inv);

                // Ensure RHS and ShadowPrice are JSON-safe numbers or null
                double? rhsOut = (double.IsNaN(rhsDisplay[k]) || double.IsInfinity(rhsDisplay[k]))
                    ? (double?)null
                    : rhsDisplay[k];

                double spRaw = (k >= 0 && k < y.Length) ? y[k] : double.NaN;
                double? spOut = (double.IsNaN(spRaw) || double.IsInfinity(spRaw))
                    ? (double?)null
                    : Math.Round(spRaw, 6);

                results.Add(new
                {
                    Constraint = $"c{k + 1}",
                    RHS = rhsOut,                 // null if not available
                    ShadowPrice = spOut,          // null if NaN/∞
                    AllowableDecrease = decOut,   // strings: numbers or "unbounded"
                    AllowableIncrease = incOut
                });
            }

            return Ok(new { success = true, output = results });
        }*/

        // ===================== ADD ACTIVITY (re-solve) =====================
        public record AddActivityRequest(double ObjCoeff, double[] Coeffs, string VarTag);

        [HttpOptions("add-activity")]
        public IActionResult OptionsAddActivity()
        {
            // let the browser's preflight succeed
            Response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
            Response.Headers["Access-Control-Allow-Headers"] = "content-type";
            return Ok();
        }

        [HttpPost("add-activity")]
        public IActionResult AddActivity([FromBody]AddActivityRequest req)
        {
            if (string.IsNullOrWhiteSpace(_lastModelText))
                return BadRequest("No model text in memory. Solve a model first.");

            // Parse cached model into segments
            var inv = CultureInfo.InvariantCulture;
            var lines = _lastModelText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
            if (lines.Count < 2) return BadRequest("Model text too short.");

            // objective is first line
            var objLine = lines[0].Trim();
            if (string.IsNullOrWhiteSpace(objLine)) return BadRequest("Objective line missing.");

            // find restrictions line (last non-empty line with only + - int bin urs)
            int restrictionsIdx = -1;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var t = lines[i].Trim();
                if (string.IsNullOrEmpty(t)) continue;
                var toks = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                bool looksRestr = toks.All(tok =>
                    tok.Equals("+", StringComparison.OrdinalIgnoreCase) ||
                    tok.Equals("-", StringComparison.OrdinalIgnoreCase) ||
                    tok.Equals("int", StringComparison.OrdinalIgnoreCase) ||
                    tok.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    tok.Equals("urs", StringComparison.OrdinalIgnoreCase));
                if (looksRestr) { restrictionsIdx = i; break; }
            }
            if (restrictionsIdx == -1) return BadRequest("Could not locate variable restrictions line.");

            // constraint lines are 1..(restrictionsIdx-1)
            var constraintIdxs = Enumerable.Range(1, restrictionsIdx - 1).ToList();
            int m = constraintIdxs.Count;
            if (req.Coeffs == null || req.Coeffs.Length != m)
                return BadRequest($"Expected {m} coefficients for the new variable, got {req.Coeffs?.Length ?? 0}.");

            // helper: signed formatting like +3 / -2
            string Signed(double v) => (v >= 0 ? "+" : "") + v.ToString(inv);

            // 1) append objective coefficient
            objLine = objLine + " " + Signed(req.ObjCoeff);
            lines[0] = objLine;

            // 2) insert new column coeff before each constraint’s operator
            var opAscii = new Regex(@"(<=|>=|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");
            var opUni = new Regex(@"(≤|≥|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");

            for (int k = 0; k < m; k++)
            {
                int idx = constraintIdxs[k];
                string ln = lines[idx];

                var match = opAscii.Match(ln);
                if (!match.Success) match = opUni.Match(ln);
                if (!match.Success) return BadRequest($"Could not find operator/RHS in constraint line:\n{ln}");

                // split "lhs  op  rhs" and inject new coeff at end of lhs
                string lhs = ln.Substring(0, match.Index).TrimEnd();
                string tail = ln.Substring(match.Index);
                lhs = string.IsNullOrEmpty(lhs) ? Signed(req.Coeffs[k]) : $"{lhs} {Signed(req.Coeffs[k])}";
                lines[idx] = lhs + " " + tail;
            }

            // 3) append variable tag to restrictions (default to "+")
            var tag = string.IsNullOrWhiteSpace(req.VarTag) ? "+" : req.VarTag.Trim();
            lines[restrictionsIdx] = (lines[restrictionsIdx].Trim() + " " + tag).Trim();

            // rebuild text and re-solve
            var newText = string.Join("\n", lines);
            var model = ModelParser.Parse(newText);
            if (model == null) return BadRequest("Model parse failed after adding activity.");

            ISolver solver = new PrimalSimplexSolver();
            var result = solver.Solve(model);

            // update cache
            _lastModelText = newText;
            _cache.LastResult = result;

            var output = string.Join("\n", result.Log ?? new List<string>()) +
                         $"\n\nThe new objective value is: {result.ObjectiveValue:0.####}";
            return Content(output, "text/plain");
        }





    }
}
