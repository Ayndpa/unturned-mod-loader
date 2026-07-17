using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnturnedModLoader.I18n;
using UnturnedModLoader.Services;
using UnturnedModLoader.Services.WinFsp;

namespace UnturnedModLoader.ViewModels;

/// <summary>
/// Shared view-model for the WinFsp download-mirror picker shown in both the onboarding
/// wizard and the settings window. Owns the speed test, the mirror option list (with
/// per-mirror latency labels), and the selected mirror that gets forwarded to the install
/// script. Embed one instance per host view-model and bind the host's view to it.
/// </summary>
public partial class WinFspMirrorPickerViewModel : ObservableObject
{
    [ObservableProperty]
    private WinFspMirrorOptionViewModel? _selectedOption;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string _testStatus = "";

    public ObservableCollection<WinFspMirrorOptionViewModel> Options { get; } = [];

    /// <summary>The mirror the user has selected (auto-set to the fastest after a test).</summary>
    public WinFspMirror SelectedMirror => SelectedOption?.Mirror ?? WinFspMirror.Direct;

    /// <summary>Resolves the MSI asset URL and runs the concurrent speed test, then picks the fastest.</summary>
    [RelayCommand]
    private async Task TestAsync()
    {
        if (IsTesting)
            return;

        IsTesting = true;
        TestStatus = L.Get(WinFspKeys.Testing);

        // Clear latencies so stale values don't show while re-testing.
        foreach (var opt in Options)
        {
            opt.LatencyMs = null;
            opt.TimedOut = false;
        }

        try
        {
            var assetUrl = await WinFspMirrorService.ResolveMsiAssetUrlAsync();
            var probes = await WinFspMirrorService.ProbeAsync(assetUrl);

            var byMirror = probes.ToDictionary(p => p.Mirror);
            foreach (var opt in Options)
            {
                if (byMirror.TryGetValue(opt.Mirror, out var probe))
                {
                    opt.TimedOut = probe.TimedOut;
                    opt.LatencyMs = probe.TimedOut ? null : (int?)probe.LatencyMs;
                }
            }

            var fastest = WinFspMirrorService.PickFastest(probes);
            SelectedOption = Options.FirstOrDefault(o => o.Mirror == fastest) ?? Options[0];
            TestStatus = "";
        }
        catch
        {
            TestStatus = "";
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>Rebuilds the option labels for the current locale (latencies are preserved).</summary>
    public void RefreshLabels()
    {
        var savedLatencies = Options.ToDictionary(o => o.Mirror, o => o.LatencyMs);
        var savedTimedOut = Options.ToDictionary(o => o.Mirror, o => o.TimedOut);
        Options.Clear();
        foreach (var mirror in WinFspMirrorService.All)
        {
            var opt = new WinFspMirrorOptionViewModel(mirror, LabelFor(mirror));
            if (savedLatencies.TryGetValue(mirror, out var lat))
                opt.LatencyMs = lat;
            if (savedTimedOut.TryGetValue(mirror, out var to))
                opt.TimedOut = to;
            Options.Add(opt);
        }

        // Preserve the current selection across the rebuild.
        var current = SelectedMirror;
        SelectedOption = Options.FirstOrDefault(o => o.Mirror == current) ?? Options[0];
    }

    private static string LabelFor(WinFspMirror mirror) => mirror switch
    {
        WinFspMirror.Direct => L.Get(WinFspKeys.MirrorDirect),
        WinFspMirror.GhProxyCom => L.Get(WinFspKeys.MirrorGhProxyCom),
        WinFspMirror.GhProxyOrg => L.Get(WinFspKeys.MirrorGhProxyOrg),
        WinFspMirror.V4GhProxyOrg => L.Get(WinFspKeys.MirrorV4),
        WinFspMirror.V6GhProxyOrg => L.Get(WinFspKeys.MirrorV6),
        WinFspMirror.CdnGhProxyOrg => L.Get(WinFspKeys.MirrorCdn),
        _ => mirror.ToString(),
    };
}

/// <summary>One row in the mirror picker: a mirror plus its (nullable) measured latency.</summary>
public partial class WinFspMirrorOptionViewModel : ObservableObject
{
    public WinFspMirror Mirror { get; }
    public string Label { get; }

    [ObservableProperty]
    private int? _latencyMs;

    [ObservableProperty]
    private bool _timedOut;

    public WinFspMirrorOptionViewModel(WinFspMirror mirror, string label)
    {
        Mirror = mirror;
        Label = label;
    }

    /// <summary>Display text: label plus latency or timeout suffix, e.g. "gh-proxy.com (推荐) (123ms)".</summary>
    public string DisplayText
    {
        get
        {
            if (TimedOut)
                return $"{Label} ({L.Get(WinFspKeys.TestTimeout)})";
            if (LatencyMs is null)
                return Label;
            return $"{Label} ({LatencyMs}ms)";
        }
    }

    partial void OnLatencyMsChanged(int? value) => OnPropertyChanged(nameof(DisplayText));
    partial void OnTimedOutChanged(bool value) => OnPropertyChanged(nameof(DisplayText));
}
