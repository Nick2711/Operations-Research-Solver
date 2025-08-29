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

namespace Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public sealed class SolveController : ControllerBase
    {
        [HttpPost]
        public ActionResult<SolveResponse> Post([FromBody] SolveRequest req, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            if (req is null)
                return BadRequest(new SolveResponse { OutputText = "Error: Request body is null." });

            if (string.IsNullOrWhiteSpace(req.ModelText))
                return BadRequest(new SolveResponse { OutputText = "Error: ModelText is empty." });

            // Time limit (optional)
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (req.Settings != null && req.Settings.TimeLimitSeconds > 0)
                cts.CancelAfter(TimeSpan.FromSeconds(req.Settings.TimeLimitSeconds));
            var token = cts.Token;

            try
            {
                // Parse text -> model (ModelParser owns normalization/cleaning and algebraic fallback)
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

                // Pick solver from enum
                ISolver solver = req.Algorithm switch
                {
                    Algorithm.Knapsack01 => new Knapsack01Solver(),
                    Algorithm.BranchAndBound => new BranchAndBoundSimplexSolver(),
                    Algorithm.CuttingPlane => new CuttingPlaneSolver(),
                    Algorithm.RevisedSimplex => new RevisedSimplexSolver(),
                    Algorithm.PrimalSimplex => new PrimalSimplexSolver(),
                    _ => new PrimalSimplexSolver()
                };

                // If MIN and caller didn't explicitly request BranchAndBound/Knapsack, prefer Dual Simplex
                if (model.Direction == OptimizeDirection.Min &&
                    req.Algorithm != Algorithm.BranchAndBound &&
                    req.Algorithm != Algorithm.Knapsack01)
                {
                    solver = new DualSimplexSolver();
                }

                token.ThrowIfCancellationRequested();

                var result = solver.Solve(model);

                token.ThrowIfCancellationRequested();

                // Build output text
                var outText = new StringBuilder();
                outText.AppendLine($"Algorithm: {solver.Name}");

                if (result.Log != null)
                {
                    var linesToShow = (req.Settings?.Verbose ?? true)
                        ? result.Log
                        : result.Log.Take(100); // show more by default when not verbose
                    foreach (var l in linesToShow)
                        outText.AppendLine(l);
                }

                // Convert objective to the user's original sense (if solvers normalized internally)
                double userObjective = result.Success
                    ? (model.Direction == OptimizeDirection.Max
                        ? result.ObjectiveValue
                        : -result.ObjectiveValue)
                    : 0.0;

                outText.AppendLine();
                if (result.Success)
                {
                    outText.AppendLine($"SUCCESS — Objective: {userObjective.ToString("0.###", CultureInfo.InvariantCulture)}");

                    if (result.X is { Length: > 0 })
                    {
                        var sol = string.Join(", ",
                            result.X.Select((v, i) => $"x{i + 1}={(double.IsNaN(v) ? "NaN" : v.ToString("0.###", CultureInfo.InvariantCulture))}"));
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

                // 🔎 Echo normalized lines to diagnose invisible characters / tokenization issues
                string normalized;
                try
                {
                    normalized = string.Join("\n", ModelParser.DebugNormalizeToLines(req.ModelText));
                }
                catch
                {
                    normalized = "(failed to normalize)";
                }

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
    }
}
