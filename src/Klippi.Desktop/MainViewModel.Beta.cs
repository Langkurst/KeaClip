using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klippi.Core;

namespace Klippi.Desktop;

public enum BetaResult { NotTried, Works, Problem }

/// <summary>One thing a beta tester is asked to try, and their answer. Pressing the chosen answer again clears it.</summary>
public sealed class BetaCheck : ObservableObject
{
    private BetaResult _result;

    public BetaCheck(int number, string key, string title, string hint)
    {
        Number = number; Key = key; Title = title; Hint = hint;
        MarkWorksCommand = new RelayCommand(() => Result = Result == BetaResult.Works ? BetaResult.NotTried : BetaResult.Works);
        MarkProblemCommand = new RelayCommand(() => Result = Result == BetaResult.Problem ? BetaResult.NotTried : BetaResult.Problem);
    }

    public int Number { get; }
    public string Key { get; }
    public string Title { get; }
    public string Hint { get; }
    public BetaResult Result
    {
        get => _result;
        set { if (SetProperty(ref _result, value)) { OnPropertyChanged(nameof(Works)); OnPropertyChanged(nameof(Problem)); } }
    }
    public bool Works => Result == BetaResult.Works;
    public bool Problem => Result == BetaResult.Problem;
    public IRelayCommand MarkWorksCommand { get; }
    public IRelayCommand MarkProblemCommand { get; }
}

// Beta builds only: a short checklist of what matters most on someone else's PC, a notes box, and a plain-text
// report the tester can read before emailing it. Answers survive restarts (beta-test.json in the data folder).
public sealed partial class MainViewModel
{
    private const string BetaStateFileName = "beta-test.json";
    private ObservableCollection<BetaCheck>? _betaChecks;
    private string _betaNotes = "";
    private string? _betaReportPath;
    private bool _loadingBetaState;
    private IAsyncRelayCommand? _saveBetaReport, _copyContactEmail;
    private IRelayCommand? _showBetaReport;

    public bool IsBeta => _services.BetaTest;
    public bool IsBetaTest => SelectedTab == 5;
    public string ContactEmail => AppIdentity.ContactEmail;
    /// <summary>Set by the window (it knows the monitor), so the report can say how the screen is scaled.</summary>
    public double DisplayScaling { get; set; } = 1;
    public string ScreenSize { get; set; } = "";

    public ObservableCollection<BetaCheck> BetaChecks => _betaChecks ??= CreateBetaChecks();
    public string BetaNotes
    {
        get => _betaNotes;
        set { if (SetProperty(ref _betaNotes, value ?? "")) SaveBetaState(); }
    }
    public int BetaTriedCount => BetaChecks.Count(check => check.Result != BetaResult.NotTried);
    public string BetaProgress => BetaTriedCount + " of " + BetaChecks.Count + " tried";
    public string BetaProgressShort => BetaTriedCount + "/" + BetaChecks.Count;
    public bool HasBetaReport => _betaReportPath is not null;
    public string BetaReportSaved => _betaReportPath is null ? "" : "Saved " + Path.GetFileName(_betaReportPath) + (_services.ReportsRoot is null ? "" : " to your Desktop") + ".";

    public IAsyncRelayCommand SaveBetaReportCommand => _saveBetaReport ??= new AsyncRelayCommand(SaveBetaReportAsync);
    public IRelayCommand ShowBetaReportCommand => _showBetaReport ??= new RelayCommand(() => { if (_betaReportPath is { } path) _services.Platform.RevealFile(path); });
    public IAsyncRelayCommand CopyContactEmailCommand => _copyContactEmail ??= new AsyncRelayCommand(() => CopyToClipboardAsync(AppIdentity.ContactEmail, "Email address copied."));

    private string BetaStatePath => Path.Combine(_services.DataRoot, BetaStateFileName);

    private ObservableCollection<BetaCheck> CreateBetaChecks()
    {
        ObservableCollection<BetaCheck> checks =
        [
            new(1, "setup", "Let it set up", "The first time, KeaClip fetches FFmpeg and yt-dlp from their makers. Did it finish on its own?"),
            new(2, "download", "Save a video", "Copy a YouTube link, paste it on the Download page and press Download."),
            new(3, "play", "Play it", "Open the video in the Editor. Do the picture and sound play smoothly?"),
            new(4, "trim", "Trim and shrink", "Drag the ends to keep about 10 seconds, pick Fit for Discord, then Export."),
            new(5, "share", "Share it", "Press Share on the clip, then open the link on your phone."),
            new(6, "look", "Looks right", "Anything cut off, blurry or hard to read? Try Limestone day in Settings too."),
        ];
        foreach (var check in checks) check.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BetaCheck.Result)) BetaAnswerChanged(); };
        return checks;
    }

    private void BetaAnswerChanged()
    {
        OnPropertyChanged(nameof(BetaTriedCount)); OnPropertyChanged(nameof(BetaProgress)); OnPropertyChanged(nameof(BetaProgressShort));
        SaveBetaState();
    }

    /// <summary>The first start of a beta build opens the checklist; later starts load the answers given so far.</summary>
    private void OpenBetaTestOnFirstStart()
    {
        if (!IsBeta) return;
        if (!LoadBetaState()) { SelectedTab = 5; SaveBetaState(); }
    }

    private bool LoadBetaState()
    {
        try
        {
            if (!File.Exists(BetaStatePath)) return false;
            using var json = JsonDocument.Parse(File.ReadAllText(BetaStatePath));
            _loadingBetaState = true;
            if (json.RootElement.TryGetProperty("answers", out var answers) && answers.ValueKind == JsonValueKind.Object)
                foreach (var check in BetaChecks)
                    if (answers.TryGetProperty(check.Key, out var answer) && Enum.TryParse<BetaResult>(answer.GetString(), out var result)) check.Result = result;
            if (json.RootElement.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.String) BetaNotes = notes.GetString() ?? "";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { CaptureDiagnostics(error); return true; }
        finally { _loadingBetaState = false; }
    }

    private void SaveBetaState()
    {
        if (_loadingBetaState || !IsBeta) return;
        try
        {
            Directory.CreateDirectory(_services.DataRoot);
            var state = new { answers = BetaChecks.ToDictionary(check => check.Key, check => check.Result.ToString()), notes = BetaNotes };
            File.WriteAllText(BetaStatePath, JsonSerializer.Serialize(state));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { CaptureDiagnostics(error); }
    }

    /// <summary>Plain text, so the tester can open it and see exactly what is sent. Personal paths are removed.</summary>
    public string BuildBetaReport()
    {
        static string Label(BetaResult result) => result switch { BetaResult.Works => "Works", BetaResult.Problem => "PROBLEM", _ => "Not tried" };
        var report = new StringBuilder()
            .AppendLine("KeaClip beta report")
            .AppendLine("Saved: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture))
            .AppendLine();
        foreach (var check in BetaChecks) report.AppendLine($"{check.Number}. {check.Title}: {Label(check.Result)}");
        report.AppendLine().AppendLine("Notes:").AppendLine(string.IsNullOrWhiteSpace(BetaNotes) ? "(none)" : BetaNotes.Trim()).AppendLine()
            .AppendLine("This PC:")
            .AppendLine("Display scaling: " + Math.Round(DisplayScaling * 100).ToString(CultureInfo.InvariantCulture) + "%" + (ScreenSize.Length > 0 ? ", screen " + ScreenSize : ""))
            .AppendLine("Memory: " + (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB, " + Environment.ProcessorCount + " logical processors")
            .AppendLine("Theme: " + Theme + ", reduced motion " + (EffectiveReducedMotion ? "on" : "off"))
            .AppendLine()
            .AppendLine("App, Windows and tools:").AppendLine(VersionDiagnostics);
        if (HasRecordedError) report.AppendLine().AppendLine("Last error:").AppendLine(Diagnostics);
        return MediaValidation.RedactDiagnostics(report.ToString()).TrimEnd() + Environment.NewLine;
    }

    private async Task SaveBetaReportAsync()
    {
        try
        {
            var folder = _services.ReportsRoot ?? _services.DataRoot;
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "KeaClip beta report " + DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture) + ".txt");
            await File.WriteAllTextAsync(path, BuildBetaReport());
            _betaReportPath = path;
            OnPropertyChanged(nameof(HasBetaReport)); OnPropertyChanged(nameof(BetaReportSaved));
            Status = BetaReportSaved + " Please email it to " + AppIdentity.ContactEmail + ".";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { CaptureDiagnostics(error); Status = "The report could not be saved. " + FriendlyError(error); }
    }
}
