using LightningChart.LA.Api;
using LightningChart.LA.WebView;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LightningChartUnoExample;

public sealed class MainPage : Page, IAsyncDisposable
{
    private const int HistoricalPointCount = 1_000_000;
    private const int StreamBatchSize = 10_000;
    private readonly WebView2 _webView = new();
    private readonly Button _loadButton = new() { Content = "Load historical data", IsEnabled = false };
    private readonly Button _streamButton = new() { Content = "Start real-time", IsEnabled = false };
    private readonly TextBlock _mode = new() { Text = "Historical", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _samples = new() { Text = "0 samples" };
    private readonly TextBlock _status = new() { Text = "Starting chart…", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
    private readonly CancellationTokenSource _lifetime = new();
    private WebViewTransport? _transport;
    private LclaContext? _context;
    private LclaChart? _chart;
    private CancellationTokenSource? _streamCancellation;
    private bool _created;
    private bool _isStreaming;
    private int _sampleCount;
    private double _nextX;

    public MainPage()
    {
        _loadButton.Click += async (_, _) => await LoadHistoricalDataAsync();
        _streamButton.Click += async (_, _) => await ToggleStreamingAsync();

        var header = new Grid { ColumnSpacing = 16, ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } } };
        header.Children.Add(new TextBlock { Text = "Signal monitor", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_loadButton, 1);
        Grid.SetColumn(_streamButton, 2);
        header.Children.Add(_loadButton);
        header.Children.Add(_streamButton);

        var summary = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 32 };
        summary.Children.Add(Metric("Mode", _mode));
        summary.Children.Add(Metric("Samples processed", _samples));
        summary.Children.Add(Metric("Batch size", new TextBlock { Text = "10,000" }));

        var layout = new Grid { Padding = new Thickness(24), RowSpacing = 16, RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }, new RowDefinition { Height = GridLength.Auto } } };
        layout.Children.Add(header);
        Grid.SetRow(summary, 1);
        layout.Children.Add(summary);
        Grid.SetRow(_webView, 2);
        layout.Children.Add(_webView);
        var footer = new StackPanel { Spacing = 4 };
        footer.Children.Add(_status);
        footer.Children.Add(_error);
        Grid.SetRow(footer, 3);
        layout.Children.Add(footer);
        Content = layout;

        _webView.NavigationCompleted += async (_, args) => await OnNavigationCompletedAsync(args.IsSuccess, args.WebErrorStatus.ToString());
        Loaded += async (_, _) => await InitializeAsync();
        Unloaded += async (_, _) => await DisposeAsync();
    }

    private static StackPanel Metric(string label, TextBlock value)
    {
        var metric = new StackPanel { Spacing = 2 };
        metric.Children.Add(new TextBlock { Text = label, Opacity = 0.65 });
        metric.Children.Add(value);
        return metric;
    }

    private async Task InitializeAsync()
    {
        try
        {
            _transport = await WebViewTransport.StartAsync(_lifetime.Token);
            _webView.Source = new Uri(_transport.Uri.AbsoluteUri);
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task OnNavigationCompletedAsync(bool succeeded, string errorStatus)
    {
        if (!succeeded) { ShowError(new InvalidOperationException($"The chart page could not load: {errorStatus}.")); return; }
        if (_created || _transport is null) return;
        _created = true;
        try
        {
            var licenseKey = Environment.GetEnvironmentVariable("LCJS_LICENSE_KEY") ?? throw new InvalidOperationException("Set LCJS_LICENSE_KEY before starting the example.");
            _context = new LclaContext(_transport, new LclaLicense { Key = licenseKey });
            _context.ErrorOccurred += (_, eventArgs) => DispatcherQueue.TryEnqueue(() => ShowError(eventArgs.Exception));
            _chart = await _context.CreateChartAsync(new XYChartConfig
            {
                ContainerId = "lcla-root", Title = "High-rate signal monitor", AnimationsEnabled = false,
                DataSets = [new DataSetConfig { Id = "signals", MaxSampleCount = 2_000_000, Columns = [new DataSetColumnConfig { Id = "raw" }, new DataSetColumnConfig { Id = "filtered" }] }],
                Channels = [new ChannelConfig { Id = "raw", DataSetId = "signals", Column = "raw", Name = "Raw signal", Color = "#9E9E9E" }, new ChannelConfig { Id = "filtered", DataSetId = "signals", Column = "filtered", Name = "Filtered", Color = "#00A6FF" }],
            });
            _loadButton.IsEnabled = true;
            _streamButton.IsEnabled = true;
            await LoadHistoricalDataAsync();
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task LoadHistoricalDataAsync()
    {
        if (_chart is null) return;
        await StopStreamingAsync();
        _loadButton.IsEnabled = false;
        try
        {
            var data = await Task.Run(CreateHistoricalData, _lifetime.Token);
            _chart.SetScrollStrategy(new SetScrollStrategyOptions { AxisX = ScrollStrategy.Fitting });
            _chart.SetData(new SetDataOptions { DataSetId = "signals", X = data.X, Columns = data.Columns });
            _chart.SetAxisInterval(new SetAxisIntervalOptions { Axis = AxisTarget.X, Start = 980, End = 1_000 });
            _sampleCount = HistoricalPointCount;
            _nextX = data.X[^1];
            UpdateSummary("Historical data ready");
        }
        catch (Exception exception) { ShowError(exception); }
        finally { _loadButton.IsEnabled = _chart is not null; }
    }

    private async Task ToggleStreamingAsync()
    {
        if (_isStreaming) await StopStreamingAsync();
        else StartStreaming();
    }

    private void StartStreaming()
    {
        if (_chart is null || _isStreaming) return;
        _chart.SetScrollStrategy(new SetScrollStrategyOptions { AxisX = ScrollStrategy.Scrolling });
        _chart.SetDefaultAxisInterval(new SetDefaultAxisIntervalOptions { Axis = AxisTarget.X, Length = 5 });
        _isStreaming = true;
        _streamButton.Content = "Stop real-time";
        UpdateSummary("Real-time updates active");
        _streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = StreamAsync(_streamCancellation.Token);
    }

    private async Task StopStreamingAsync()
    {
        if (!_isStreaming) return;
        _isStreaming = false;
        _streamCancellation?.Cancel();
        _streamCancellation?.Dispose();
        _streamCancellation = null;
        _streamButton.Content = "Start real-time";
        UpdateSummary("Real-time updates paused");
        await Task.CompletedTask;
    }

    private async Task StreamAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _chart is not null)
            {
                var data = CreateStreamBatch();
                _chart.AppendData(new AppendDataOptions { DataSetId = "signals", X = data.X, Columns = data.Columns });
                _sampleCount += StreamBatchSize;
                if (_sampleCount % 100_000 == 0) DispatcherQueue.TryEnqueue(() => UpdateSummary("Real-time updates active"));
                await Task.Delay(16, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { DispatcherQueue.TryEnqueue(() => ShowError(exception)); }
    }

    private static (double[] X, Dictionary<string, double[]> Columns) CreateHistoricalData()
    {
        var x = new double[HistoricalPointCount]; var raw = new double[HistoricalPointCount]; var filtered = new double[HistoricalPointCount]; var random = new Random(42);
        for (var i = 0; i < HistoricalPointCount; i++) { var time = i * 0.001; x[i] = time; filtered[i] = Math.Sin(time * 10); raw[i] = filtered[i] + Math.Sin(time * 77) * 0.35 + random.NextDouble() * 0.35 - 0.175; }
        return (x, new Dictionary<string, double[]> { ["raw"] = raw, ["filtered"] = filtered });
    }

    private (double[] X, Dictionary<string, double[]> Columns) CreateStreamBatch()
    {
        var x = new double[StreamBatchSize]; var raw = new double[StreamBatchSize]; var filtered = new double[StreamBatchSize];
        for (var i = 0; i < StreamBatchSize; i++) { var time = _nextX; x[i] = time; filtered[i] = Math.Sin(time * 10); raw[i] = filtered[i] + Math.Sin(time * 77) * 0.35; _nextX += 0.001; }
        return (x, new Dictionary<string, double[]> { ["raw"] = raw, ["filtered"] = filtered });
    }

    private void UpdateSummary(string status)
    {
        _mode.Text = _isStreaming ? "Real-time" : "Historical";
        _samples.Text = FormatCount(_sampleCount);
        _status.Text = status;
        _error.Visibility = Visibility.Collapsed;
    }

    private static string FormatCount(int count) => count >= 1_000_000 ? $"{count / 1_000_000d:0.0}M samples" : $"{count / 1_000d:0}k samples";

    private void ShowError(Exception exception)
    {
        _status.Text = "The chart could not be started.";
        _error.Text = exception.Message;
        _error.Visibility = Visibility.Visible;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await StopStreamingAsync();
        if (_chart is not null) await _chart.DisposeAsync();
        if (_context is not null) await _context.DisposeAsync();
        if (_transport is not null) await _transport.DisposeAsync();
        _lifetime.Dispose();
    }
}
