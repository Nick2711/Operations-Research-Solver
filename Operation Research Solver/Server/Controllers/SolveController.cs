using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;           // << add this
using Microsoft.AspNetCore.Mvc;
using Shared.Models;

using Solver.Engine.IO;
using Solver.Engine.Simplex;

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

            // Time limit
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (req.Settings != null && req.Settings.TimeLimitSeconds > 0)
                cts.CancelAfter(TimeSpan.FromSeconds(req.Settings.TimeLimitSeconds));
            var token = cts.Token;

            // ----------- sanitize/normalize input lines -----------
            // 1) unify newlines
            var rawLines = (req.ModelText ?? string.Empty)
                .Replace("\r\n", "\n")
                .Split('\n');

            // 2) strip comments and squeeze whitespace
            static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Trim();

            var cleaned = rawLines
                .Select(l => l.Split('#')[0])                                  // strip comments after '#'
                .Select(Clean)                                                 // collapse whitespace
                .Where(l => l.Length > 0)                                      // drop empties
                .Select(l => Regex.Replace(l, @"\s*(<=|>=|=)\s*", " $1 "))     // ensure spaces around <=, >=, =
                .ToArray();

            if (cleaned.Length < 2)
            {
                sw.Stop();
                return BadRequest(new SolveResponse
                {
                    OutputText = "Error: Not enough lines after cleaning.",
                    RuntimeMs = sw.ElapsedMilliseconds
                });
            }
            // ------------------------------------------------------

            try
            {
                // Parse text -> model (use cleaned)
                var parsed = ModelParser.Parse(cleaned);

                if (parsed == null || parsed.Model == null)
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
                    Algorithm.PrimalSimplex => new PrimalSimplexSolver(),
                    Algorithm.RevisedSimplex => new RevisedSimplexSolverStub(),   // replace when implemented
                    _ => new PrimalSimplexSolver()
                };

                token.ThrowIfCancellationRequested();

                var result = solver.Solve(parsed.Model);

                token.ThrowIfCancellationRequested();

                // Build output text
                var outText = new StringBuilder();
                outText.AppendLine($"Algorithm: {solver.Name}");

                if (result.Log != null)
                {
                    var linesToShow = (req.Settings?.Verbose ?? true)
                        ? result.Log
                        : result.Log.Take(10);
                    foreach (var l in linesToShow)
                        outText.AppendLine(l);
                }

                outText.AppendLine();
                if (result.Success)
                {
                    outText.AppendLine($"SUCCESS — Objective: {result.ObjectiveValue:0.###}");
                    if (result.X is { Length: > 0 })
                        outText.AppendLine("Solution: " + string.Join(", ", result.X.Select((v, i) => $"x{i + 1}={v:0.###}")));
                }
                else
                {
                    outText.AppendLine(result.Unbounded ? "FAILED — Unbounded."
                                   : result.Infeasible ? "FAILED — Infeasible (Phase I needed)."
                                   : "FAILED — Unknown.");
                }

                sw.Stop();

                return Ok(new SolveResponse
                {
                    Success = result.Success,
                    Unbounded = result.Unbounded,
                    Infeasible = result.Infeasible,
                    Objective = result.Success ? Math.Round(result.ObjectiveValue, 3) : (double?)null,
                    SolutionSummary = result.Success && result.X is { Length: > 0 }
                        ? string.Join(", ", result.X.Select((v, i) => $"x{i + 1}={v:0.###}"))
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
                // include cleaned lines to help debug formatting issues
                var dbg = string.Join("\n", cleaned.Select((l, i) => $"{i}: {l}"));
                return BadRequest(new SolveResponse
                {
                    OutputText = $"Error: {ex.Message}\n\n[Cleaned lines]\n{dbg}",
                    RuntimeMs = sw.ElapsedMilliseconds
                });
            }
        }
    }
}
