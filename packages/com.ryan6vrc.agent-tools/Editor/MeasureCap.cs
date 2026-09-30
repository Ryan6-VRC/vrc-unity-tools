using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>The wall-clock limits on play-mode measurement: a per-run cap and a budget summed over every run in a
    /// rolling window. A pump that measures across frames arms one at its start, checks <see cref="Expired"/> every tick,
    /// tears down through its own restore path when it reads true, and calls <see cref="Close"/> in that teardown.
    /// <c>DrivePhysBones</c> does; a hand-written <c>execute_code</c> pump does the same, so a drive it arms from inside
    /// its own run is charged to the one budget. The limits are the operator's on how much measuring an agent queues, not
    /// timeouts to size to the work: measuring from a pump that never arms one, or arming one above
    /// <see cref="DefaultSeconds"/> to fit a longer program, runs the measurement they exist to stop. Make the program
    /// cheaper instead, or report what the runs that fit show. Contract: docs/unity-tools.md.</summary>
    public sealed class MeasureCap
    {
        public const float DefaultSeconds = 180f;   // one run's wall clock
        public const float BudgetSeconds = 900f;    // all runs' wall clock, summed over the window; sized to one rig-skirt row
        public const float WindowSeconds = 1800f;
        const string LedgerKey = "Ryan6Vrc.MeasureCap.ledger";

        /// <summary>One run in the ledger. An open run is charged to its cap's end at most, so a pump that died without
        /// closing stops costing once its cap would have stopped it.</summary>
        internal struct Entry { public long id; public string stage; public DateTime start; public DateTime? end; public float cap; }

        readonly long id;
        readonly DateTime armedAt;
        public readonly string Stage;
        public readonly float Seconds;
        bool closed;

        MeasureCap(long id, string stage, float seconds) { this.id = id; Stage = stage; Seconds = seconds; armedAt = DateTime.UtcNow; }

        public double Elapsed => (DateTime.UtcNow - armedAt).TotalSeconds;
        public bool Expired => Passed(Elapsed, Seconds);

        /// <summary>The line a pump writes as its FAIL detail when <see cref="Expired"/> reads true.</summary>
        public string Reason => "time cap: " + Math.Round(Elapsed).ToString(CultureInfo.InvariantCulture) + " s of wall clock since arming, over the "
            + S(Seconds) + " s cap, the operator's limit on one run's measuring; measure less rather than arm a higher cap";

        /// <summary>Opens a run in the ledger, or returns null with <paramref name="refusal"/> set when the window's budget is
        /// spent or <paramref name="seconds"/> is not a positive number.</summary>
        public static MeasureCap Arm(string stage, out string refusal, float seconds = DefaultSeconds)
        {
            refusal = SecondsError(seconds) ?? BudgetRefusal(); if (refusal != null) return null;
            var ledger = Load(); long id = ledger.Count == 0 ? 1 : ledger.Max(e => e.id) + 1;
            var cap = new MeasureCap(id, string.IsNullOrEmpty(stage) ? "unnamed" : stage, seconds);
            ledger.Add(new Entry { id = id, stage = cap.Stage, start = cap.armedAt, cap = seconds }); Save(ledger);
            return cap;
        }

        /// <summary>Ends the run's charge to the budget. Idempotent.</summary>
        public void Close()
        {
            if (closed) return; closed = true;
            var ledger = Load(); int i = ledger.FindIndex(e => e.id == id);
            if (i >= 0) { var e = ledger[i]; e.end = DateTime.UtcNow; ledger[i] = e; Save(ledger); }
        }

        /// <summary>The refusal a measuring door returns before it arms, or null while the window's budget has room.</summary>
        public static string BudgetRefusal() => BudgetRefusal(Load(), DateTime.UtcNow, BudgetSeconds, WindowSeconds);

        internal static string SecondsError(float s) => s > 0 && !float.IsInfinity(s) ? null : "the time cap is wall-clock seconds, above 0";

        internal static bool Passed(double elapsed, float cap) => elapsed > cap;

        /// <summary>Seconds of wall clock the ledger's runs cover inside the window ending at <paramref name="now"/>, overlaps
        /// counted once, so a drive a harness arms inside its own run costs nothing extra; and each stage's share. Pure.</summary>
        internal static double Spent(IList<Entry> ledger, DateTime now, float window, out List<(string stage, double seconds)> byStage)
        {
            var from = now.AddSeconds(-window);
            byStage = ledger.Select(e => (e.stage, Overlap(e, from, now))).Where(x => x.Item2 > 0.5).GroupBy(x => x.stage)
                .Select(g => (g.Key, g.Sum(x => x.Item2))).OrderByDescending(x => x.Item2).ToList();
            var spans = ledger.Select(e => (s: e.start < from ? from : e.start, e: Min(End(e), now))).Where(x => x.e > x.s).OrderBy(x => x.s).ToList();
            double total = 0; DateTime? cs = null, ce = null;
            foreach (var (s, e) in spans)
            {
                if (ce == null || s > ce) { if (cs != null) total += (ce.Value - cs.Value).TotalSeconds; cs = s; ce = e; }
                else if (e > ce) ce = e;
            }
            if (cs != null) total += (ce.Value - cs.Value).TotalSeconds;
            return total;
        }

        /// <summary>Null while the window ending at <paramref name="now"/> holds less than <paramref name="budget"/> seconds of
        /// measurement; else what was used, by which stages, and when the next run can start. Pure.</summary>
        internal static string BudgetRefusal(IList<Entry> ledger, DateTime now, float budget, float window)
        {
            double spent = Spent(ledger, now, window, out var byStage);
            if (spent < budget) return null;
            // The earliest second from which the window, sliding on with no new run, holds less than the budget.
            var frees = now; while (frees < now.AddSeconds(window) && Spent(ledger, frees, window, out _) >= budget) frees = frees.AddSeconds(1);
            return "measurement budget spent: runs used " + S((float)spent) + " s of wall clock in the last " + S(window / 60) + " min against a " + S(budget) + " s budget ("
                + string.Join(", ", byStage.Take(8).Select(x => x.stage + " " + S((float)x.seconds) + " s")) + (byStage.Count > 8 ? ", …" : "")
                + "). Report what those runs show before measuring more; the next run can start at " + frees.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ".";
        }

        static DateTime End(Entry e) => Min(e.end ?? DateTime.MaxValue, e.start.AddSeconds(e.cap));
        static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
        static double Overlap(Entry e, DateTime from, DateTime to) { var s = e.start < from ? from : e.start; var x = Min(End(e), to); return x > s ? (x - s).TotalSeconds : 0; }
        static string S(float f) => Math.Round(f).ToString(CultureInfo.InvariantCulture);

        // ── The ledger: SessionState, so it survives the domain reloads of play entry and a script refresh ──────────

        internal static string Format(IEnumerable<Entry> ledger) => string.Join("\n", ledger.Select(e => e.id.ToString(CultureInfo.InvariantCulture) + "\t" + e.stage.Replace('\t', ' ').Replace('\n', ' ')
            + "\t" + e.start.Ticks.ToString(CultureInfo.InvariantCulture) + "\t" + (e.end?.Ticks.ToString(CultureInfo.InvariantCulture) ?? "") + "\t" + e.cap.ToString("R", CultureInfo.InvariantCulture)));

        internal static List<Entry> Parse(string raw)
        {
            var list = new List<Entry>();
            foreach (var l in (raw ?? "").Split('\n'))
            {
                var f = l.Split('\t');
                if (f.Length != 5 || !long.TryParse(f[0], out var id) || !long.TryParse(f[2], out var st) || !float.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var cap)) continue;
                list.Add(new Entry { id = id, stage = f[1], start = new DateTime(st, DateTimeKind.Utc), end = long.TryParse(f[3], out var en) ? new DateTime(en, DateTimeKind.Utc) : (DateTime?)null, cap = cap });
            }
            return list;
        }

        static List<Entry> Load() => Parse(SessionState.GetString(LedgerKey, ""));

        // Entries older than the window never count again, so they are dropped on save.
        static void Save(List<Entry> ledger) { var from = DateTime.UtcNow.AddSeconds(-WindowSeconds); SessionState.SetString(LedgerKey, Format(ledger.Where(e => End(e) > from))); }

        /// <summary>A domain reload drops every pump, so a run still open after one has stopped: close it at the reload.</summary>
        [InitializeOnLoadMethod]
        static void CloseOrphans()
        {
            var ledger = Load(); if (!ledger.Any(e => e.end == null)) return;
            var now = DateTime.UtcNow; Save(ledger.Select(e => { if (e.end == null) e.end = now; return e; }).ToList());
        }
    }
}
