using System.Diagnostics;
using System.Globalization;
using LightningChart.LA.Api;
using LightningChart.LA.WebView;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace LightningChartUnoExample;

public sealed partial class MainPage : Page, IAsyncDisposable
{
    private const double WindowMs = 20_000;
    private const string DataSetId = "flight";
    private const string RouteDataSetId = "route";
    private readonly SolidColorBrush PageBackground;
    private readonly SolidColorBrush BorderColor;
    private readonly SolidColorBrush Muted;
    private readonly SolidColorBrush AltitudeColor;
    private readonly Rectangle _altitudeMarker;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch _clock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private WebViewTransport? _transport;
    private LclaContext? _context;
    private LclaChart? _chart;
    private WebViewTransport? _routeTransport;
    private LclaContext? _routeContext;
    private LclaChart? _routeChart;
    private FlightData? _flight;
    private int _nextIndex;
    private bool _initialized;
    private bool _telemetryReady;
    private bool _routeReady;
    private bool _playing;
    private bool _disposed;

    public MainPage()
    {
        InitializeComponent();
        PageBackground = (SolidColorBrush)Resources["PageBackground"];
        BorderColor = (SolidColorBrush)Resources["BorderColor"];
        Muted = (SolidColorBrush)Resources["Muted"];
        AltitudeColor = (SolidColorBrush)Resources["AltitudeColor"];
        Background = PageBackground;
        _altitudeMarker = new Rectangle { Width = 58, Height = 4, Fill = AltitudeColor };
        _altitudeGaugeHost.Children.Add(BuildAltitudeGauge());
        _playButton.Click += (_, _) => ToggleReplay();
        _timer.Tick += (_, _) => AppendTick();

        _webView.NavigationCompleted += async (_, args) =>
            await OnNavigationCompletedAsync(args.IsSuccess, args.WebErrorStatus.ToString());

        _routeWebView.NavigationCompleted += async (_, args) =>
            await OnRouteNavigationCompletedAsync(args.IsSuccess, args.WebErrorStatus.ToString());

        Loaded += async (_, _) => await InitializeAsync();
        Unloaded += async (_, _) => await DisposeAsync();
    }

    private Viewbox BuildAltitudeGauge()
    {
        var scale = new Canvas { Width = 100, Height = 270, Background = PageBackground };
        var axis = new Rectangle { Width = 3, Height = 238, Fill = BorderColor };
        Canvas.SetLeft(axis, 20);
        Canvas.SetTop(axis, 16);
        scale.Children.Add(axis);

        foreach (var tick in new[] { 45, 30, 15, 0, -5 })
        {
            var y = 16 + (45 - tick) / 50.0 * 238;
            scale.Children.Add(new Line { X1 = 12, X2 = 32, Y1 = y, Y2 = y, Stroke = Muted, StrokeThickness = 1 });
            var label = new TextBlock { Text = $"{tick} m", FontSize = 11, Foreground = Muted };
            Canvas.SetLeft(label, 42);
            Canvas.SetTop(label, y - 9);
            scale.Children.Add(label);
        }

        scale.Children.Add(_altitudeMarker);

        return new Viewbox { Child = scale, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
    }

    private async Task InitializeAsync()
    {
        if (_initialized || _disposed) return;
        _initialized = true;

        try
        {
            _flight = await Task.Run(FlightData.Load, _lifetime.Token);
            if (_disposed) return;
            _recordingName.Text = $"DRONE FLIGHT  /  {_flight.Name}";
            _transport = await WebViewTransport.StartAsync(_lifetime.Token);
            _routeTransport = await WebViewTransport.StartAsync(_lifetime.Token);
            _webView.Source = _transport.Uri;
            _routeWebView.Source = _routeTransport.Uri;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception) { if (!_disposed) ShowError(exception); }
    }

    private LclaContext CreateContext(WebViewTransport transport)
    {
        var licenseKey = Environment.GetEnvironmentVariable("LCJS_LICENSE_KEY")
            ?? throw new InvalidOperationException("Set LCJS_LICENSE_KEY before starting the example.");

        var context = new LclaContext(transport, new LclaLicense { Key = licenseKey });

        context.ErrorOccurred += (_, args) =>
        {
            // The host retries a failed long poll.
            if (args.Exception.Message.Contains("WebView failed while waiting for a chart command: TypeError", StringComparison.Ordinal))
            {
                Debug.WriteLine(args.Exception);
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed) ShowError(args.Exception);
            });
        };

        return context;
    }

    private async Task OnNavigationCompletedAsync(bool succeeded, string errorStatus)
    {
        if (_disposed) return;

        if (!succeeded)
        {
            ShowError(new InvalidOperationException($"The telemetry page could not load: {errorStatus}."));
            return;
        }

        if (_context is not null || _transport is null || _flight is null)
            return;

        try
        {
            _context = CreateContext(_transport);

            _chart = await _context.CreateChartAsync(
                new XYChartConfig
                {
                    ContainerId = "lcla-root",
                    Title = "",
                },
                _lifetime.Token);

            if (_disposed) return;

            _context.ConfigureDataSets(
            [
                new DataSetConfig
                {
                    Id = DataSetId,
                    XDataPattern = DataPattern.Progressive,
                    MaxSampleCount = 50_000,
                    Columns =
                    [
                        new DataSetColumnConfig { Id = "altitude" },
                        new DataSetColumnConfig { Id = "speed" },
                        new DataSetColumnConfig { Id = "climb" },
                    ],
                },
            ]);

            _chart.ConfigureChannels(
            [
                new ChannelConfig
                {
                    Id = "altitude",
                    DataSetId = DataSetId,
                    Column = "altitude",
                    Name = "Relative altitude (m)",
                    Color = "#8BF584",
                    StackIndex = 0,
                },
                new ChannelConfig
                {
                    Id = "speed",
                    DataSetId = DataSetId,
                    Column = "speed",
                    Name = "Horizontal speed (km/h)",
                    Color = "#FFC56A",
                    StackIndex = -1,
                },
                new ChannelConfig
                {
                    Id = "climb",
                    DataSetId = DataSetId,
                    Column = "climb",
                    Name = "Vertical speed (m/s)",
                    Color = "#BBA6F2",
                    StackIndex = -2,
                },
            ]);

            _chart.SetScrollStrategy(new SetScrollStrategyOptions { AxisX = ScrollStrategy.Scrolling });
            _telemetryReady = true;
            StartWhenReady();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception) { if (!_disposed) ShowError(exception); }
    }

    private async Task OnRouteNavigationCompletedAsync(bool succeeded, string errorStatus)
    {
        if (_disposed) return;

        if (!succeeded)
        {
            ShowError(new InvalidOperationException($"The route page could not load: {errorStatus}."));
            return;
        }

        if (_routeContext is not null || _routeTransport is null || _flight is null)
        {
            return;
        }

        try
        {
            _routeContext = CreateContext(_routeTransport);

            _routeChart = await _routeContext.CreateChartAsync(new XYChartConfig
            {
                ContainerId = "lcla-root",
                Title = "",
                DataSets =
                [
                    new DataSetConfig
                    {
                        Id = RouteDataSetId,
                        XDataPattern = DataPattern.None,
                        MaxSampleCount = _flight.Longitude.Length,
                        Columns = 
                        [ 
                            new DataSetColumnConfig { Id = "latitude" }, 
                            new DataSetColumnConfig { Id = "position" } 
                        ],
                    }
                ],
                Channels = 
                [
                    new ChannelConfig
                    {
                        Id = "route",
                        DataSetId = RouteDataSetId,
                        Column = "latitude",
                        Name = "Flight route",
                        Type = ChannelType.Line,
                        Color = "#8BF584",
                    },
                    new ChannelConfig
                    {
                        Id = "position",
                        DataSetId = RouteDataSetId,
                        Column = "position",
                        Name = "Current position",
                        Type = ChannelType.Scatter,
                        Color = "#FFC56A",
                    }
                ]
            },
            _lifetime.Token);

            if (_disposed) return;

            _routeChart.SetDefaultAxisInterval(new SetDefaultAxisIntervalOptions { Axis = AxisTarget.X, Start = 28.7548, End = 28.7597 });
            _routeChart.SetDefaultAxisInterval(new SetDefaultAxisIntervalOptions { Axis = AxisTarget.Y, Start = 62.39327, End = 62.39552 });
            _routeReady = true;
            StartWhenReady();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception) { if (!_disposed) ShowError(exception); }
    }

    private void StartWhenReady()
    {
        if (_disposed || !_telemetryReady || !_routeReady || _playButton.IsEnabled)
            return;

        ResetReplay();
        _playButton.IsEnabled = true;
        StartReplay();
    }

    private void ToggleReplay()
    {
        if (_playing) PauseReplay();
        else StartReplay();
    }

    private void StartReplay()
    {
        if (_disposed || _chart is null || _context is null || _flight is null || !_telemetryReady || !_routeReady || _playing)
            return;

        _chart.SetDefaultAxisInterval(new SetDefaultAxisIntervalOptions { Axis = AxisTarget.X, Length = WindowMs });
        _playing = true;
        _playButton.Content = "Pause";
        _clock.Start();
        AppendTick();
        if (_playing) _timer.Start();
    }

    private void PauseReplay()
    {
        _timer.Stop();
        _clock.Stop();
        _playing = false;
        _playButton.Content = "Play";
    }

    private void ResetReplay()
    {
        if (_chart is null || _context is null || _flight is null)
            return;

        _context.ClearData(new ClearDataOptions { DataSetId = DataSetId });
        _chart.SetDefaultAxisInterval(new SetDefaultAxisIntervalOptions { Axis = AxisTarget.X, Length = WindowMs });
        _clock.Reset();
        if (_playing) _clock.Start();
        _nextIndex = 0;
        UpdateInstruments(0);
    }

    private void AppendTick()
    {
        if (_chart is null || _context is null || _flight is null || !_playing)
            return;

        try
        {
            var endTime = _clock.Elapsed.TotalMilliseconds;
            var start = _nextIndex;

            while (_nextIndex < _flight.TimeMs.Length && _flight.TimeMs[_nextIndex] <= endTime)
            {
                _nextIndex++;
            }

            if (_nextIndex > start)
            {
                _context.AppendData(new AppendDataOptions
                {
                    DataSetId = DataSetId,
                    X = _flight.TimeMs[start.._nextIndex],
                    Columns = new Dictionary<string, double[]>
                    {
                        ["altitude"] = _flight.Altitude[start.._nextIndex],
                        ["speed"] = _flight.Speed[start.._nextIndex],
                        ["climb"] = _flight.Climb[start.._nextIndex],
                    },
                });

                UpdateInstruments(_nextIndex - 1);
            }

            if (endTime >= _flight.TimeMs[^1]) ResetReplay();
        }
        catch (Exception exception)
        {
            PauseReplay();
            ShowError(exception);
        }
    }

    private void UpdateInstruments(int index)
    {
        var flight = _flight!;
        _altitude.Text = flight.Altitude[index].ToString("0.0", CultureInfo.InvariantCulture);
        _speed.Text = flight.Speed[index].ToString("0.0", CultureInfo.InvariantCulture);
        _climb.Text = flight.Climb[index].ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        _distance.Text = flight.Distance[index].ToString("0", CultureInfo.InvariantCulture) + " m";
        _latitude.Text = flight.Latitude[index].ToString("F6", CultureInfo.InvariantCulture) + "°";
        _longitude.Text = flight.Longitude[index].ToString("F6", CultureInfo.InvariantCulture) + "°";
        _cameraMetadata.Text = FormattableString.Invariant(
            $"ISO {flight.Iso}  ·  1/{flight.Shutter[index]} s  ·  f/{flight.Aperture:0.0}\nEV {flight.Ev:+0.0;-0.0;0.0}  ·  {flight.FocalLengthRaw / 10.0:0.0} mm  ·  {flight.DigitalZoomRaw / 10000.0:0.0}×  ·  CT {flight.Ct[index]:0}");
        _elapsed.Text =
            $"{TimeSpan.FromMilliseconds(flight.TimeMs[index]):mm\\:ss} / {TimeSpan.FromMilliseconds(flight.TimeMs[^1]):mm\\:ss}";
        Canvas.SetLeft(_altitudeMarker, 4);
        Canvas.SetTop(_altitudeMarker, 14 + (45 - Math.Clamp(flight.Altitude[index], -5, 45)) / 50 * 238);
        UpdateRoutePosition(index);
    }

    private void UpdateRoutePosition(int index)
    {
        if (!_routeReady || _routeContext is null || _flight is null)
            return;

        var count = index + 1;
        var position = new double[count];
        Array.Fill(position, double.NaN);
        position[index] = _flight.Latitude[index];

        _routeContext.SetData(new SetDataOptions
        {
            DataSetId = RouteDataSetId,
            X = _flight.Longitude[..count],
            Columns = new Dictionary<string, double[]>
            {
                ["latitude"] = _flight.Latitude[..count],
                ["position"] = position,
            },
        });
    }

    private void ShowError(Exception exception)
    {
        Debug.WriteLine(exception);
        _error.Text = exception.Message;
        _error.Visibility = Visibility.Visible;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _clock.Stop();
        _lifetime.Cancel();
        // Each context disposes the charts it owns.
        if (_context is not null) await _context.DisposeAsync();
        if (_routeContext is not null) await _routeContext.DisposeAsync();
        if (_transport is not null) await _transport.DisposeAsync();
        if (_routeTransport is not null) await _routeTransport.DisposeAsync();
        _lifetime.Dispose();
    }

    private sealed class FlightData
    {
        public required double[] TimeMs { get; init; }
        public required double[] Altitude { get; init; }
        public required double[] Speed { get; init; }
        public required double[] Climb { get; init; }
        public required double[] Distance { get; init; }
        public required double[] Latitude { get; init; }
        public required double[] Longitude { get; init; }
        public required int[] Shutter { get; init; }
        public required double[] Ct { get; init; }
        public required int Iso { get; init; }
        public required double Aperture { get; init; }
        public required double Ev { get; init; }
        public required int FocalLengthRaw { get; init; }
        public required int DigitalZoomRaw { get; init; }
        public required string Name { get; init; }

        public static FlightData Load()
        {
            using var stream = typeof(MainPage).Assembly.GetManifestResourceStream("DroneFlightData")
                ?? throw new InvalidOperationException("Drone data was not found.");

            using var reader = new StreamReader(stream);
            var header = reader.ReadLine()!.Split(',');
            int Column(string name) => Array.IndexOf(header, name);
            var timeColumn = Column("TIMECODE");
            var altitudeColumn = Column("ALTITUDE");
            var speedColumn = Column("SPEED.TWOD");
            var climbColumn = Column("SPEED.VERTICAL");
            var distanceColumn = Column("DISTANCE");
            var latitudeColumn = Column("GPS.LATITUDE");
            var longitudeColumn = Column("GPS.LONGITUDE");
            var shutterColumn = Column("SHUTTER");
            var ctColumn = Column("CT");
            var isoColumn = Column("ISO");
            var apertureColumn = Column("FNUM");
            var evColumn = Column("EV");
            var focalColumn = Column("FOCAL_LEN");
            var zoomColumn = Column("DZOOM_RATIO");
            var nameColumn = Column("NAME");
            var time = new List<double>();
            var altitude = new List<double>();
            var speed = new List<double>();
            var climb = new List<double>();
            var distance = new List<double>();
            var latitude = new List<double>();
            var longitude = new List<double>();
            var shutter = new List<int>();
            var ct = new List<double>();
            var iso = 0;
            var aperture = 0.0;
            var ev = 0.0;
            var focal = 0;
            var zoom = 0;
            var name = "";

            while (reader.ReadLine() is { } line)
            {
                var cells = line.Split(',');
                if (time.Count == 0)
                {
                    iso = int.Parse(cells[isoColumn], CultureInfo.InvariantCulture);
                    aperture = double.Parse(cells[apertureColumn], CultureInfo.InvariantCulture);
                    ev = double.Parse(cells[evColumn], CultureInfo.InvariantCulture);
                    focal = int.Parse(cells[focalColumn], CultureInfo.InvariantCulture);
                    zoom = int.Parse(cells[zoomColumn], CultureInfo.InvariantCulture);
                    name = cells[nameColumn];
                }
                time.Add(TimeSpan.Parse(cells[timeColumn], CultureInfo.InvariantCulture).TotalMilliseconds);
                altitude.Add(double.Parse(cells[altitudeColumn], CultureInfo.InvariantCulture));
                speed.Add(double.Parse(cells[speedColumn], CultureInfo.InvariantCulture));
                climb.Add(double.Parse(cells[climbColumn], CultureInfo.InvariantCulture) / 3.6);
                distance.Add(double.Parse(cells[distanceColumn], CultureInfo.InvariantCulture));
                latitude.Add(double.Parse(cells[latitudeColumn], CultureInfo.InvariantCulture));
                longitude.Add(double.Parse(cells[longitudeColumn], CultureInfo.InvariantCulture));
                shutter.Add(int.Parse(cells[shutterColumn], CultureInfo.InvariantCulture));
                ct.Add(double.Parse(cells[ctColumn], CultureInfo.InvariantCulture));
            }

            return new FlightData
            {
                TimeMs = time.ToArray(),
                Altitude = altitude.ToArray(),
                Speed = speed.ToArray(),
                Climb = climb.ToArray(),
                Distance = distance.ToArray(),
                Latitude = latitude.ToArray(),
                Longitude = longitude.ToArray(),
                Shutter = shutter.ToArray(),
                Ct = ct.ToArray(),
                Iso = iso,
                Aperture = aperture,
                Ev = ev,
                FocalLengthRaw = focal,
                DigitalZoomRaw = zoom,
                Name = name,
            };
        }
    }
}