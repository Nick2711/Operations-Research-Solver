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
        // This is the ONLY action that handles POST /api/solve
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

                // ✅ Cache results for sensitivity + remember raw text
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

        // POST /api/solve/change-rhs
        [HttpPost("change-rhs")]
        public IActionResult ChangeRhs([FromBody] ChangeRhsRequest req)
        {
            // lines: 0 objective, ... constraints ..., last = variable restrictions
            var rawLines = _lastModelText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
            if (rawLines.Count < 2) return BadRequest("Model text too short.");

            // Build a list of actual constraint lines by detecting an operator (<=, >=, =, ≤, ≥)
            var opRegexAscii = new Regex(@"(<=|>=|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");
            var opRegexUnicode = new Regex(@"(≤|≥|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");

            var constraintLineIndices = rawLines
                .Select((text, idx) => new { text, idx })
                // skip empty lines and comments
                .Where(t => !string.IsNullOrWhiteSpace(t.text) && !t.text.TrimStart().StartsWith("#"))
                // exclude the last non-empty line if it's variable restrictions (all tokens are words like bin/int/urs/+/-)
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
                // keep lines that have an operator + RHS number
                .Where(t => opRegexAscii.IsMatch(t.text) || opRegexUnicode.IsMatch(t.text))
                .Select(t => t.idx)
                .ToList();

            if (constraintLineIndices.Count == 0)
                return BadRequest("No constraint lines with an operator were detected in the model.");

            if (req.ConstraintIndex < 0 || req.ConstraintIndex >= constraintLineIndices.Count)
                return BadRequest($"Constraint index out of range. Found {constraintLineIndices.Count} constraint(s), 0-based.");

            // Pick the requested constraint line by index into the detected list
            int lineIdx = constraintLineIndices[req.ConstraintIndex];
            string line = rawLines[lineIdx];

            // Find the operator+RHS on that line
            var m = opRegexAscii.Match(line);
            if (!m.Success) m = opRegexUnicode.Match(line);
            if (!m.Success)
                return BadRequest($"Could not find operator/RHS on constraint line:\n{line}");

            // Preserve whether there was a space between operator and number
            string opToken = m.Groups[1].Value;  // "<=", ">=", "=", "≤", "≥"
            bool hadSpace = m.Value.Contains(" ");
            string newRhsStr = req.NewRhs.ToString(CultureInfo.InvariantCulture);

            // Replace only the matched operator+number segment
            line = line.Remove(m.Index, m.Length)
                       .Insert(m.Index, hadSpace ? $"{opToken} {newRhsStr}" : $"{opToken}{newRhsStr}");

            rawLines[lineIdx] = line;

            var newText = string.Join("\n", rawLines);

            // Re-solve with Primal Simplex (or pick based on last request if you wish)
            var model = ModelParser.Parse(newText);
            ISolver solver = new PrimalSimplexSolver();
            var result = solver.Solve(model);

            // Update cache for subsequent actions
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

            // Split cached model into lines
            var lines = _lastModelText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
            if (lines.Count < 2) return BadRequest("Model text too short.");

            var objLine = lines[0].Trim();
            var objParts = objLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (objParts.Length < 2) return BadRequest("Objective line invalid.");
            int numVars = objParts.Length - 1;  // first token = max/min, rest are coeffs

            // Parse new constraint text: "<coeffs...> <op> <rhs>" (supports <=, >=, = and ≤/≥; space optional before RHS)
            var raw = req.ConstraintText.Trim();
            if (raw.StartsWith("#")) return BadRequest("Constraint cannot be a comment.");

            // Find operator and rhs with regex
            var m = Regex.Match(raw, @"(<=|>=|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");
            if (!m.Success) m = Regex.Match(raw, @"(≤|≥|=)\s*(-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)");
            if (!m.Success) return BadRequest("Could not find operator/RHS in the new constraint.");

            string opToken = m.Groups[1].Value;  // "<=", ">=", "=", "≤", "≥"
            string rhsText = m.Groups[2].Value;

            // Coeffs are everything before the operator match
            string coeffsRegion = raw.Substring(0, m.Index).Trim();
            var coeffTokens = coeffsRegion.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            if (coeffTokens.Length != numVars)
                return BadRequest($"Expected {numVars} coefficients but found {coeffTokens.Length}.");

            // Validate coefficients are numbers
            foreach (var t in coeffTokens)
                if (!double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                    return BadRequest($"Coefficient '{t}' is not a number.");

            // Validate RHS
            if (!double.TryParse(rhsText, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                return BadRequest($"RHS '{rhsText}' is not a number.");

            // Normalize the new constraint line: keep tokens as given; ensure a space before RHS
            var normalizedConstraint = $"{string.Join(' ', coeffTokens)} {opToken} {rhsText}";

            // Find the variable restrictions line (last non-empty line with only bin/int/urs/+/- tokens)
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

            // Insert the new constraint right before restrictions
            lines.Insert(restrictionsIdx, normalizedConstraint);

            var newText = string.Join("\n", lines);

            // Re-solve
            var model = ModelParser.Parse(newText);
            ISolver solver = new PrimalSimplexSolver();
            var result = solver.Solve(model);

            // Update cache
            _lastModelText = newText;
            _cache.LastResult = result;

            var output = string.Join("\n", result.Log) +
                         $"\n\nThe new objective value is: {result.ObjectiveValue:0.####}";

            return Content(output, "text/plain");
        }

        // ===================== APPLY DUALITY (build dual, solve, text output) =====================
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

            // Parse objective line
            var objParts = lines[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (objParts.Length < 2) return BadRequest("Invalid objective line.");

            bool isMax = objParts[0].Equals("max", StringComparison.OrdinalIgnoreCase);
            bool isMin = objParts[0].Equals("min", StringComparison.OrdinalIgnoreCase);
            if (!isMax && !isMin) return BadRequest("Objective must start with 'max' or 'min'.");

            var c = objParts.Skip(1)
                            .Select(s => double.Parse(s, CultureInfo.InvariantCulture))
                            .ToArray();
            int n = c.Length;

            // Detect constraints (supports <=, >=, = and Unicode ≤/≥)
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

            // Only handle the standard dual-friendly forms for now:
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

            // Build A^T
            var At = new double[n, m];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                    At[j, i] = A[i, j];

            // --- Build dual model text ---
            var sb = new StringBuilder();
            if (isMax && allLe)
            {
                // Primal: max, Ax <= b, x >= 0  -> Dual: min, A^T y >= c, y >= 0
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
                // Primal: min, Ax >= b, x >= 0  -> Dual: max, A^T y <= c, y >= 0
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

            // Solve dual with the correct flavor
            var dualModel = ModelParser.Parse(dualText);
            ISolver solver = (isMax && allLe) ? new DualSimplexSolver() : new DualSimplexSolver();

            var result = solver.Solve(dualModel);


            // Format like your normal solver output
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

            // Add a strong-duality line if we have primal
            if (_cache?.LastResult?.Success == true)
            {
                var primalZ = _cache.LastResult.ObjectiveValue;
                var dualZ = result.ObjectiveValue *-1;
                outText.AppendLine();
                outText.AppendLine($"Duality Strength (LP): primal z = {primalZ:0.###}, dual = {dualZ:0.###} " +
                                   (Math.Abs(primalZ - dualZ) < 1e-6 ? "This lp has a strong duality" : "This lp has a weak duality"));
            }

            // Show the dual model text at the very top for reference (optional — comment out if not desired)
            var finalText =
                "DUAL MODEL (auto-generated):\n" +
                dualText +
                "\n" +
                outText.ToString();

            return Content(finalText, "text/plain");
        }




    }
}
