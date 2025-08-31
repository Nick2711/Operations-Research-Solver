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
    }
}
