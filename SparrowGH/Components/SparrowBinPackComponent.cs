using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Newtonsoft.Json;
using Rhino.Geometry;

namespace SparrowGH.Components
{
    public class SparrowBinPackComponent : GH_Component
    {
        // ── State machine ─────────────────────────────────────────────────────────
        private enum State { Idle, Running, Done, Failed }
        private volatile State _state = State.Idle;

        private volatile string _progress = "";
        private volatile string _errorMsg  = "";

        private DateTime _startTime;
        private int      _timeBudget = 60;

        // Cached result
        private readonly object _lock = new object();
        private DataTree<Curve>?     _cachedCurveTree;
        private DataTree<Transform>? _cachedXformTree;
        private DataTree<int>?       _cachedIdTree;
        private List<Rectangle3d>?   _cachedSheetOutlines;
        private int                  _cachedSheetsUsed;
        private List<double>?        _cachedDensities;
        private double               _cachedTotalDensity;
        private bool                 _hasCached;

        private bool  _prevRun;
        private bool  _initialized;
        private Task? _bgTask;
        private System.Threading.Timer? _ticker;
        private volatile Process? _proc;

        public SparrowBinPackComponent()
            : base(
                "Sparrow Nest",
                "SpNest",
                "2D irregular sheet nesting using the Sparrow engine.",
                "Sparrow",
                "Nesting")
        { }

        public override Guid ComponentGuid => new Guid("c3d4e5f6-a7b8-9012-cdef-234567890abc");

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("sheet_w", "W",
                "Width of each sheet in model units.", GH_ParamAccess.item, 2500.0);
            pManager.AddNumberParameter("sheet_h", "H",
                "Height of each sheet in model units.", GH_ParamAccess.item, 1250.0);
            pManager.AddCurveParameter("crvs", "C",
                "Closed planar curves to nest.", GH_ParamAccess.list);
            pManager.AddNumberParameter("rotations", "A",
                "Allowed rotation angles in degrees. Leave empty for continuous rotation.",
                GH_ParamAccess.list);
            pManager.AddNumberParameter("spacing", "Sp",
                "Minimum gap between pieces in model units. 0 = no gap.",
                GH_ParamAccess.item, 0.0);
            pManager.AddIntegerParameter("time_limit", "T",
                "Optimisation time budget in seconds.", GH_ParamAccess.item, 60);
            pManager.AddBooleanParameter("run", "R",
                "Connect a Button or Toggle. Fires on rising edge.",
                GH_ParamAccess.item, false);

            pManager[3].Optional = true;  // rotations
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddRectangleParameter("sheets", "SO",
                "One rectangle per used sheet, laid out along X.", GH_ParamAccess.list);
            pManager.AddCurveParameter("nested_crvs", "NC",
                "Nested curves per sheet. Branch {i} = sheet i.", GH_ParamAccess.tree);
            pManager.AddIntegerParameter("ids", "ID",
                "Original index of each input curve. Branch {i} = sheet i.", GH_ParamAccess.tree);
            pManager.AddTransformParameter("transforms", "X",
                "Transform per curve. Branch {i} = sheet i.", GH_ParamAccess.tree);
            pManager.AddNumberParameter("densities", "D",
                "Packing density per sheet [0–1].", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── Inputs ────────────────────────────────────────────────────────────
            var curves     = new List<Curve>();
            double sheetW  = 2500.0;
            double sheetH  = 1250.0;
            var angles     = new List<double>();
            int timeSecs   = 60;
            double spacing = 0.0;
            bool run       = false;

            DA.GetData(0, ref sheetW);
            DA.GetData(1, ref sheetH);
            if (!DA.GetDataList(2, curves)) return;
            DA.GetDataList(3, angles);
            DA.GetData(4, ref spacing);
            DA.GetData(5, ref timeSecs);
            DA.GetData(6, ref run);

            // Seed _prevRun on first solve so a saved-true toggle doesn't fire immediately
            bool risingEdge = _initialized && run && !_prevRun;
            _prevRun = run;
            _initialized = true;

            // ── Launch on rising edge (only when not already running) ─────────────
            if (risingEdge && _state != State.Running)
            {
                _state      = State.Running;
                _progress   = "Starting";
                _errorMsg   = "";
                _startTime  = DateTime.Now;
                _timeBudget = timeSecs;

                var curvesCopy = curves.Select(c => c.DuplicateCurve()).ToList();
                var anglesCopy = angles.ToList();

                _bgTask = Task.Run(() =>
                    BackgroundBinPack(curvesCopy, sheetW, sheetH, anglesCopy, timeSecs, spacing));

                _ticker?.Dispose();
                _ticker = new System.Threading.Timer(_ =>
                {
                    if (_state == State.Running)
                        Rhino.RhinoApp.InvokeOnUiThread(new Action(() => ExpireSolution(true)));
                    else
                        { _ticker?.Dispose(); _ticker = null; }
                }, null, 1000, 1000);
            }

            // ── Component Message (shown below component name on canvas) ─────────
            switch (_state)
            {
                case State.Running:
                    Message = $"{_progress}\n[{(int)(DateTime.Now - _startTime).TotalSeconds}/{_timeBudget}s]";
                    break;
                case State.Done:
                    Message = $"{_cachedSheetsUsed} sheets · {_cachedTotalDensity:P1}";
                    break;
                case State.Failed:
                    Message = "error";
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, _errorMsg);
                    break;
                case State.Idle:
                    Message = _hasCached
                        ? $"{_cachedSheetsUsed} sheets · {_cachedTotalDensity:P1}"
                        : "";
                    break;
            }

            // ── Geometry outputs ──────────────────────────────────────────────────
            lock (_lock)
            {
                if (!_hasCached) return;
                DA.SetDataList(0, _cachedSheetOutlines);
                DA.SetDataTree(1, _cachedCurveTree!);
                DA.SetDataTree(2, _cachedIdTree!);
                DA.SetDataTree(3, _cachedXformTree!);
                DA.SetDataList(4, _cachedDensities);
            }
        }


        // ── Background thread ─────────────────────────────────────────────────────

        private void BackgroundBinPack(
            List<Curve> curves, double sheetWidth, double sheetHeight,
            List<double> angles, int timeSecs, double spacing)
        {
            string tempDir   = Path.GetTempPath();
            string jobName   = "gh_binpack_" + DateTime.Now.Ticks;
            string inputPath = Path.Combine(tempDir, jobName + ".json");
            string outputDir = Path.Combine(tempDir, jobName + "_output");

            try
            {
                var input = BuildBinPackInput(curves, sheetWidth, sheetHeight, angles);
                if (input == null) { Fail("No valid curves found."); return; }

                Directory.CreateDirectory(outputDir);
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

                var psi = new ProcessStartInfo(bin)
                {
                    Arguments              = $"--mode bp -i \"{inputPath}\" -t {timeSecs}{spacingArg}",
                    WorkingDirectory       = outputDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };

                _proc = Process.Start(psi)!;
                var proc = _proc;

                DateTime lastRefresh = DateTime.MinValue;
                string? earlyError = null;
                // Rolling buffer of last stdout lines for diagnostics
                var lastLines = new System.Collections.Generic.Queue<string>();
                var stderrLines = new System.Collections.Generic.Queue<string>();

                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (lastLines) { lastLines.Enqueue(e.Data); if (lastLines.Count > 8) lastLines.Dequeue(); }

                    if (e.Data.Contains("[BP] ERROR:"))
                    {
                        int idx = e.Data.IndexOf("[BP] ERROR:");
                        earlyError = e.Data.Substring(idx + 5).Trim();
                        Rhino.RhinoApp.InvokeOnUiThread(new Action(() => ExpireSolution(true)));
                        return;
                    }

                    string? status = ParseBpProgress(e.Data);
                    if (status != null)
                    {
                        _progress = status;
                        if ((DateTime.Now - lastRefresh).TotalSeconds >= 1)
                        {
                            lastRefresh = DateTime.Now;
                            Rhino.RhinoApp.InvokeOnUiThread(new Action(() => ExpireSolution(true)));
                        }
                    }
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (stderrLines) { stderrLines.Enqueue(e.Data); if (stderrLines.Count > 5) stderrLines.Dequeue(); }
                };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                bool finished = proc.WaitForExit(timeSecs * 1000 + 15_000);
                _proc = null;
                if (!finished) { try { proc.Kill(); } catch { } Fail($"Timed out after {timeSecs + 15}s."); return; }

                if (earlyError != null) { Fail(earlyError); return; }

                string outPath = Path.Combine(outputDir, "output", "final_gh_binpack.json");
                if (!File.Exists(outPath))
                {
                    // Collect diagnostics: exit code + last log lines
                    var diag = new System.Text.StringBuilder();
                    diag.Append($"No output file (exit {proc.ExitCode}).");
                    string[] stderr = stderrLines.ToArray();
                    string[] stdout = lastLines.ToArray();
                    var tail = stderr.Length > 0 ? stderr : stdout;
                    if (tail.Length > 0)
                        diag.Append(" Last output: " + string.Join(" | ", tail
                            .Select(l => l.Length > 120 ? l.Substring(l.Length - 120) : l)));
                    Fail(diag.ToString());
                    return;
                }

                ParseBpAndCache(File.ReadAllText(outPath), curves, sheetWidth, sheetHeight);
                _state = State.Done;
            }
            catch (Exception ex) { Fail(ex.Message); }
            finally
            {
                try { File.Delete(inputPath); } catch { }
                Rhino.RhinoApp.InvokeOnUiThread(new Action(() => ExpireSolution(true)));
            }
        }

        private void StopCurrentRun()
        {
            try { _proc?.Kill(); } catch { }
            _proc = null;
            _ticker?.Dispose();
            _ticker = null;
            if (_state == State.Running) { _state = State.Idle; Message = ""; }
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

        private void Fail(string msg) { _errorMsg = msg; _state = State.Failed; }

        // ── Progress parsing ──────────────────────────────────────────────────────

        private static readonly Regex _bpBinsRx    = new Regex(@"(\d+) bins?",      RegexOptions.Compiled);
        private static readonly Regex _bpDensityRx = new Regex(@"density ([\d.]+)", RegexOptions.Compiled);

        // Returns a short status string for lines that carry meaningful progress, else null.
        private static string? ParseBpProgress(string line)
        {
            if (line.Contains("[BP-LBF]") && line.Contains("initial assignment done"))
            {
                var bm = _bpBinsRx.Match(line);
                return bm.Success ? $"Initial: {bm.Groups[1].Value} bins" : "Initial assignment done";
            }
            if (line.Contains("[BP-EXPL]") && line.Contains("attempting"))
                return "Reducing bins";
            if (line.Contains("[BP-EXPL]") && line.Contains("reduced to"))
            {
                var bm = _bpBinsRx.Match(line);
                var dm = _bpDensityRx.Match(line);
                string bins = bm.Success ? bm.Groups[1].Value : "?";
                string dens = dm.Success ? $"  {double.Parse(dm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture):P1}" : "";
                return $"Reduced → {bins} bins{dens}";
            }
            if (line.Contains("[BP-EXPL]") && line.Contains("inter-bin moves"))
                return "inter-bin moves";
            if (line.Contains("[BP]") && line.Contains("final solution"))
            {
                var bm = _bpBinsRx.Match(line);
                var dm = _bpDensityRx.Match(line);
                string bins = bm.Success ? bm.Groups[1].Value : "?";
                string dens = dm.Success ? $"  {double.Parse(dm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture):P1}" : "";
                return $"Done: {bins} sheets{dens}";
            }
            return null;
        }

        // ── Output parsing ────────────────────────────────────────────────────────

        private void ParseBpAndCache(string json, List<Curve> curves, double sheetWidth, double sheetHeight)
        {
            dynamic result   = JsonConvert.DeserializeObject(json)!;
            dynamic solution = result.solution;
            double totalDens = (double)solution.density;

            var curveTree    = new DataTree<Curve>();
            var xformTree    = new DataTree<Transform>();
            var idTree       = new DataTree<int>();
            var outlines     = new List<Rectangle3d>();
            var densities    = new List<double>();
            double gap       = sheetWidth * 0.05;
            int idx          = 0;

            foreach (var layout in solution.layouts)
            {
                var path = new GH_Path(idx);
                densities.Add((double)layout.density);

                double offsetX  = idx * (sheetWidth + gap);
                var plane       = new Plane(new Point3d(offsetX, 0, 0), Vector3d.ZAxis);
                outlines.Add(new Rectangle3d(plane, sheetWidth, sheetHeight));

                foreach (var placed in layout.placed_items)
                {
                    int    itemId = (int)(long)placed.item_id;
                    double rotDeg = (double)placed.transformation.rotation;
                    double tx     = (double)placed.transformation.translation[0] + offsetX;
                    double ty     = (double)placed.transformation.translation[1];

                    Transform xform = Transform.Translation(new Vector3d(tx, ty, 0))
                                    * Transform.Rotation(rotDeg * Math.PI / 180.0, Vector3d.ZAxis, Point3d.Origin);
                    Curve nested = curves[itemId].DuplicateCurve();
                    nested.Transform(xform);
                    curveTree.Add(nested, path);
                    xformTree.Add(xform, path);
                    idTree.Add(itemId, path);
                }
                idx++;
            }

            lock (_lock)
            {
                _cachedCurveTree     = curveTree;
                _cachedXformTree     = xformTree;
                _cachedIdTree        = idTree;
                _cachedSheetOutlines = outlines;
                _cachedSheetsUsed    = idx;
                _cachedDensities     = densities;
                _cachedTotalDensity  = totalDens;
                _hasCached           = true;
            }
        }

        // ── Input building ────────────────────────────────────────────────────────

        private BpInput? BuildBinPackInput(
            List<Curve> curves, double sheetWidth, double sheetHeight, List<double> angles)
        {
            var items = new List<BpItem>();
            for (int i = 0; i < curves.Count; i++)
            {
                Curve proj = Curve.ProjectToPlane(curves[i], Plane.WorldXY);
                if (proj == null) continue;
                if (!proj.TryGetPolyline(out Polyline poly))
                {
                    proj = proj.ToPolyline(0, 0, 0.1, 0, 0, 1.0, 0, 0, true);
                    if (!proj.TryGetPolyline(out poly)) continue;
                }
                if (!poly.IsClosed) poly.Add(poly[0]);
                items.Add(new BpItem
                {
                    id     = i,
                    demand = 1,
                    allowed_orientations = angles.Count > 0
                        ? angles.Select(a => (float)a).ToArray() : null,
                    shape = new BpShape
                    {
                        type = "simple_polygon",
                        data = poly.Select(pt => new double[] { pt.X, pt.Y }).ToList()
                    }
                });
            }
            if (items.Count == 0) return null;
            return new BpInput
            {
                name  = "gh_binpack",
                bins  = new List<BpBin> { new BpBin {
                    id    = 0,
                    stock = 999,          // effectively unlimited
                    cost  = 1,
                    shape = new BpShapeRect { type = "rectangle", data = new BpRectData {
                        x_min = 0f, y_min = 0f,
                        width  = (float)sheetWidth,
                        height = (float)sheetHeight
                    }}
                }},
                items = items
            };
        }

        // ── Binary discovery ──────────────────────────────────────────────────────

        private static readonly bool IsWindows =
            Environment.OSVersion.Platform == PlatformID.Win32NT;
        private static string BinName => IsWindows ? "sparrow.exe" : "sparrow";

        private string? FindSparrowBinary()
        {
            string? ghaDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (ghaDir != null) { string c = Path.Combine(ghaDir, BinName); if (File.Exists(c)) return c; }

            string dev = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "sparrow-grasshopper", "sparrow", "target", "release", BinName);
            if (File.Exists(dev)) return dev;

            try {
                using var p = Process.Start(new ProcessStartInfo(IsWindows ? "where" : "which", BinName)
                    { RedirectStandardOutput = true, UseShellExecute = false });
                string? found = p?.StandardOutput.ReadLine()?.Trim();
                p?.WaitForExit();
                if (!string.IsNullOrEmpty(found) && File.Exists(found)) return found;
            } catch { }
            return null;
        }

        // ── JSON models ───────────────────────────────────────────────────────────

        private class BpInput  { public string name{get;set;}="gh_binpack"; public List<BpBin> bins{get;set;}=new(); public List<BpItem> items{get;set;}=new(); }
        private class BpBin    { public int id{get;set;} public int stock{get;set;} public int cost{get;set;} public BpShapeRect shape{get;set;}=new(); }
        private class BpShapeRect { public string type{get;set;}="rectangle"; public BpRectData data{get;set;}=new(); }
        private class BpRectData  { public float x_min{get;set;} public float y_min{get;set;} public float width{get;set;} public float height{get;set;} }
        private class BpItem   { public int id{get;set;} public int demand{get;set;} [JsonProperty(NullValueHandling=NullValueHandling.Ignore)] public float[]? allowed_orientations{get;set;} public BpShape shape{get;set;}=new(); }
        private class BpShape  { public string type{get;set;}="simple_polygon"; public List<double[]> data{get;set;}=new(); }
    }
}
