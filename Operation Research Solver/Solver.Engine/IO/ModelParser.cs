using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Solver.Engine.Core;

namespace Solver.Engine.IO
{
    /// <summary>
    /// Tolerant LP text parser with numeric format (primary) and algebraic fallback.
    /// Numeric format (preferred):
    ///   max 60 30 20
    ///   8 6 1 <= 48
    ///   4 2 1.5 <= 20
    ///   2 1.5 0.5 <= 8
    ///   + + +               // "+", "-", "urs", "int", "bin"
    ///
    /// Algebraic fallback (accepted):
    ///   Max z = 4x1 + 2x2 + 2x3 + x4 + 10x5
    ///   12x1 + 2x2 + x3 + x4 + 4x5 <= 15
    ///   xi (i = 1..5) = 0 or 1
    ///
    /// Notes: comments (# or //) stripped; Unicode (≤ ≥ − NBSP) normalized; commas→dots; glued ≤≥ allowed.
    /// </summary>
    public static class ModelParser
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// Entry point: try numeric first; if it fails, try algebraic; if both fail, surface the numeric error details.
        /// </summary>
        public static LpModel Parse(string rawText)
        {
            if (rawText == null) throw new ArgumentNullException(nameof(rawText));

            Exception? numericErr = null;

            // 1) Numeric fast path
            try
            {
                var lines0 = NormalizeToLines(rawText);
                return Parse(lines0).Model;
            }
            catch (Exception exNum)
            {
                numericErr = exNum;
                // fall through to algebraic
            }

            // 2) Algebraic fallback
            try
            {
                var converted = TryAlgebraicToNumeric(rawText);
                if (converted != null)
                {
                    var lines1 = NormalizeToLines(converted);
                    return Parse(lines1).Model;
                }
            }
            catch (Exception exAlg)
            {
                // if algebraic fallback itself throws, prefer that message (usually very specific)
                throw new InvalidOperationException("Algebraic input error: " + exAlg.Message, exAlg);
            }

            // 3) Both failed — include the numeric error for diagnosis.
            var msg = new StringBuilder();
            msg.AppendLine("Input string was not in a correct format.");
            if (numericErr != null)
            {
                msg.AppendLine();
                msg.AppendLine("Numeric parse error:");
                msg.AppendLine("  " + numericErr.Message);
            }
            msg.AppendLine();
            msg.AppendLine("Use numeric, e.g.:");
            msg.AppendLine("  max 4 2 2 1 10");
            msg.AppendLine("  12 2 1 1 4 <= 15");
            msg.AppendLine("  bin bin bin bin bin");
            msg.AppendLine("or algebraic, e.g.:");
            msg.AppendLine("  Max z = 4x1 + 2x2 + 2x3 + x4 + 10x5");
            msg.AppendLine("  12x1 + 2x2 + x3 + x4 + 4x5 <= 15");
            msg.AppendLine("  xi (i = 1..5) = 0 or 1");

            throw new InvalidOperationException(msg.ToString(), numericErr);
        }

        /// <summary>
        /// Original API used internally: parse from cleaned lines and return a ParseResult wrapper.
        /// </summary>
        public static ParseResult Parse(string[] lines)
        {
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            if (lines.Length < 2)
                throw new InvalidOperationException("Input must contain objective, at least one constraint, and sign restrictions.");

            int lineIdx = 0;

            // ── Objective ─────────────────────────────────────────────────────────
            var objective = lines[lineIdx++].Trim();
            var toks = objective.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length < 2)
                throw new InvalidOperationException("Objective line must have direction ('max'/'min') and at least one coefficient.");

            var first = toks[0].ToLowerInvariant();
            var dir = first switch
            {
                "max" or "maximize" or "maximum" => OptimizeDirection.Max,
                "min" or "minimize" or "minimum" => OptimizeDirection.Min,
                _ => throw new InvalidOperationException("Objective must start with 'max' or 'min'.")
            };

            double[] objCoeffs;
            try { objCoeffs = toks.Skip(1).Select(ParseNumber).ToArray(); }
            catch (Exception e) { throw new InvalidOperationException($"Objective coefficients invalid: {e.Message}"); }

            if (objCoeffs.Length == 0)
                throw new InvalidOperationException("Objective must contain at least one coefficient.");

            var model = new LpModel(dir);
            for (int i = 0; i < objCoeffs.Length; i++)
                model.Variables.Add(new Variable($"x{i + 1}", objCoeffs[i], SignRestriction.Urs)); // will set signs later

            // ── Constraints (all lines until LAST) ────────────────────────────────
            var constraints = new List<Constraint>();
            int constraintNo = 0;
            while (lineIdx < lines.Length - 1) // reserve last for signs
            {
                var rawLine = lines[lineIdx++].Trim();
                if (string.IsNullOrWhiteSpace(rawLine)) continue;
                constraintNo++;

                var ctoks = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (ctoks.Count < model.NumVars + 1)
                    throw new InvalidOperationException($"Constraint {constraintNo}: too few tokens (need coefficients + relation + RHS).");

                // Try "glued" last token (e.g., <=48) first, else read last two tokens
                Relation rel;
                double rhs;
                var lastTok = ctoks[^1];

                if (TrySplitRelationRhs(lastTok, out rel, out rhs))
                {
                    ctoks.RemoveAt(ctoks.Count - 1);
                }
                else
                {
                    if (ctoks.Count < model.NumVars + 2)
                        throw new InvalidOperationException($"Constraint {constraintNo}: missing relation/RHS.");

                    rel = ParseRelation(ctoks[^2]);      // "<=", ">=", "="
                    rhs = ParseNumber(ctoks[^1]);        // number
                    ctoks.RemoveRange(ctoks.Count - 2, 2);
                }

                if (ctoks.Count != model.NumVars)
                    throw new InvalidOperationException($"Constraint {constraintNo}: expected {model.NumVars} coefficients, got {ctoks.Count}.");

                double[] coeffs;
                try { coeffs = ctoks.Select(ParseSignedNumber).ToArray(); }
                catch (Exception e)
                {
                    throw new InvalidOperationException($"Constraint {constraintNo}: coefficient invalid — {e.Message}");
                }

                constraints.Add(new Constraint(coeffs, rel, rhs));
            }

            if (constraints.Count == 0)
                throw new InvalidOperationException("At least one constraint line is required before the signs line.");

            model.Constraints.AddRange(constraints);

            // ── Sign restrictions (last line) ────────────────────────────────────
            var signsLine = lines[^1].Trim();
            var signToks = SplitTokens(signsLine).ToList();
            if (signToks.Count == 0)
                throw new InvalidOperationException("Sign line is empty (expected tokens like '+', '-', 'urs', 'int', 'bin').");

            if (signToks.Count == 1 && model.NumVars > 1)
                signToks = Enumerable.Repeat(signToks[0], model.NumVars).ToList();

            if (signToks.Count != model.NumVars)
                throw new InvalidOperationException($"Sign line: expected {model.NumVars} sign tokens, got {signToks.Count}.");

            try
            {
                for (int i = 0; i < model.NumVars; i++)
                    model.Variables[i].Sign = ParseSignToken(signToks[i]);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Sign line invalid — {e.Message}");
            }

            return new ParseResult(model);
        }

        // ── Normalization ────────────────────────────────────────────────────────

        private static string[] NormalizeToLines(string raw)
        {
            if (raw.Length > 0 && raw[0] == '\uFEFF') raw = raw[1..]; // BOM

            raw = raw.Replace("\r\n", "\n").Replace("\r", "\n")
                     .Replace('\u00A0', ' ')              // NBSP
                     .Replace('–', '-').Replace('—', '-')  // dashes
                     .Replace('−', '-')                   // math minus
                     .Replace("≤", "<=").Replace("≥", ">=");

            // Strip comments
            var noComments = new List<string>();
            foreach (var line in raw.Split('\n'))
            {
                var s = line;
                int iHash = s.IndexOf('#');
                if (iHash >= 0) s = s[..iHash];
                int iSlashes = s.IndexOf("//", StringComparison.Ordinal);
                if (iSlashes >= 0) s = s[..iSlashes];
                s = s.Trim();
                if (s.Length > 0) noComments.Add(s);
            }

            var text = string.Join("\n", noComments);

            // Decimal commas → dot
            text = text.Replace(',', '.');

            // Put spaces around operators so tokenization is predictable
            text = Regex.Replace(text, @"<=", " <= ", RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @">=", " >= ", RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @"(?<![<>!])=(?!=)", " = ", RegexOptions.CultureInvariant);

            // Collapse multiple spaces
            text = Regex.Replace(text, @"[ \t]+", " ", RegexOptions.CultureInvariant);

            // Back to lines
            return text.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        }

        /// <summary>Expose normalization for controller error messages.</summary>
        public static string[] DebugNormalizeToLines(string raw) => NormalizeToLines(raw);

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static Relation ParseRelation(string s) => s switch
        {
            "<=" => Relation.LessOrEqual,
            ">=" => Relation.GreaterOrEqual,
            "=" => Relation.Equal,
            _ => throw new InvalidOperationException($"Invalid relation: {s}")
        };

        private static bool TrySplitRelationRhs(string token, out Relation rel, out double rhs)
        {
            rel = Relation.LessOrEqual; rhs = 0;
            foreach (var r in new[] { "<=", ">=", "=" })
            {
                int idx = token.IndexOf(r, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    rel = ParseRelation(r);
                    var rhsStr = token[(idx + r.Length)..];
                    if (rhsStr.Length == 0) return false;
                    rhs = ParseNumber(rhsStr);
                    return true;
                }
            }
            return false;
        }

        private static double ParseSignedNumber(string s) => ParseNumber(s);

        private static double ParseNumber(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                throw new InvalidOperationException("Invalid number token: (empty)");
            var t = s.StartsWith("+") ? s[1..] : s; // allow leading '+'
            if (double.TryParse(t, NumberStyles.Float, Inv, out var val))
                return val;
            throw new InvalidOperationException($"Invalid number token: {s}");
        }

        private static IEnumerable<string> SplitTokens(string s)
            => Regex.Split(s, @"[\s,;]+").Where(t => !string.IsNullOrWhiteSpace(t));

        private static SignRestriction ParseSignToken(string tok)
        {
            var k = tok.Trim().ToLowerInvariant();
            return k switch
            {
                "+" or "plus" or "nonneg" or "non-negative" => SignRestriction.Plus,
                "-" or "minus" => SignRestriction.Minus,
                "urs" or "free" or "unrestricted" => SignRestriction.Urs,
                "i" or "int" or "integer" => SignRestriction.Int,
                "b" or "bin" or "binary" => SignRestriction.Bin,
                _ => throw new InvalidOperationException($"Invalid sign restriction: {tok}")
            };
        }

        // ── Algebraic fallback ──────────────────────────────────────────────────

        private static string? TryAlgebraicToNumeric(string raw)
        {
            string s = raw.Replace("\r\n", "\n").Replace("\r", "\n")
                          .Replace('\u00A0', ' ')
                          .Replace('–', '-').Replace('—', '-').Replace('−', '-')
                          .Replace("≤", "<=").Replace("≥", ">=");

            var lines = s.Split('\n')
                         .Select(l => l.Trim())
                         .Where(l => l.Length > 0 && !l.StartsWith("#") && !l.StartsWith("//"))
                         .ToList();

            if (lines.Count < 2) return null;
            if (!Regex.IsMatch(lines[0], @"^\s*(max|min)\b", RegexOptions.IgnoreCase) &&
                !lines.Any(l => Regex.IsMatch(l, @"x\s*\d+", RegexOptions.IgnoreCase)))
                return null; // probably not algebraic

            // Objective
            var objLine = lines[0];
            var dirMatch = Regex.Match(objLine, @"^\s*(max|min)\b", RegexOptions.IgnoreCase);
            if (!dirMatch.Success) return null;
            string dir = dirMatch.Value.Trim().ToLowerInvariant().StartsWith("min") ? "min" : "max";
            objLine = Regex.Replace(objLine, @"^\s*(max|min)\s*(z)?\s*(=)?\s*", "", RegexOptions.IgnoreCase);

            // First constraint with a relation
            int conIdx = lines.FindIndex(1, l => Regex.IsMatch(l, @"(<=|>=|=)"));
            if (conIdx == -1) return null;
            var conLine = lines[conIdx];

            // Detect global bin line
            bool allBin =
                lines.Any(l => Regex.IsMatch(l, @"x\s*i\b.*0\s*or\s*1", RegexOptions.IgnoreCase)) ||
                lines.Any(l => Regex.IsMatch(l, @"\bbin(ary)?\b", RegexOptions.IgnoreCase));

            // Parse linear forms like "3x2", "x4", "-1.5x3"
            static Dictionary<int, double> ParseLinear(string expr)
            {
                var map = new Dictionary<int, double>();
                foreach (Match m in Regex.Matches(expr, @"([+\-]?\s*\d*(?:[.,]\d+)?)\s*\*?\s*x\s*(\d+)", RegexOptions.IgnoreCase))
                {
                    string coefRaw = m.Groups[1].Value.Replace(" ", "");
                    if (coefRaw == "" || coefRaw == "+" || coefRaw == "-") coefRaw += "1";
                    coefRaw = coefRaw.Replace(',', '.');
                    if (!double.TryParse(coefRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double c))
                        throw new FormatException($"Bad coefficient: '{coefRaw}'");
                    int idx = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    map[idx] = map.ContainsKey(idx) ? map[idx] + c : c;
                }
                return map;
            }

            var objMap = ParseLinear(objLine);
            if (objMap.Count == 0) return null;
            int n = objMap.Keys.Max();

            // Constraint relation and RHS
            var relMatch = Regex.Match(conLine, @"(<=|>=|=)");
            if (!relMatch.Success) return null;
            string rel = relMatch.Value;
            var parts = Regex.Split(conLine, @"(<=|>=|=)");
            if (parts.Length < 3) return null;
            string lhs = parts[0];
            string rhs = parts[2];

            var conMap = ParseLinear(lhs);

            var rhsStr = rhs.Trim().Replace(',', '.');
            if (!double.TryParse(rhsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double rhsVal))
                throw new FormatException($"Bad RHS: '{rhsStr}'");

            double[] c = Enumerable.Range(1, n).Select(i => objMap.TryGetValue(i, out var v) ? v : 0.0).ToArray();
            double[] a = Enumerable.Range(1, n).Select(i => conMap.TryGetValue(i, out var v) ? v : 0.0).ToArray();

            var sb = new StringBuilder();
            sb.Append(dir).Append(' ').AppendLine(string.Join(' ', c.Select(v => v.ToString("0.####", CultureInfo.InvariantCulture))));
            sb.AppendLine(string.Join(' ', a.Select(v => v.ToString("0.####", CultureInfo.InvariantCulture))) + " " + rel + " " + rhsVal.ToString("0.####", CultureInfo.InvariantCulture));
            sb.AppendLine(string.Join(' ', Enumerable.Repeat(allBin ? "bin" : "+", n)));

            return sb.ToString();
        }
    }
}
