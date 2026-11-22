using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using OpenClevo.Localization;
using static OpenClevo.App.DiagCatalog;

namespace OpenClevo.App;

/// <summary>What the wizard hands to the report.</summary>
sealed record WizardResult(List<string> Selected, string? Note, List<StepRecord> Steps);

/// <summary>
/// The guided compatibility check: the user ticks the problems they see, the app runs a few safe tests per problem (writes are
/// always undone), asks what the user sees, checks its own answer where it can, and puts everything into the report.
/// </summary>
sealed class DiagWizard : Window
{
    readonly StepContext _ctx;
    readonly string _version;
    readonly List<DiagScenario> _all = DiagCatalog.All();
    readonly Dictionary<string, CheckBox> _boxes = new();
    readonly List<StepRecord> _records = [];

    readonly TextBlock _title = new() { FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _sub = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 8, 0, 0) };
    readonly StackPanel _body = new() { Margin = new Thickness(0, 14, 0, 0) };
    readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 22, 0, 0) };
    readonly TextBox _note = new() { MinHeight = 56, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Margin = new Thickness(0, 12, 0, 0) };

    List<DiagScenario> _selected = [];
    int _si, _ti;
    List<DiagStep> _steps = [];
    bool _closing, _stop;
    Func<Task>? _pendingUndo;      // the undo of a step that already changed something
    Func<Task>? _pendingTeardown;  // the teardown of the scenario in progress
    string? _noteText;

    public DiagWizard(HardwareService hw, KeyboardEffectEngine kb, string version)
    {
        _ctx = new StepContext { Hw = hw, Kb = kb };
        _version = version;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height; Width = 600; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true; FlowDirection = Loc.Flow;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        var card = new Border { Margin = new Thickness(24), Padding = new Thickness(26, 24, 26, 20), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        card.SetResourceReference(Border.BackgroundProperty, "Card");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        card.Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.45, Color = Colors.Black };
        card.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not (Button or TextBox)) DragMove(); };
        _sub.Style = (Style)FindResource("Caption");
        var stack = new StackPanel();
        stack.Children.Add(_title); stack.Children.Add(_sub); stack.Children.Add(_body); stack.Children.Add(_buttons);
        card.Child = stack;
        Content = card;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        ShowSelect();
    }

    /// <summary>Runs a wizard step in the background; if it throws, say so and still undo and finish instead of freezing.</summary>
    async void Fire(Task t)
    {
        try { await t; }
        catch (Exception ex)
        {
            Log("wizard error: " + ex);
            try
            {
                if (_pendingUndo != null) { try { await _pendingUndo(); } catch { } _pendingUndo = null; }
                await EndScenarioAsync();
            }
            catch { }
            _records.Add(new StepRecord("wizard", "internal-error", "the wizard itself failed", null, false, ex.Message, ex.GetType().Name, null, null, "none", "failed: the wizard threw an exception", null, 0));
            _stop = true;
            Finish();
        }
    }

    static void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "OpenClevo-wizard.log"), $"{DateTime.Now:HH:mm:ss}  {line}\r\n"); } catch { }
    }
    // ------------------------------------------------------------------ helpers

    Button Btn(string text, Action click, bool primary = false, bool enabled = true)
    {
        var b = new Button { Content = text, Style = (Style)FindResource(primary ? "PrimaryBtn" : "Btn"), Margin = new Thickness(8, 0, 0, 0), IsEnabled = enabled, IsDefault = primary };
        b.Click += (_, _) => click();
        return b;
    }

    TextBlock Line(string text, bool muted = false, double size = 13)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 0, 0, 8) };
        if (muted) t.Style = (Style)FindResource("Caption");
        return t;
    }

    void Page(string title, string? sub = null)
    {
        _title.Text = title;
        _sub.Text = sub ?? "";
        _sub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
        _body.Children.Clear();
        _buttons.Children.Clear();
    }

    void Buttons(params Button[] bs) { foreach (var b in bs.Reverse()) _buttons.Children.Add(b); }   // first = main action, on the right

    // ------------------------------------------------------------------ page 1: what is wrong

    void ShowSelect()
    {
        Page(T("diag.title", "Compatibility check"),
             T("diag.intro", "Tick what you are experiencing. OpenClevo will run a few safe tests for each one and ask what you see. Every change is undone right after. At the end it saves a report you can send."));
        _boxes.Clear();
        foreach (var sc in _all)
        {
            var cb = new CheckBox { Style = (Style)FindResource("Toggle"), Margin = new Thickness(0, 5, 0, 5) };
            var tb = new TextBlock { FontSize = 13 };
            tb.Inlines.Add(new Run(sc.Title));
            var hint = new Run("   " + sc.Hint) { FontSize = 12 };
            hint.SetResourceReference(TextElement.ForegroundProperty, "Muted");
            tb.Inlines.Add(hint);
            cb.Content = tb;
            cb.Checked += (_, _) => RefreshStartButton();
            cb.Unchecked += (_, _) => RefreshStartButton();
            _boxes[sc.Id] = cb;
            _body.Children.Add(cb);
        }
        _body.Children.Add(Line(T("diag.note", "Anything else we should know? (optional)"), true));
        _note.Text = _noteText ?? "";
        _body.Children.Add(_note);
        RefreshStartButton();
    }

    void RefreshStartButton()
    {
        bool any = _boxes.Values.Any(b => b.IsChecked == true);
        _buttons.Children.Clear();
        Buttons(Btn(any ? T("diag.start", "Start") : T("diag.report_only", "Create report only"), StartClicked, primary: true),
                Btn(Loc.T("common.cancel"), Close));
    }

    void StartClicked()
    {
        _noteText = string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text.Trim();
        _selected = _all.Where(s => _boxes[s.Id].IsChecked == true).ToList();
        _si = 0; _ti = -1; _steps = [];
        if (_selected.Count == 0) { Finish(); return; }
        Fire(NextAsync());
    }

    // ------------------------------------------------------------------ page 2: the tests

    async Task NextAsync()
    {
        // finish the scenario that just ended
        if (_steps.Count > 0 && _ti >= _steps.Count - 1) await EndScenarioAsync();
        if (_steps.Count == 0 || _ti >= _steps.Count - 1)
        {
            if (_steps.Count > 0) _si++;
            _steps = [];
            if (_stop || _si >= _selected.Count) { Finish(); return; }
            var sc = _selected[_si];
            _pendingTeardown = sc.Teardown == null ? null : () => sc.Teardown(_ctx);
            if (sc.Setup != null) try { await sc.Setup(_ctx); } catch { }
            _steps = sc.Steps(_ctx);
            _ti = -1;
        }
        _ti++;
        ShowStepReady(_selected[_si], _steps[_ti]);
    }

    async Task EndScenarioAsync()
    {
        if (_pendingTeardown != null) { try { await _pendingTeardown(); } catch { } _pendingTeardown = null; }
        _ctx.Hw.ReloadSettings();
    }

    void ShowStepReady(DiagScenario sc, DiagStep st)
    {
        Page(sc.Title, $"{T("diag.step", "Step")} {_ti + 1} / {_steps.Count}");
        _body.Children.Add(Line(st.Text));
        if (st.Warning != null) _body.Children.Add(Line(st.Warning, true, 12));
        Buttons(Btn(T("diag.run", "Run"), () => Fire(RunStepAsync(sc, st)), primary: true),
                Btn(T("diag.skip", "Skip"), () => { Record(sc, st, new StepOutcome(true, "not run"), "skipped", null, 0); Fire(NextAsync()); }),
                Btn(T("diag.finish", "Finish now"), () => { _stop = true; Fire(StopNowAsync()); }));
    }

    async Task StopNowAsync() { await EndScenarioAsync(); Finish(); }

    async Task RunStepAsync(DiagScenario sc, DiagStep st)
    {
        Page(sc.Title, $"{T("diag.step", "Step")} {_ti + 1} / {_steps.Count}");
        _body.Children.Add(Line(st.Text));
        var status = Line(T("diag.running", "Running…"), true);
        _body.Children.Add(status);
        _ctx.Status = s => Dispatcher.Invoke(() => status.Text = T("diag.waiting", "Waiting…") + " " + s);
        var sw = Stopwatch.StartNew();
        StepOutcome o;
        try { o = await st.Run(_ctx); }
        catch (Exception ex) { o = new StepOutcome(false, "exception", ex.Message); }
        sw.Stop();
        Log($"step {st.Id}: ok={o.CommandOk} detail={o.Detail} readback={o.Readback} matches={o.ReadbackMatches} ({sw.ElapsedMilliseconds} ms)");
        if (st.Undo != null) _pendingUndo = () => st.Undo(_ctx);

        _body.Children.Clear();
        _body.Children.Add(Line(st.Text));
        _body.Children.Add(Line((o.CommandOk ? "" : T("diag.failed", "That did not work: ")) + o.Detail + (o.Error != null ? " (" + o.Error + ")" : "") + (o.Readback != null ? "  [" + o.Readback + "]" : ""), true, 12));
        _buttons.Children.Clear();
        if (st.Question != null && o.CommandOk)
        {
            _body.Children.Add(Line(st.Question, false, 14));
            Buttons(Btn(T("diag.yes", "Yes"), () => Fire(AnswerAsync(sc, st, o, "yes", sw.ElapsedMilliseconds)), primary: true),
                    Btn(T("diag.no", "No"), () => Fire(AnswerAsync(sc, st, o, "no", sw.ElapsedMilliseconds))),
                    Btn(T("diag.skip", "Skip"), () => Fire(AnswerAsync(sc, st, o, "skipped", sw.ElapsedMilliseconds))));
        }
        else
            Buttons(Btn(T("diag.next", "Next"), () => Fire(AnswerAsync(sc, st, o, st.Question != null ? "no" : "none", sw.ElapsedMilliseconds)), primary: true));
    }

    async Task AnswerAsync(DiagScenario sc, DiagStep st, StepOutcome o, string answer, long ms)
    {
        _buttons.Children.Clear();
        _body.Children.Add(Line(T("diag.restoring", "Putting everything back…"), true));
        bool? restored = null;
        if (st.Undo != null)
        {
            try { await st.Undo(_ctx); restored = true; } catch { restored = false; }
            _pendingUndo = null;
        }
        _ctx.Hw.ReloadSettings();
        Log($"answer {st.Id}: {answer}, restored={restored}");
        Record(sc, st, o, answer, restored, ms);
        await NextAsync();
    }

    void Record(DiagScenario sc, DiagStep st, StepOutcome o, string answer, bool? restored, long ms)
    {
        bool hasQ = st.Question != null;
        string consistency = Consistency(o, answer == "none" ? "yes" : answer, hasQ && answer != "none", st.ExpectYes?.Invoke(o));
        _records.Add(new StepRecord(sc.Id, st.Id, st.Text, st.Question, o.CommandOk, o.Detail, o.Error, o.Readback, o.ReadbackMatches, answer, consistency, restored, ms));
    }

    // ------------------------------------------------------------------ page 3: the report

    void Finish()
    {
        Page(T("diag.title", "Compatibility check"), T("diag.collecting", "Collecting the report…"));
        _body.Children.Add(Line(T("diag.collecting_note", "This takes a few seconds."), true));
        Fire(BuildReportAsync());
    }

    async Task BuildReportAsync()
    {
        string? path = null, error = null;
        var wiz = new WizardResult(_selected.Select(s => s.Id).ToList(), _noteText, _records.ToList());
        try { path = await Task.Run(() => Diagnostics.CreateReport(_version, wiz)); }
        catch (Exception ex) { error = ex.Message; }

        Page(T("diag.done_title", "Report ready"));
        if (path == null) { _body.Children.Add(Line(error ?? "?")); Buttons(Btn(Loc.T("common.ok"), Close, primary: true)); return; }
        int bad = _records.Count(r => r.Consistency.StartsWith("failed") || r.Consistency.StartsWith("inconsistent") || r.Consistency.StartsWith("no effect"));
        _body.Children.Add(Line(Loc.F("settings.diag_done", path)));
        if (_records.Count > 0)
            _body.Children.Add(Line(string.Format(T("diag.summary", "{0} tests run, {1} need a look."), _records.Count(r => r.Answer != "skipped"), bad), true, 12));
        Buttons(Btn(T("diag.open", "Show in folder"), () => { try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { } Close(); }, primary: true),
                Btn(T("diag.close", "Close"), Close));
    }

    // ------------------------------------------------------------------ closing: never leave a test half done

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closing && (_pendingUndo != null || _pendingTeardown != null))
        {
            e.Cancel = true;
            _closing = true;
            Fire(CleanUpAndCloseAsync());
        }
        base.OnClosing(e);
    }

    async Task CleanUpAndCloseAsync()
    {
        if (_pendingUndo != null) { try { await _pendingUndo(); } catch { } _pendingUndo = null; }
        if (_pendingTeardown != null) { try { await _pendingTeardown(); } catch { } _pendingTeardown = null; }
        try { _ctx.Hw.ReloadSettings(); } catch { }
        Close();
    }
}
