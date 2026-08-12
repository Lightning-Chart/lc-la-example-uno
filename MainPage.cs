using LightningChart.LA.Api;
using LightningChart.LA.WebView;
using Microsoft.UI.Xaml.Controls;

namespace LightningChartUnoExample;

public sealed class MainPage : Page
{
    private readonly WebView2 _webView = new();
    private WebViewTransport? _transport;

    public MainPage()
    {
        Content = _webView;
        Loaded += async (_, _) =>
        {
            _transport = await WebViewTransport.StartAsync();
            _webView.Source = new Uri(_transport.Uri.AbsoluteUri);
            await CreateChartAsync(_transport);
        };
    }

    private static async Task CreateChartAsync(WebViewTransport transport)
    {
        var licenseKey = Environment.GetEnvironmentVariable("LCJS_LICENSE_KEY")
            ?? throw new InvalidOperationException("Set LCJS_LICENSE_KEY before starting the example.");
        var context = new LclaContext(transport, new LclaLicense { Key = licenseKey, AppTitle = "LightningChart Uno Example" });
        var chart = await context.CreateChartAsync(new XYChartConfig
        {
            ContainerId = "lcla-root",
            Title = "Uno signal monitor",
            DataSets = [new DataSetConfig { Id = "signal", MaxSampleCount = 2_000_000, Columns = [new DataSetColumnConfig { Id = "value" }] }],
            Channels = [new ChannelConfig { Id = "value", DataSetId = "signal", Column = "value", Name = "Signal" }],
        });
        const int count = 1_000_000;
        var x = new double[count];
        var values = new double[count];
        for (var i = 0; i < count; i++) { x[i] = i * 0.001; values[i] = Math.Sin(x[i] * 8); }
        chart.SetData(new SetDataOptions { DataSetId = "signal", X = x, Columns = new Dictionary<string, double[]> { ["value"] = values } });
        chart.SetScrollStrategy(new SetScrollStrategyOptions { AxisX = ScrollStrategy.Scrolling });
        chart.SetDefaultAxisInterval(new SetDefaultAxisIntervalOptions { Axis = AxisTarget.X, Length = 10 });
        _ = StreamAsync(chart);
    }

    private static async Task StreamAsync(LclaChart chart)
    {
        var next = 1_000d;
        while (true)
        {
            const int count = 10_000;
            var x = new double[count];
            var values = new double[count];
            for (var i = 0; i < count; i++) { x[i] = next; values[i] = Math.Sin(next * 8); next += 0.001; }
            chart.AppendData(new AppendDataOptions { DataSetId = "signal", X = x, Columns = new Dictionary<string, double[]> { ["value"] = values } });
            await Task.Delay(16);
        }
    }
}
