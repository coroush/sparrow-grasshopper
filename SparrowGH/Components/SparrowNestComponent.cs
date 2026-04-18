using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Grasshopper.Kernel;
using Newtonsoft.Json;
using Rhino.Geometry;

namespace SparrowGH.Components
{
    /// <summary>
    /// Grasshopper component that calls the Sparrow nesting engine via JSON I/O.
    /// Runs on a background thread so Grasshopper stays responsive.
    /// Progress is shown on the component while running.
    ///
    /// Inputs:
    ///   Curves      — closed planar curves to nest
    ///   StripHeight — fixed height of the material strip (model units)
    ///   Angles      — allowed rotation angles in degrees; empty = free rotation
    ///   TimeSecs    — optimisation budget in seconds (default 30)
    ///   Run         — Button or Toggle; triggers on rising edge (false → true)
    ///
    /// Outputs:
    ///   NestedCurves — curves repositioned in nested layout
    ///   Transforms   — one Transform per curve
    ///   StripWidth   — optimised strip length
    ///   Density      — packing density [0–1]
    /// </summary>
    public class SparrowNestComponent : GH_Component
    {
        private const string FallbackBinaryName = "sparrow";

        // ── State machine ─────────────────────────────────────────────────────────
        private enum State { Idle, Running, Done, Failed }
        private volatile State _state = State.Idle;

        // Updated by the background thread; read on the UI thread
        private volatile string _progress = "";
        private volatile string _errorMsg  = "";

        // Full status log — appended by bg thread, read by UI thread
        private readonly List<string> _log = new List<string>();
        private readonly object _logLock = new object();
        private DateTime _startTime;
        private int _timeBudget = 10;

        // Cached result (written once by bg thread, read by UI thread)
        private readonly object _lock = new object();
        private List<Curve>?    _cachedCurves;
        private List<Transform>? _cachedTransforms;
        private List<int>?      _cachedItemIds;
        private double          _cachedWidth;
        private double          _cachedDensity;
        private bool            _hasCached;
        private int             _resultsVersion   = 0;
        private int             _deliveredVersion = -1;

        private bool _prevRun;
        private bool _initialized;
        private Task? _bgTask;
        private System.Threading.Timer? _ticker;
        private readonly object _procsLock = new object();
        private List<Process> _procs = new List<Process>();
        private int _cachedBestSeed;

        public SparrowNestComponent()
            : base(
                "Sparrow Strip Nest",
                "SpStrip",
                "2D irregular strip packing using the Sparrow engine.",
                "Sparrow",
                "Nesting")
        { }

        public override Guid ComponentGuid => new Guid("b2c3d4e5-f6a7-8901-bcde-f12345678901");

        protected override void ExpireDownStreamObjects()
        {
            if (_resultsVersion != _deliveredVersion)
                base.ExpireDownStreamObjects();
        }

        // TODO: icons
        protected override System.Drawing.Bitmap Icon => LoadIcon("SparrowGH.Resources.strip.png");
        
        private static System.Drawing.Bitmap LoadIcon(string name)
        {
            var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null) return null!;
            var original = new System.Drawing.Bitmap(stream);
            var scaled = new System.Drawing.Bitmap(24, 24, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(scaled);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(original, 0, 0, 24, 24);
            return scaled;
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("strip_h", "H",
                "Fixed height of the material strip.",
                GH_ParamAccess.item, 1000.0);
            pManager.AddCurveParameter("crvs", "C",
                "Closed planar curves to nest. Projected to XY plane automatically.",
                GH_ParamAccess.list);
            pManager.AddNumberParameter("rotations", "A",
                "Allowed rotation angles in degrees. Leave empty for continuous rotation.",
                GH_ParamAccess.list);
            pManager.AddNumberParameter("spacing", "Sp",
                "Minimum gap between pieces in model units. 0 = no gap.",
                GH_ParamAccess.item, 0.0);
            pManager.AddIntegerParameter("time_limit", "T",
                "Optimisation time budget in seconds.",
                GH_ParamAccess.item, 10);
            pManager.AddIntegerParameter("seeds", "S",
                "RNG seed(s). 0 = random. Feed a flat list of multiple seeds to run them in parallel; the best result is returned and the winning seed is shown. Empty = single random seed.",
                GH_ParamAccess.list);
            pManager.AddBooleanParameter("run", "R",
                "Connect a Button or Toggle. Nesting fires on the rising edge.",
                GH_ParamAccess.item, false);

            pManager[2].Optional = true;
            pManager[5].Optional = true;  // seeds
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("nested_crvs", "NC",
                "Input curves repositioned in their nested layout.", GH_ParamAccess.list);
            pManager.AddIntegerParameter("ids", "ID",
                "Original index of each curve in the input list.", GH_ParamAccess.list);
            pManager.AddTransformParameter("transforms", "X",
                "Transformation per curve (rotation + translation in XY).", GH_ParamAccess.list);
            pManager.AddNumberParameter("density", "D",
                "Packing density [0–1].", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── Collect inputs ────────────────────────────────────────────────────
            var curves    = new List<Curve>();
            double stripH = 1000.0;
            var angles    = new List<double>();
            int timeSecs  = 10;
            double spacing = 0.0;
            var seeds     = new List<int>();
            bool run      = false;

            DA.GetData(0, ref stripH);
            if (!DA.GetDataList(1, curves)) return;
            DA.GetDataList(2, angles);
            DA.GetData(3, ref spacing);
            DA.GetData(4, ref timeSecs);
            DA.GetDataList(5, seeds);
            DA.GetData(6, ref run);
            if (seeds.Count == 0) seeds.Add(0);

            bool risingEdge = _initialized && run && !_prevRun;
            _prevRun = run;
            _initialized = true;

            // ── Launch on rising edge (only when not already running) ─────────────
            if (risingEdge && _state != State.Running)
            {
                _state     = State.Running;
                _progress  = "Starting";
                _errorMsg  = "";
                _startTime = DateTime.Now;
                _timeBudget = timeSecs;
                lock (_logLock) { _log.Clear(); }

                var curvesCopy   = curves.Select(c => c.DuplicateCurve()).ToList();
                var anglesCopy   = angles.ToList();
                var seedsCopy    = seeds.ToList();
                double stripCopy = stripH;
                int timeCopy     = timeSecs;
                double spacingCopy = spacing;

                _bgTask = Task.Run(() => BackgroundNest(curvesCopy, stripCopy, anglesCopy, timeCopy, spacingCopy, seedsCopy));

                _ticker?.Dispose();
                _ticker = new System.Threading.Timer(_ =>
                {
                    if (_state == State.Running)
                        Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
                        {
                            // Update message display only — no re-solve, no downstream trigger
                            Message = $"{_progress}\n[{(int)(DateTime.Now - _startTime).TotalSeconds}/{_timeBudget}s]";

                            Grasshopper.Instances.ActiveCanvas?.Refresh();
                        }));
                    else
                        { _ticker?.Dispose(); _ticker = null; }
                }, null, 1000, 1000);
            }

            // ── Component message (shown below component name) ────────────────────
            switch (_state)
            {
                case State.Running:
                    Message = $"{_progress}\n[{ElapsedSecs()}/{_timeBudget}s]";
                    break;
                case State.Done:
                    Message = FormatResultMessage();
                    if (_cachedBestSeed > 0)
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"Best seed: {_cachedBestSeed}");
                    break;
                case State.Failed:
                    Message = "error";
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, _errorMsg);
                    break;
                case State.Idle:
                    Message = _hasCached ? FormatResultMessage() : "";
                    if (_hasCached && _cachedBestSeed > 0)
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"Best seed: {_cachedBestSeed}");
                    break;
            }

            // ── Geometry outputs ──────────────────────────────────────────────────
            OutputCached(DA);

        }

        private string Ts() =>
            (DateTime.Now - _startTime).ToString(@"mm\:ss");

        private int ElapsedSecs() =>
            (int)(DateTime.Now - _startTime).TotalSeconds;

        private string AsciiBar()
        {
            int total   = Math.Max(_timeBudget, 1);
            int elapsed = (int)(DateTime.Now - _startTime).TotalSeconds;
            double frac = Math.Min((double)elapsed / total, 1.0);
            int filled  = (int)Math.Round(frac * 20);
            string bar  = new string('█', filled) + new string('░', 20 - filled);
            return $"[{bar}] {elapsed}/{total}s";
        }

        private string BuildStatusText(string headline)
        {
            var lines = new List<string>();
            if (_state == State.Running)
                lines.Add(AsciiBar());
            lines.Add(headline);
            return string.Join("\n", lines);
        }

        private void OutputCached(IGH_DataAccess DA)
        {
            lock (_lock)
            {
                if (!_hasCached) return;
                DA.SetDataList(0, _cachedCurves);
                DA.SetDataList(1, _cachedItemIds);
                DA.SetDataList(2, _cachedTransforms);
                DA.SetData(3, _cachedDensity);
                _deliveredVersion = _resultsVersion;
            }
        }

        // ── Background nesting thread ─────────────────────────────────────────────

        private void BackgroundNest(
            List<Curve> curves, double stripHeight,
            List<double> angles, int timeSecs, double spacing, List<int> seeds)
        {
            string tempDir   = Path.GetTempPath();
            string ticks     = DateTime.Now.Ticks.ToString();
            string jobName   = "gh_nest_" + ticks;
            string inputPath = Path.Combine(tempDir, jobName + ".json");

            try
            {
                var input = BuildSparrowInput(curves, stripHeight, angles);
                if (input == null)
                {
                    Fail("Failed to build nesting input (check curve list).");
                    return;
                }

                File.WriteAllText(inputPath, JsonConvert.SerializeObject(input, Formatting.Indented));

                string? bin = FindSparrowBinary();
                if (bin == null)
                {
                    Fail("Cannot find 'sparrow' binary. Place it next to SparrowGH.gha or add it to PATH.");
                    return;
                }

                string spacingArg = spacing > 0
                    ? $" -p {spacing.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                    : "";

                // ── Launch one process per seed ───────────────────────────────────
                var runs = new List<(int seed, Process proc, string outputDir,
                                      Queue<string> stdout, Queue<string> stderr,
                                      System.Collections.Concurrent.ConcurrentDictionary<int,string> earlyError)>();

                int totalRuns = seeds.Count;
                int doneCount = 0;

                for (int i = 0; i < seeds.Count; i++)
                {
                    int seed = seeds[i];
                    string jobSuffix = seed > 0 ? $"s{seed}" : $"r{i}";
                    string outputDir = Path.Combine(tempDir, $"{jobName}_{jobSuffix}_output");
                    Directory.CreateDirectory(outputDir);

                    string seedArg = seed > 0 ? $" -s {seed}" : "";
                    var psi = new ProcessStartInfo(bin)
                    {
                        Arguments              = $"-i \"{inputPath}\" -t {timeSecs}{spacingArg}{seedArg} --no-svg",
                        WorkingDirectory       = outputDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                    };

                    var proc      = Process.Start(psi)!;
                    var stdoutBuf = new Queue<string>();
                    var stderrBuf = new Queue<string>();
                    var earlyErr  = new System.Collections.Concurrent.ConcurrentDictionary<int,string>();
                    int capturedSeed = seed;

                    proc.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data == null) return;
                        lock (stdoutBuf) { stdoutBuf.Enqueue(e.Data); if (stdoutBuf.Count > 8) stdoutBuf.Dequeue(); }

                        if (e.Data.Contains("[MAIN] ERROR:"))
                        {
                            int idx = e.Data.IndexOf("[MAIN] ERROR:");
                            earlyErr[0] = e.Data.Substring(idx + 7).Trim();
                            return;
                        }

                        var (shortStatus, logLine) = ParseProgress(e.Data);
                        if (shortStatus != null)
                        {
                            _progress = totalRuns > 1
                                ? $"seed {(capturedSeed == 0 ? "rand" : capturedSeed.ToString())}: {shortStatus}  [{doneCount}/{totalRuns}]"
                                : shortStatus;
                            lock (_logLock) { _log.Add(logLine!); }
                        }
                    };
                    proc.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data == null) return;
                        lock (stderrBuf) { stderrBuf.Enqueue(e.Data); if (stderrBuf.Count > 5) stderrBuf.Dequeue(); }
                    };
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    runs.Add((seed, proc, outputDir, stdoutBuf, stderrBuf, earlyErr));
                }

                lock (_procsLock) { _procs.Clear(); _procs.AddRange(runs.Select(r => r.proc)); }

                // ── Wait on all processes within the shared wall-clock budget ────
                const int overheadMs = 120_000;
                var deadline = DateTime.Now.AddMilliseconds(timeSecs * 1000 + overheadMs);

                foreach (var r in runs)
                {
                    int remainingMs = (int)Math.Max(0, (deadline - DateTime.Now).TotalMilliseconds);
                    if (!r.proc.WaitForExit(remainingMs))
                    {
                        try { r.proc.Kill(); } catch { }
                    }
                    System.Threading.Interlocked.Increment(ref doneCount);
                }

                // ── Score each run and pick the winner ──────────────────────────
                var results  = new List<(int seed, double width, double density, string json)>();
                var failures = new List<(int seed, string reason)>();

                foreach (var r in runs)
                {
                    if (r.earlyError.TryGetValue(0, out string? err))
                    {
                        failures.Add((r.seed, err));
                        continue;
                    }
                    string outPath = Path.Combine(r.outputDir, "output", "final_gh_nest.json");
                    if (!File.Exists(outPath))
                    {
                        string[] stderrArr = r.stderr.ToArray();
                        string[] stdoutArr = r.stdout.ToArray();
                        var tail = stderrArr.Length > 0 ? stderrArr : stdoutArr;
                        string tailMsg = tail.Length > 0
                            ? " Last output: " + string.Join(" | ", tail.Select(l => l.Length > 120 ? l.Substring(l.Length - 120) : l))
                            : "";
                        failures.Add((r.seed, $"No output (exit {r.proc.ExitCode}).{tailMsg}"));
                        continue;
                    }
                    try
                    {
                        string json     = File.ReadAllText(outPath);
                        dynamic result  = JsonConvert.DeserializeObject(json)!;
                        var solution    = result.solution;
                        double width    = (double)solution.strip_width;
                        double density  = (double)solution.density;
                        results.Add((r.seed, width, density, json));
                    }
                    catch (Exception ex)
                    {
                        failures.Add((r.seed, $"parse error: {ex.Message}"));
                    }
                }

                if (results.Count == 0)
                {
                    string combined = failures.Count > 0
                        ? string.Join(" | ", failures.Select(f => $"seed {(f.seed == 0 ? "rand" : f.seed.ToString())}: {f.reason}"))
                        : "all runs failed";
                    Fail(combined);
                    return;
                }

                // Winner: smallest strip width (tie-break by higher density, rarely differs since width drives density)
                var best = results
                    .OrderBy(x => x.width)
                    .ThenByDescending(x => x.density)
                    .First();

                ParseAndCache(best.json, curves);
                _cachedBestSeed = best.seed;
                _state = State.Done;
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
            }
            finally
            {
                lock (_procsLock) _procs.Clear();
                try { File.Delete(inputPath); } catch { }
                Rhino.RhinoApp.InvokeOnUiThread(
                    new Action(() => ExpireSolution(true)));
            }
        }

        private void StopCurrentRun()
        {
            lock (_procsLock)
            {
                foreach (var p in _procs) { try { p.Kill(); } catch { } }
                _procs.Clear();
            }
            _ticker?.Dispose();
            _ticker = null;
            if (_state == State.Running)
            {
                _state = State.Idle;
                Message = "";
            }
        }

        private string FormatResultMessage()
        {
            return $"density {_cachedDensity:P1}";
        }

        public override void AddedToDocument(GH_Document document)
        {
            base.AddedToDocument(document);
            document.SolutionStart += OnSolutionStart;
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            document.SolutionStart -= OnSolutionStart;
            StopCurrentRun();
            base.RemovedFromDocument(document);
        }

        private void OnSolutionStart(object sender, GH_SolutionEventArgs e)
        {
            if (this.Locked && _state == State.Running)
                StopCurrentRun();
        }

        private void Fail(string msg)
        {
            _errorMsg = msg;
            _state    = State.Failed;
        }

        // Returns (shortStatus, logLine) for lines that carry progress info, else (null, null).
        // Sample Sparrow stderr lines:
        //   [INFO] [00:00:04] <main> [EXPL] finished, best feasible solution: width: 11.021 (88.909%)
        //   [INFO] [00:00:04] <main> [CMPR] success at 0.050% (11.016 | 88.953%)
        private static readonly Regex _densityRx = new Regex(@"\((\d+\.\d+)%\)", RegexOptions.Compiled);
        private static readonly Regex _widthRx   = new Regex(@"width[:\s]+([\d.]+)", RegexOptions.Compiled);
        private static readonly Regex _timeRx    = new Regex(@"\[(\d{2}:\d{2}:\d{2})\]", RegexOptions.Compiled);

        private static (string? shortStatus, string? logLine) ParseProgress(string line)
        {
            if (!line.Contains("[EXPL]") && !line.Contains("[CMPR]")) return (null, null);

            string phase   = line.Contains("[EXPL]") ? "Exploring" : "Compressing";
            var    dm      = _densityRx.Match(line);
            var    wm      = _widthRx.Match(line);
            var    tm      = _timeRx.Match(line);

            string ts      = tm.Success ? tm.Groups[1].Value : "??:??:??";

            string shortStatus = phase;
            string logLine     = $"[{ts}] {shortStatus}";

            return (shortStatus, logLine);
        }

        private void ParseAndCache(string json, List<Curve> curves)
        {
            dynamic result   = JsonConvert.DeserializeObject(json)!;
            var     solution = result.solution;
            double  width    = (double)solution.strip_width;
            double  density  = (double)solution.density;
            int     elapsed  = (int)(DateTime.Now - _startTime).TotalSeconds;

            var nestedCurves = new List<Curve>();
            var transforms   = new List<Transform>();
            var itemIds      = new List<int>();

            foreach (var placed in solution.layout.placed_items)
            {
                int    itemId = (int)(long)placed.item_id;
                double rotDeg = (double)placed.transformation.rotation;
                double tx     = (double)placed.transformation.translation[0];
                double ty     = (double)placed.transformation.translation[1];

                Transform rot      = Transform.Rotation(rotDeg * Math.PI / 180.0, Vector3d.ZAxis, Point3d.Origin);
                Transform trans    = Transform.Translation(new Vector3d(tx, ty, 0));
                Transform combined = trans * rot;

                Curve nested = curves[itemId].DuplicateCurve();
                nested.Transform(combined);

                nestedCurves.Add(nested);
                transforms.Add(combined);
                itemIds.Add(itemId);
            }

            _progress = $"density {density:P1}  width {width:F3}";

            lock (_logLock)
            {
                _log.Add($"[{Ts()}] Finished in {elapsed}s — density {density:P1}  width {width:F3}  items placed {nestedCurves.Count}");
            }

            lock (_lock)
            {
                _cachedCurves     = nestedCurves;
                _cachedTransforms = transforms;
                _cachedItemIds    = itemIds;
                _cachedWidth      = width;
                _cachedDensity    = density;
                _hasCached        = true;
                _resultsVersion++;
            }
        }

        // ── Input building ────────────────────────────────────────────────────────

        private SparrowInput? BuildSparrowInput(
            List<Curve> curves, double stripHeight, List<double> angles)
        {
            var items = new List<SparrowItem>();

            for (int i = 0; i < curves.Count; i++)
            {
                Curve projected = Curve.ProjectToPlane(curves[i], Plane.WorldXY);
                if (projected == null) continue;

                if (!projected.TryGetPolyline(out Polyline poly))
                {
                    projected = projected.ToPolyline(0, 0, 0.1, 0, 0, 1.0, 0, 0, true);
                    if (!projected.TryGetPolyline(out poly)) continue;
                }

                if (!poly.IsClosed) poly.Add(poly[0]);

                items.Add(new SparrowItem
                {
                    id     = i,
                    demand = 1,
                    allowed_orientations = angles.Count > 0
                        ? angles.Select(a => (float)a).ToArray()
                        : null,
                    shape = new SparrowShape
                    {
                        type = "simple_polygon",
                        data = poly.Select(pt => new double[] { pt.X, pt.Y }).ToList()
                    }
                });
            }

            if (items.Count == 0) return null;

            return new SparrowInput
            {
                name         = "gh_nest",
                strip_height = (float)stripHeight,
                items        = items
            };
        }

        // ── Binary discovery ──────────────────────────────────────────────────────

        private static readonly bool IsWindows =
            Environment.OSVersion.Platform == PlatformID.Win32NT;

        private static string BinName =>
            IsWindows ? "sparrow.exe" : "sparrow";

        private string? FindSparrowBinary()
        {
            // 1. Next to the .gha (the normal installed location)
            string? ghaDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (ghaDir != null)
            {
                string c = Path.Combine(ghaDir, BinName);
                if (File.Exists(c)) return c;
            }

            // 2. Desktop workspace (dev convenience)
            string devBin = IsWindows ? "sparrow.exe" : "sparrow";
            string dev = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "sparrow-grasshopper", "sparrow", "target", "release", devBin);
            if (File.Exists(dev)) return dev;

            // 3. Search PATH — use 'where' on Windows, 'which' on Mac/Linux
            try
            {
                string pathCmd = IsWindows ? "where" : "which";
                string pathArg = IsWindows ? "sparrow.exe" : "sparrow";
                using var proc = Process.Start(new ProcessStartInfo(pathCmd, pathArg)
                    { RedirectStandardOutput = true, UseShellExecute = false });
                string? found = proc?.StandardOutput.ReadLine()?.Trim();
                proc?.WaitForExit();
                if (!string.IsNullOrEmpty(found) && File.Exists(found)) return found;
            }
            catch { }

            return null;
        }

        // ── JSON model classes ────────────────────────────────────────────────────

        private class SparrowInput
        {
            public string name { get; set; } = "gh_nest";
            public float strip_height { get; set; }
            public List<SparrowItem> items { get; set; } = new();
        }

        private class SparrowItem
        {
            public int id { get; set; }
            public int demand { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public float[]? allowed_orientations { get; set; }
            public SparrowShape shape { get; set; } = new();
        }

        private class SparrowShape
        {
            public string type { get; set; } = "simple_polygon";
            public List<double[]> data { get; set; } = new();
        }
    }
}
